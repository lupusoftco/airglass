import AppKit
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
/// report into this object.
@MainActor
@Observable
final class AppState {
    private(set) var captureMode: CaptureMode = .fullScreen
    private(set) var captureState: CaptureState = .idle
    var connection: ConnectionState = .waiting

    /// URL encoded in the QR code. Placeholder until the local server exists.
    var viewerURL = "http://192.168.1.2:3131/#placeholder-token"

    @ObservationIgnored let capturer = ScreenCapturer()
    @ObservationIgnored let preview = PreviewRenderer()

    var isStreaming: Bool {
        if case .connected = connection { return true }
        return false
    }

    init() {
        capturer.onStateChange = { [weak self] state in
            self?.captureState = state
        }
        capturer.addConsumer(preview)
    }

    /// Opens the system content picker for the given mode.
    func chooseSource(_ mode: CaptureMode) {
        captureMode = mode
        capturer.presentPicker(for: mode)
    }

    func stopCapture() {
        Task { await capturer.stop() }
    }

    func openScreenRecordingSettings() {
        let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!
        NSWorkspace.shared.open(url)
    }

    func disconnect() {
        connection = .waiting
    }
}
