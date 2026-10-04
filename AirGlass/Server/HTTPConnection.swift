import Foundation
import Network

struct HTTPRequest {
    var method: String
    /// Path without query string.
    var path: String
    /// Header names are lowercased.
    var headers: [String: String]
    var body = Data()

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

    /// The body parsed as a JSON object, if it is one.
    var jsonObject: [String: Any]? {
        (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
    }
}

/// Reads one HTTP/1.1 request (head and Content-Length body) from a TCP
/// connection and writes one response; every connection is closed after
/// its response. The response may come later (long polling).
/// All methods must be called on `queue`.
final class HTTPConnection {
    static let maxHeadSize = 8 * 1024
    /// Signaling messages (SDP, ICE) are a few KB at most.
    static let maxBodySize = 64 * 1024
    static let requestTimeout: TimeInterval = 10
    static let lingerTimeout: TimeInterval = 2

    var onRequest: ((HTTPRequest) -> Void)?
    var onClose: (() -> Void)?

    /// True once the socket is gone, e.g. the client went away while a
    /// long poll was pending.
    private(set) var isFinished = false

    let remoteDescription: String

    private let connection: NWConnection
    private let queue: DispatchQueue
    private var buffer: [UInt8] = []
    private var pendingRequest: HTTPRequest?
    private var bodyStart = 0
    private var contentLength = 0
    private var hasRequest = false
    private var hasResponded = false

    init(connection: NWConnection, queue: DispatchQueue) {
        self.connection = connection
        self.queue = queue
        remoteDescription = String(describing: connection.endpoint)
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
        receiveRequest()

        // Drop clients that open a socket and never finish a request.
        queue.asyncAfter(deadline: .now() + Self.requestTimeout) { [weak self] in
            guard let self, !self.hasRequest, !self.hasResponded else { return }
            Log.server.error("TCP \(peer, privacy: .public): no complete request within \(Self.requestTimeout, privacy: .public) s (\(self.buffer.count, privacy: .public) bytes); closing")
            self.connection.cancel()
        }
    }

    func respond(status: Int, reason: String, headers: [(String, String)] = [], body: Data = Data()) {
        guard !hasResponded else { return }
        hasResponded = true
        guard !isFinished else { return }

        var head = "HTTP/1.1 \(status) \(reason)\r\n"
        for (name, value) in headers {
            head += "\(name): \(value)\r\n"
        }
        head += "Content-Length: \(body.count)\r\nConnection: close\r\n\r\n"

        // .finalMessage sends the response followed by a FIN.
        let connection = connection
        connection.send(
            content: Data(head.utf8) + body,
            contentContext: .finalMessage,
            isComplete: true,
            completion: .contentProcessed { [queue] error in
                if let error {
                    Log.server.error("Sending response failed: \(String(describing: error), privacy: .public)")
                    connection.cancel()
                    return
                }
                // The client normally closes right away, which the drain
                // loop notices; this only bounds how long we wait for it.
                queue.asyncAfter(deadline: .now() + Self.lingerTimeout) { connection.cancel() }
            }
        )
    }

    func respondJSON(_ object: Any, status: Int = 200) {
        let body = (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8)
        respond(status: status, reason: status == 200 ? "OK" : "Error", headers: Self.jsonHeaders, body: body)
    }

    func respondJSON(rawArray elements: [Data]) {
        var body = Data("[".utf8)
        for (index, element) in elements.enumerated() {
            if index > 0 { body.append(contentsOf: Array(",".utf8)) }
            body.append(element)
        }
        body.append(contentsOf: Array("]".utf8))
        respond(status: 200, reason: "OK", headers: Self.jsonHeaders, body: body)
    }

    private static let jsonHeaders = [
        ("Content-Type", "application/json; charset=utf-8"),
        ("Cache-Control", "no-store"),
        ("X-Content-Type-Options", "nosniff"),
    ]

    // MARK: - Reading

    private func receiveRequest() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: Self.maxHeadSize + Self.maxBodySize) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data { self.buffer.append(contentsOf: data) }

            switch self.parse() {
            case .complete(let request):
                self.hasRequest = true
                // Keep reading so a client that goes away (e.g. during a
                // long poll) is noticed; anything it sends is discarded.
                self.drain()
                self.onRequest?(request)
            case .invalid(let status, let reason):
                self.hasRequest = true
                self.respond(status: status, reason: reason)
                self.drain()
            case .incomplete:
                if isComplete || error != nil {
                    let reason = error.map { String(describing: $0) } ?? "EOF"
                    Log.server.log("TCP \(self.remoteDescription, privacy: .public) closed before a full request (\(self.buffer.count, privacy: .public) bytes): \(reason, privacy: .public)")
                    self.connection.cancel()
                } else {
                    self.receiveRequest()
                }
            }
        }
    }

    private enum ParseResult {
        case incomplete
        case complete(HTTPRequest)
        case invalid(Int, String)
    }

    private func parse() -> ParseResult {
        if pendingRequest == nil {
            guard let end = headEnd() else {
                return buffer.count > Self.maxHeadSize ? .invalid(431, "Request Header Fields Too Large") : .incomplete
            }
            guard let request = HTTPRequest(head: Array(buffer[..<end])) else {
                return .invalid(400, "Bad Request")
            }
            if request.headers["transfer-encoding"] != nil {
                return .invalid(501, "Not Implemented")
            }
            guard let length = Int(request.headers["content-length"] ?? "0"), length >= 0 else {
                return .invalid(400, "Bad Request")
            }
            guard length <= Self.maxBodySize else {
                return .invalid(413, "Payload Too Large")
            }
            pendingRequest = request
            bodyStart = end + 4
            contentLength = length
        }

        guard var request = pendingRequest, buffer.count - bodyStart >= contentLength else { return .incomplete }
        request.body = Data(buffer[bodyStart..<(bodyStart + contentLength)])
        pendingRequest = nil
        buffer = []
        return .complete(request)
    }

    private func headEnd() -> Int? {
        let terminator: [UInt8] = [13, 10, 13, 10]
        guard buffer.count >= 4 else { return nil }
        for index in 0...(buffer.count - 4) where buffer[index..<(index + 4)].elementsEqual(terminator) {
            return index
        }
        return nil
    }

    /// Reads and discards until the client closes. Closing a socket that
    /// still has unread input makes the kernel send a RST, which can
    /// discard our response before the client reads it.
    private func drain() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] _, _, isComplete, error in
            guard let self else { return }
            if isComplete || error != nil {
                self.connection.cancel()
            } else {
                self.drain()
            }
        }
    }

    private func finish() {
        guard !isFinished else { return }
        isFinished = true
        onClose?()
        onClose = nil
    }
}
