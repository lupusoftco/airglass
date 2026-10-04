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

    private(set) var serverState: LocalServer.State = .starting
    private(set) var localAddress: String?
    /// Mirrors `tokens.current` so the QR code refreshes when it rotates.
    private(set) var token: String

    @ObservationIgnored let capturer = ScreenCapturer()
    @ObservationIgnored let preview = PreviewRenderer()
    @ObservationIgnored private let tokens: TokenStore
    @ObservationIgnored private let server: LocalServer
    @ObservationIgnored private let addressMonitor = LocalAddressMonitor()
    @ObservationIgnored private var viewer: ViewerChannel?

    var isStreaming: Bool {
        if case .connected = connection { return true }
        return false
    }

    /// What the QR code encodes: the page on this Mac, with the token in the
    /// fragment so it is never sent in an HTTP request.
    var viewerURL: String? {
        guard let localAddress, case .ready(let port) = serverState else { return nil }
        return "http://\(localAddress):\(port)/#\(token)"
    }

    /// Why there is no QR code, if there isn't one.
    var unavailableReason: String? {
        if case .failed = serverState { return "Yerel sunucu başlatılamadı." }
        if localAddress == nil { return "Wi-Fi veya Ethernet bağlantısı yok." }
        return nil
    }

    init() {
        let tokens = TokenStore()
        self.tokens = tokens
        token = tokens.current
        server = LocalServer(tokens: tokens)

        capturer.onStateChange = { [weak self] state in
            self?.captureState = state
        }
        capturer.addConsumer(preview)

        // Both callbacks are delivered on the main queue.
        server.onStateChange = { [weak self] state in
            MainActor.assumeIsolated { self?.serverState = state }
        }
        server.onViewerConnected = { [weak self] channel in
            MainActor.assumeIsolated { self?.viewerConnected(channel) }
        }
        addressMonitor.onChange = { [weak self] address in
            MainActor.assumeIsolated { self?.localAddress = address }
        }

        server.start()
        addressMonitor.start()
    }

    private func viewerConnected(_ channel: ViewerChannel) {
        // Milestone 3 only proves the handshake; streaming and the
        // one-viewer / one-time-token rules follow in milestones 4 and 5.
        viewer = channel
        channel.onClose = { [weak self, weak channel] in
            MainActor.assumeIsolated {
                guard let self, self.viewer === channel else { return }
                self.viewer = nil
            }
        }
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
