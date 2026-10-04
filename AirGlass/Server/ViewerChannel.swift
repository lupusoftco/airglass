import Foundation

/// An authenticated viewer's signaling channel over plain HTTP:
/// the phone POSTs its messages and long-polls GET for ours.
/// Messages are JSON objects with a "type" field.
///
/// The public API (`send`, `close`, callbacks) may be used from the main
/// thread; everything else runs on the server queue.
final class ViewerChannel {
    /// How long a poll is held open when there is nothing to deliver.
    static let pollTimeout: TimeInterval = 20
    /// Without a pending poll for this long, the viewer is considered gone
    /// (tab closed, phone locked, network lost).
    static let idleTimeout: TimeInterval = 8

    let deviceName: String

    /// Called on the main thread, in arrival order.
    var onMessage: (([String: Any]) -> Void)?
    /// Called once on the main thread when the channel ends for any reason.
    var onClose: (() -> Void)?

    /// Server-internal, called once on the server queue when the channel ends.
    var onEnded: (() -> Void)?

    private let queue: DispatchQueue
    /// Serialized JSON objects waiting for the next poll.
    private var outbox: [Data] = []
    private var pendingPoll: HTTPConnection?
    private var pollTimeoutWork: DispatchWorkItem?
    private var lastSeen = Date()
    private var isClosed = false
    private let watchdog: DispatchSourceTimer

    /// Must be called on `queue`.
    init(deviceName: String, queue: DispatchQueue) {
        self.deviceName = deviceName
        self.queue = queue
        watchdog = DispatchSource.makeTimerSource(queue: queue)
        watchdog.schedule(deadline: .now() + 2, repeating: 2)
        watchdog.setEventHandler { [weak self] in self?.checkLiveness() }
        watchdog.resume()
    }

    deinit {
        watchdog.cancel()
    }

    // MARK: - Public

    func send(_ message: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: message) else { return }
        Log.signaling.log("→ \(message["type"] as? String ?? "?", privacy: .public)")
        queue.async {
            guard !self.isClosed else { return }
            self.outbox.append(data)
            self.flush()
        }
    }

    func close() {
        queue.async { self.end(reason: "closed by the Mac") }
    }

    // MARK: - Server side (on queue)

    /// A message the viewer POSTed.
    func receive(_ message: [String: Any]) {
        guard !isClosed else { return }
        lastSeen = Date()
        Log.signaling.log("← \(message["type"] as? String ?? "?", privacy: .public)")
        DispatchQueue.main.async { self.onMessage?(message) }
    }

    /// A long-poll GET: answered now if messages are waiting, otherwise
    /// when one is sent or after `pollTimeout` with an empty list.
    func poll(on http: HTTPConnection) {
        guard !isClosed else {
            http.respond(status: 410, reason: "Gone")
            return
        }
        lastSeen = Date()

        // Only one poll is held at a time; release an older one.
        if let previous = pendingPoll {
            pendingPoll = nil
            previous.respondJSON(rawArray: [])
        }

        pendingPoll = http
        flush()
        guard pendingPoll === http else { return }

        pollTimeoutWork?.cancel()
        let work = DispatchWorkItem { [weak self, weak http] in
            guard let self, let http, self.pendingPoll === http else { return }
            self.pendingPoll = nil
            self.lastSeen = Date()
            http.respondJSON(rawArray: [])
        }
        pollTimeoutWork = work
        queue.asyncAfter(deadline: .now() + Self.pollTimeout, execute: work)
    }

    /// The viewer said goodbye (page closed).
    func closeByViewer() {
        end(reason: "closed by the viewer")
    }

    // MARK: - Private

    private func flush() {
        guard !outbox.isEmpty, let poll = pendingPoll else { return }
        pendingPoll = nil
        pollTimeoutWork?.cancel()
        lastSeen = Date()
        guard !poll.isFinished else { return }
        let messages = outbox
        outbox = []
        poll.respondJSON(rawArray: messages)
    }

    private func checkLiveness() {
        if let poll = pendingPoll, !poll.isFinished {
            lastSeen = Date()
            return
        }
        if Date().timeIntervalSince(lastSeen) > Self.idleTimeout {
            end(reason: "viewer stopped polling")
        }
    }

    private func end(reason: String) {
        guard !isClosed else { return }
        isClosed = true
        Log.signaling.log("Channel ended: \(reason, privacy: .public)")
        watchdog.cancel()
        pollTimeoutWork?.cancel()
        pendingPoll?.respond(status: 410, reason: "Gone")
        pendingPoll = nil
        outbox = []
        onEnded?()
        onEnded = nil
        DispatchQueue.main.async {
            self.onClose?()
            self.onClose = nil
        }
    }
}
