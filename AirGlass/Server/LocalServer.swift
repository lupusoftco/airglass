import Foundation
import Network

/// Serves the viewer page over HTTP and accepts the signaling WebSocket on
/// the same port. Everything runs on one private serial queue; callbacks
/// arrive on the main thread.
final class LocalServer {
    enum State: Equatable {
        case starting
        case ready(port: UInt16)
        case failed(String)
    }

    /// 3131 first; if it is taken, the next free one.
    static let candidatePorts: [UInt16] = Array(3131...3140)
    /// Plenty for one viewer reloading; caps resource use on hostile networks.
    static let maxConnections = 16
    /// Time a fresh WebSocket gets to present its token.
    static let helloTimeout: TimeInterval = 5

    var onStateChange: ((State) -> Void)?
    var onViewerConnected: ((ViewerChannel) -> Void)?

    private let tokens: TokenStore
    private let assets = ViewerAssets()
    private let queue = DispatchQueue(label: "AirGlass.server", qos: .userInitiated)
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: AnyObject] = [:]

    init(tokens: TokenStore) {
        self.tokens = tokens
    }

    func start() {
        queue.async { self.listen(portIndex: 0) }
    }

    // MARK: - Listening

    private func listen(portIndex: Int) {
        guard portIndex < Self.candidatePorts.count else {
            report(.failed("Boş port bulunamadı"))
            return
        }
        let port = NWEndpoint.Port(rawValue: Self.candidatePorts[portIndex])!

        let listener: NWListener
        do {
            listener = try NWListener(using: .tcp, on: port)
        } catch {
            listen(portIndex: portIndex + 1)
            return
        }

        listener.stateUpdateHandler = { [weak self, weak listener] state in
            guard let self, let listener, self.listener === listener else { return }
            switch state {
            case .ready:
                self.report(.ready(port: port.rawValue))
            case .failed(let error):
                listener.cancel()
                self.listener = nil
                if case .posix(.EADDRINUSE) = error {
                    self.listen(portIndex: portIndex + 1)
                } else {
                    self.report(.failed(error.localizedDescription))
                }
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }

        self.listener = listener
        listener.start(queue: queue)
    }

    private func accept(_ connection: NWConnection) {
        guard connections.count < Self.maxConnections else {
            connection.cancel()
            return
        }

        let http = HTTPConnection(connection: connection, queue: queue)
        let id = ObjectIdentifier(http)
        connections[id] = http
        http.onClose = { [weak self] in self?.connections[id] = nil }
        http.onRequest = { [weak self, unowned http] request, leftover in
            self?.route(request, on: http, leftover: leftover)
        }
        http.start()
    }

    // MARK: - Routing

    private func route(_ request: HTTPRequest, on http: HTTPConnection, leftover: [UInt8]) {
        guard request.method == "GET" else {
            http.respond(status: 405, reason: "Method Not Allowed", headers: [("Allow", "GET")])
            return
        }
        if request.path == "/ws" {
            upgrade(request, on: http, leftover: leftover)
        } else if let asset = assets[request.path] {
            http.respond(status: 200, reason: "OK", headers: Self.assetHeaders(contentType: asset.contentType), body: asset.data)
        } else {
            http.respond(status: 404, reason: "Not Found")
        }
    }

    private static func assetHeaders(contentType: String) -> [(String, String)] {
        [
            ("Content-Type", contentType),
            ("Cache-Control", "no-store"),
            ("X-Content-Type-Options", "nosniff"),
            ("Referrer-Policy", "no-referrer"),
            ("Content-Security-Policy",
             "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' ws:; "
             + "media-src 'self' blob:; img-src 'self' data:; base-uri 'none'; form-action 'none'; "
             + "frame-ancestors 'none'"),
        ]
    }

    // MARK: - WebSocket

    private func upgrade(_ request: HTTPRequest, on http: HTTPConnection, leftover: [UInt8]) {
        let headers = request.headers
        guard headers["upgrade"]?.lowercased() == "websocket",
              headers["connection"]?.lowercased().contains("upgrade") == true,
              headers["sec-websocket-version"] == "13",
              let key = headers["sec-websocket-key"], !key.isEmpty,
              let host = headers["host"]
        else {
            http.respond(status: 400, reason: "Bad Request")
            return
        }
        // Only our own page may open the socket (guards against other sites
        // in the phone's browser and DNS rebinding).
        if let origin = headers["origin"], origin != "http://\(host)" {
            http.respond(status: 403, reason: "Forbidden")
            return
        }

        let response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: \(WebSocketConnection.acceptValue(forKey: key))\r\n\r\n"

        let connection = http.handOff()
        connections[ObjectIdentifier(http)] = nil
        connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in })

        let socket = WebSocketConnection(connection: connection, queue: queue, leftover: leftover)
        let id = ObjectIdentifier(socket)
        connections[id] = socket
        let deviceName = Self.deviceName(fromUserAgent: headers["user-agent"])

        var isAuthenticated = false
        socket.onClose = { [weak self] in self?.connections[id] = nil }
        socket.onText = { [weak self, unowned socket] text in
            guard let self else { return }
            guard !isAuthenticated, self.isValidHello(text) else {
                // Same response for every failure: reveal nothing.
                socket.close(.policyViolation)
                return
            }
            isAuthenticated = true
            socket.send(text: #"{"type":"welcome"}"#)

            let channel = ViewerChannel(socket: socket, queue: self.queue, deviceName: deviceName)
            // The channel now owns the close callback; keep tracking the socket.
            let previousOnClose = socket.onClose
            socket.onClose = { [weak self] in
                previousOnClose?()
                self?.connections[id] = nil
            }
            DispatchQueue.main.async { self.onViewerConnected?(channel) }
        }
        socket.start()

        queue.asyncAfter(deadline: .now() + Self.helloTimeout) { [weak socket] in
            if !isAuthenticated { socket?.close(.policyViolation) }
        }
    }

    private func isValidHello(_ text: String) -> Bool {
        guard let data = text.data(using: .utf8),
              let message = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              message["type"] as? String == "hello",
              let token = message["token"] as? String
        else { return false }
        return tokens.isValid(token)
    }

    private static func deviceName(fromUserAgent userAgent: String?) -> String {
        guard let userAgent else { return "Tarayıcı" }
        if userAgent.contains("iPhone") { return "iPhone" }
        if userAgent.contains("iPad") { return "iPad" }
        if userAgent.contains("Android") { return "Android" }
        // iPadOS Safari reports itself as a Mac.
        if userAgent.contains("Macintosh") { return "iPad / Mac" }
        return "Tarayıcı"
    }

    private func report(_ state: State) {
        DispatchQueue.main.async { self.onStateChange?(state) }
    }
}
