import Foundation
import Network

/// Serves the viewer page and carries WebRTC signaling, both over plain
/// HTTP on one port. Everything runs on one private serial queue; callbacks
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

    var onStateChange: ((State) -> Void)?
    var onViewerConnected: ((ViewerChannel) -> Void)?

    private let tokens: TokenStore
    private let assets = ViewerAssets()
    private let queue = DispatchQueue(label: "AirGlass.server", qos: .userInitiated)
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: HTTPConnection] = [:]
    /// Authenticated viewers by session ID.
    private var channels: [String: ViewerChannel] = [:]

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
                Log.server.log("Listening on port \(port.rawValue, privacy: .public)")
                self.report(.ready(port: port.rawValue))
            case .failed(let error):
                Log.server.error("Listener on port \(port.rawValue, privacy: .public) failed: \(String(describing: error), privacy: .public)")
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
            Log.server.error("Too many connections; dropping \(String(describing: connection.endpoint), privacy: .public)")
            connection.cancel()
            return
        }

        let http = HTTPConnection(connection: connection, queue: queue)
        Log.server.log("TCP accepted from \(http.remoteDescription, privacy: .public) (\(self.connections.count + 1, privacy: .public) open)")
        let id = ObjectIdentifier(http)
        connections[id] = http
        http.onClose = { [weak self] in self?.connections[id] = nil }
        http.onRequest = { [weak self, unowned http] request in
            self?.route(request, on: http)
        }
        http.start()
    }

    // MARK: - Routing

    /// Header carrying the session ID that /signal/hello hands out.
    static let sessionHeader = "x-airglass-session"

    private func route(_ request: HTTPRequest, on http: HTTPConnection) {
        Log.server.log("\(request.method, privacy: .public) \(request.path, privacy: .public) from \(http.remoteDescription, privacy: .public)")

        if request.path.hasPrefix("/signal/") {
            routeSignaling(request, on: http)
            return
        }
        guard request.method == "GET" else {
            http.respond(status: 405, reason: "Method Not Allowed", headers: [("Allow", "GET")])
            return
        }
        if let asset = assets[request.path] {
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
             "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
             + "media-src 'self' blob:; img-src 'self' data:; base-uri 'none'; form-action 'none'; "
             + "frame-ancestors 'none'"),
        ]
    }

    // MARK: - Signaling

    /// POST /signal/hello  {token}      → {session}; the viewer is in
    /// POST /signal/send   {message}    → 204   (session header)
    /// GET  /signal/poll                → [messages] (long poll; session header)
    /// POST /signal/bye                 → 204   (session header)
    ///
    /// Every failure gets the same bare 403: nothing is revealed about why.
    private func routeSignaling(_ request: HTTPRequest, on http: HTTPConnection) {
        // Only our own page may talk to us (guards against other sites in
        // the phone's browser and DNS rebinding).
        if let origin = request.headers["origin"], origin != "http://\(request.headers["host"] ?? "")" {
            Log.server.error("Signaling rejected: origin \(origin, privacy: .public)")
            http.respond(status: 403, reason: "Forbidden")
            return
        }

        switch (request.method, request.path) {
        case ("POST", "/signal/hello"):
            hello(request, on: http)

        case ("POST", "/signal/send"):
            guard let channel = channel(for: request), let message = request.jsonObject else {
                return forbid(http, "send without a valid session or body")
            }
            channel.receive(message)
            http.respond(status: 204, reason: "No Content")

        case ("GET", "/signal/poll"):
            guard let channel = channel(for: request) else {
                return forbid(http, "poll without a valid session")
            }
            channel.poll(on: http)

        case ("POST", "/signal/bye"):
            channel(for: request)?.closeByViewer()
            http.respond(status: 204, reason: "No Content")

        default:
            http.respond(status: 404, reason: "Not Found")
        }
    }

    private func hello(_ request: HTTPRequest, on http: HTTPConnection) {
        guard let token = request.jsonObject?["token"] as? String, tokens.isValid(token) else {
            return forbid(http, "hello with a wrong token or malformed body")
        }

        let deviceName = Self.deviceName(fromUserAgent: request.headers["user-agent"])
        let sessionID = TokenStore.generate()
        let channel = ViewerChannel(deviceName: deviceName, queue: queue)
        channels[sessionID] = channel
        channel.onEnded = { [weak self] in self?.channels[sessionID] = nil }

        Log.server.log("Viewer authenticated (\(deviceName, privacy: .public), UA: \(request.headers["user-agent"] ?? "-", privacy: .public))")
        http.respondJSON(["session": sessionID])
        DispatchQueue.main.async { self.onViewerConnected?(channel) }
    }

    private func channel(for request: HTTPRequest) -> ViewerChannel? {
        request.headers[Self.sessionHeader].flatMap { channels[$0] }
    }

    private func forbid(_ http: HTTPConnection, _ reason: String) {
        Log.server.error("Signaling rejected: \(reason, privacy: .public)")
        http.respond(status: 403, reason: "Forbidden")
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
