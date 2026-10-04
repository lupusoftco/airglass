import CryptoKit
import Foundation
import Network

/// Minimal RFC 6455 server endpoint: text messages, ping/pong and close.
/// All methods must be called on `queue`.
final class WebSocketConnection {
    enum CloseCode: UInt16 {
        case normal = 1000
        case goingAway = 1001
        case protocolError = 1002
        case policyViolation = 1008
        case messageTooBig = 1009
    }

    /// Signaling messages (SDP, ICE) are a few KB at most.
    static let maxMessageSize = 64 * 1024

    var onText: ((String) -> Void)?
    var onClose: (() -> Void)?

    private enum Opcode: UInt8 {
        case continuation = 0x0, text = 0x1, binary = 0x2, close = 0x8, ping = 0x9, pong = 0xA
    }

    private struct Frame {
        var isFinal: Bool
        var opcode: Opcode
        var payload: [UInt8]
    }

    private struct ProtocolViolation: Error {
        var code: CloseCode
    }

    private let connection: NWConnection
    private let queue: DispatchQueue
    private var buffer: [UInt8]
    private var message: [UInt8] = []
    private var messageOpcode: Opcode?
    private var isClosing = false
    private var didNotifyClose = false

    init(connection: NWConnection, queue: DispatchQueue, leftover: [UInt8]) {
        self.connection = connection
        self.queue = queue
        self.buffer = leftover
    }

    /// `Sec-WebSocket-Accept` value for a client's `Sec-WebSocket-Key`.
    static func acceptValue(forKey key: String) -> String {
        let magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
        let digest = Insecure.SHA1.hash(data: Data((key + magic).utf8))
        return Data(digest).base64EncodedString()
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.notifyClosed()
            default: break
            }
        }
        processBuffer()
        receive()
    }

    func send(text: String) {
        guard !isClosing else { return }
        sendFrame(.text, payload: Array(text.utf8))
    }

    func close(_ code: CloseCode = .normal) {
        guard !isClosing else { return }
        Log.server.log("WebSocket closing with code \(code.rawValue, privacy: .public)")
        isClosing = true
        let payload = [UInt8(code.rawValue >> 8), UInt8(code.rawValue & 0xFF)]
        let connection = connection
        sendFrame(.close, payload: payload) { connection.cancel() }
        notifyClosed()
    }

    // MARK: - Receiving

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.buffer.append(contentsOf: data)
                self.processBuffer()
            }
            if isComplete || error != nil {
                if !self.isClosing {
                    let reason = error.map { String(describing: $0) } ?? "EOF"
                    Log.server.log("WebSocket connection ended by peer: \(reason, privacy: .public)")
                }
                self.connection.cancel()
                self.notifyClosed()
            } else if !self.isClosing {
                self.receive()
            }
        }
    }

    private func processBuffer() {
        do {
            while !isClosing, let frame = try nextFrame() {
                handle(frame)
            }
        } catch let violation as ProtocolViolation {
            close(violation.code)
        } catch {
            close(.protocolError)
        }
    }

    private func handle(_ frame: Frame) {
        switch frame.opcode {
        case .text, .binary, .continuation:
            appendData(frame)
        case .ping:
            sendFrame(.pong, payload: frame.payload)
        case .pong:
            break
        case .close:
            let code = frame.payload.count >= 2 ? UInt16(frame.payload[0]) << 8 | UInt16(frame.payload[1]) : 0
            Log.server.log("WebSocket close frame from client, code \(code, privacy: .public)")
            close(.normal)
        }
    }

    private func appendData(_ frame: Frame) {
        if frame.opcode == .continuation {
            guard messageOpcode != nil else { return close(.protocolError) }
        } else {
            guard messageOpcode == nil else { return close(.protocolError) }
            messageOpcode = frame.opcode
        }

        message.append(contentsOf: frame.payload)
        guard message.count <= Self.maxMessageSize else { return close(.messageTooBig) }
        guard frame.isFinal else { return }

        let opcode = messageOpcode
        let payload = message
        messageOpcode = nil
        message = []

        // Signaling is JSON text only.
        guard opcode == .text, let text = String(bytes: payload, encoding: .utf8) else {
            return close(.protocolError)
        }
        onText?(text)
    }

    private func nextFrame() throws -> Frame? {
        guard buffer.count >= 2 else { return nil }
        let first = buffer[0]
        let second = buffer[1]

        guard first & 0x70 == 0, let opcode = Opcode(rawValue: first & 0x0F) else {
            throw ProtocolViolation(code: .protocolError)
        }
        // Client frames must be masked (RFC 6455 §5.1).
        guard second & 0x80 != 0 else { throw ProtocolViolation(code: .protocolError) }

        var length = UInt64(second & 0x7F)
        var offset = 2
        if length == 126 {
            guard buffer.count >= 4 else { return nil }
            length = UInt64(buffer[2]) << 8 | UInt64(buffer[3])
            offset = 4
        } else if length == 127 {
            guard buffer.count >= 10 else { return nil }
            length = buffer[2..<10].reduce(0) { $0 << 8 | UInt64($1) }
            offset = 10
        }

        let isControl = opcode.rawValue & 0x8 != 0
        let isFinal = first & 0x80 != 0
        if isControl && (length > 125 || !isFinal) {
            throw ProtocolViolation(code: .protocolError)
        }
        guard length <= UInt64(Self.maxMessageSize) else {
            throw ProtocolViolation(code: .messageTooBig)
        }

        let payloadLength = Int(length)
        guard buffer.count >= offset + 4 + payloadLength else { return nil }

        let mask = Array(buffer[offset..<(offset + 4)])
        offset += 4
        var payload = Array(buffer[offset..<(offset + payloadLength)])
        for index in payload.indices {
            payload[index] ^= mask[index % 4]
        }
        buffer.removeFirst(offset + payloadLength)

        return Frame(isFinal: isFinal, opcode: opcode, payload: payload)
    }

    // MARK: - Sending

    private func sendFrame(_ opcode: Opcode, payload: [UInt8], completion: (() -> Void)? = nil) {
        var frame: [UInt8] = [0x80 | opcode.rawValue]
        let count = payload.count
        if count < 126 {
            frame.append(UInt8(count))
        } else if count <= 0xFFFF {
            frame += [126, UInt8(count >> 8), UInt8(count & 0xFF)]
        } else {
            frame.append(127)
            frame += (0..<8).reversed().map { UInt8((UInt64(count) >> (UInt64($0) * 8)) & 0xFF) }
        }
        frame += payload

        connection.send(content: Data(frame), completion: .contentProcessed { _ in completion?() })
    }

    private func notifyClosed() {
        guard !didNotifyClose else { return }
        didNotifyClose = true
        isClosing = true
        onClose?()
        onClose = nil
        onText = nil
    }
}
