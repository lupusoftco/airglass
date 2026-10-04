import Foundation
import Observation

/// What gets mirrored to the viewer.
enum CaptureMode: Hashable {
    case fullScreen
    case window
}

enum ConnectionState: Equatable {
    /// Waiting for a viewer to scan the QR code.
    case waiting
    /// A single viewer is watching.
    case connected(deviceName: String)
}

/// Single source of truth for the UI. Capture, server and WebRTC layers
/// will report into this object in later milestones.
@MainActor
@Observable
final class AppState {
    var captureMode: CaptureMode = .fullScreen
    var connection: ConnectionState = .waiting

    /// URL encoded in the QR code. Placeholder until the local server exists.
    var viewerURL = "http://192.168.1.2:3131/#placeholder-token"

    var isStreaming: Bool {
        if case .connected = connection { return true }
        return false
    }

    func disconnect() {
        connection = .waiting
    }
}
