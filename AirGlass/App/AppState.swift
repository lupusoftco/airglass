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

    /// TEMPORARY: shows the on-screen debug log on the phone. Remove before release.
    static let viewerDebugLog = true

    @ObservationIgnored let capturer = ScreenCapturer()
    @ObservationIgnored private let pipeline = VideoPipeline()
    @ObservationIgnored private let tokens: TokenStore
    @ObservationIgnored private let server: LocalServer
    @ObservationIgnored private let addressMonitor = LocalAddressMonitor()
    @ObservationIgnored private var session: PeerSession?

    var isStreaming: Bool {
        if case .connected = connection { return true }
        return false
    }

    /// What the QR code encodes: the page on this Mac, with the token in the
    /// fragment so it is never sent in an HTTP request.
    var viewerURL: String? {
        guard let localAddress, case .ready(let port) = serverState else { return nil }
        return "http://\(localAddress):\(port)/#\(token)\(Self.viewerDebugLog ? "&debug" : "")"
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
            guard let self else { return }
            self.captureState = state
            if case .capturing = state {} else { self.pipeline.clearLastFrame() }
        }
        capturer.addConsumer(pipeline)

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
        // The one-viewer / one-time-token rules arrive in milestone 5; for
        // now a newer viewer simply replaces the previous one.
        session?.end()

        guard let session = PeerSession(channel: channel, pipeline: pipeline) else {
            channel.close()
            return
        }
        session.onEnd = { [weak self, weak session] in
            MainActor.assumeIsolated {
                guard let self, self.session === session else { return }
                self.session = nil
            }
        }
        self.session = session
        session.start()
    }

    func copyViewerURL() {
        guard let viewerURL else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(viewerURL, forType: .string)
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
