import Foundation

/// An authenticated viewer's signaling socket, exposed to the main thread.
/// Messages are JSON objects with a "type" field.
final class ViewerChannel {
    let deviceName: String

    /// Called on the main thread, in arrival order.
    var onMessage: (([String: Any]) -> Void)?
    /// Called once on the main thread when the socket closes for any reason.
    var onClose: (() -> Void)?

    private let socket: WebSocketConnection
    private let queue: DispatchQueue

    /// Must be called on `queue`; takes over the socket's callbacks.
    init(socket: WebSocketConnection, queue: DispatchQueue, deviceName: String) {
        self.socket = socket
        self.queue = queue
        self.deviceName = deviceName

        socket.onText = { [weak self] text in
            guard let data = text.data(using: .utf8),
                  let message = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            else { return }
            DispatchQueue.main.async { self?.onMessage?(message) }
        }
        socket.onClose = { [weak self] in
            DispatchQueue.main.async {
                self?.onClose?()
                self?.onClose = nil
            }
        }
    }

    func send(_ message: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: message),
              let text = String(data: data, encoding: .utf8)
        else { return }
        queue.async { [socket] in socket.send(text: text) }
    }

    func close() {
        queue.async { [socket] in socket.close(.goingAway) }
    }
}
