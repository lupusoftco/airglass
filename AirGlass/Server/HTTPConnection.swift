import Foundation
import Network

struct HTTPRequest {
    var method: String
    /// Path without query string.
    var path: String
    /// Header names are lowercased.
    var headers: [String: String]

    init?(head: [UInt8]) {
        guard let text = String(bytes: head, encoding: .utf8) else { return nil }
        var lines = text.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ")
        guard requestLine.count == 3, requestLine[2].hasPrefix("HTTP/1.") else { return nil }

        method = String(requestLine[0])
        path = String(requestLine[1].split(separator: "?", maxSplits: 1).first ?? "")
        headers = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            headers[name] = value
        }
    }
}

/// Reads one HTTP/1.1 request head from a TCP connection and writes a
/// response, or hands the socket off for a WebSocket upgrade.
/// All methods must be called on `queue`.
final class HTTPConnection {
    static let maxHeadSize = 8 * 1024
    static let requestTimeout: TimeInterval = 10

    /// The request plus any bytes received after its head.
    var onRequest: ((HTTPRequest, [UInt8]) -> Void)?
    var onClose: (() -> Void)?

    private let connection: NWConnection
    private let queue: DispatchQueue
    private var buffer: [UInt8] = []
    private var hasRequest = false
    private var isFinished = false

    var remoteDescription: String {
        String(describing: connection.endpoint)
    }

    init(connection: NWConnection, queue: DispatchQueue) {
        self.connection = connection
        self.queue = queue
    }

    func start() {
        let peer = remoteDescription
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed(let error):
                Log.server.error("TCP \(peer, privacy: .public) failed: \(String(describing: error), privacy: .public)")
                self?.finish()
            case .cancelled:
                self?.finish()
            default:
                break
            }
        }
        connection.start(queue: queue)
        receive()

        // Drop clients that open a socket and never finish a request.
        queue.asyncAfter(deadline: .now() + Self.requestTimeout) { [weak self] in
            guard let self, !self.hasRequest else { return }
            Log.server.error("TCP \(peer, privacy: .public): no complete request within \(Self.requestTimeout, privacy: .public) s (\(self.buffer.count, privacy: .public) bytes received); closing")
            self.connection.cancel()
        }
    }

    func respond(status: Int, reason: String, headers: [(String, String)] = [], body: Data = Data()) {
        var head = "HTTP/1.1 \(status) \(reason)\r\n"
        for (name, value) in headers {
            head += "\(name): \(value)\r\n"
        }
        head += "Content-Length: \(body.count)\r\nConnection: close\r\n\r\n"

        let connection = connection
        connection.send(
            content: Data(head.utf8) + body,
            contentContext: .finalMessage,
            isComplete: true,
            completion: .contentProcessed { _ in connection.cancel() }
        )
    }

    /// Detaches the socket so another object (the WebSocket) can own it.
    func handOff() -> NWConnection {
        isFinished = true
        connection.stateUpdateHandler = nil
        onClose = nil
        return connection
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: Self.maxHeadSize) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data { self.buffer.append(contentsOf: data) }

            if let end = self.headEnd() {
                self.hasRequest = true
                let leftover = Array(self.buffer[(end + 4)...])
                guard let request = HTTPRequest(head: Array(self.buffer[..<end])) else {
                    self.respond(status: 400, reason: "Bad Request")
                    return
                }
                self.onRequest?(request, leftover)
            } else if self.buffer.count > Self.maxHeadSize {
                self.hasRequest = true
                self.respond(status: 431, reason: "Request Header Fields Too Large")
            } else if isComplete || error != nil {
                let reason = error.map { String(describing: $0) } ?? "EOF"
                Log.server.log("TCP \(self.remoteDescription, privacy: .public) closed before a full request (\(self.buffer.count, privacy: .public) bytes): \(reason, privacy: .public)")
                self.connection.cancel()
            } else {
                self.receive()
            }
        }
    }

    private func headEnd() -> Int? {
        let terminator: [UInt8] = [13, 10, 13, 10]
        guard buffer.count >= 4 else { return nil }
        for index in 0...(buffer.count - 4) where buffer[index..<(index + 4)].elementsEqual(terminator) {
            return index
        }
        return nil
    }

    private func finish() {
        guard !isFinished else { return }
        isFinished = true
        onClose?()
        onClose = nil
    }
}
