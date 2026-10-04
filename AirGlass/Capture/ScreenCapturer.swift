import CoreGraphics
import CoreMedia
import Foundation
import ScreenCaptureKit

/// Receives every complete captured frame. Called on the capture queue,
/// so implementations must be thread-safe and return quickly.
protocol VideoFrameConsumer: AnyObject {
    func consume(_ sampleBuffer: CMSampleBuffer)
}

struct CaptureSource: Equatable {
    var isWindow: Bool
    /// Output size in pixels.
    var size: CGSize
}

enum CaptureState: Equatable {
    case idle
    case capturing(CaptureSource)
    case permissionDenied
    case failed(String)
}

/// Owns the ScreenCaptureKit stream. The user picks what to share with the
/// system `SCContentSharingPicker`; frames fan out to registered consumers
/// (the temporary preview now, the WebRTC video source later).
@MainActor
final class ScreenCapturer: NSObject {
    /// Long edge cap; keeps the later H.264 encode cheap and phone-friendly.
    static let maxPixelEdge: CGFloat = 1920
    static let framesPerSecond: Int32 = 30

    var onStateChange: ((CaptureState) -> Void)?

    private(set) var state: CaptureState = .idle {
        didSet { onStateChange?(state) }
    }

    private let picker = SCContentSharingPicker.shared
    private let sampleQueue = DispatchQueue(label: "AirGlass.capture", qos: .userInteractive)
    private nonisolated let consumers = ConsumerRegistry()
    private var stream: SCStream?

    override init() {
        super.init()
        picker.add(self)
        picker.isActive = true
    }

    func addConsumer(_ consumer: VideoFrameConsumer) {
        consumers.add(consumer)
    }

    func removeConsumer(_ consumer: VideoFrameConsumer) {
        consumers.remove(consumer)
    }

    func presentPicker(for mode: CaptureMode) {
        var configuration = SCContentSharingPickerConfiguration()
        configuration.allowedPickerModes = mode == .window ? .singleWindow : .singleDisplay
        if let bundleID = Bundle.main.bundleIdentifier {
            configuration.excludedBundleIDs = [bundleID]
        }
        picker.defaultConfiguration = configuration
        picker.present(using: mode == .window ? .window : .display)
    }

    func stop() async {
        guard let stream else { return }
        self.stream = nil
        try? await stream.stopCapture()
        state = .idle
    }

    // MARK: - Private

    private func start(with filter: SCContentFilter) async {
        let configuration = Self.configuration(for: filter)
        let source = CaptureSource(
            isWindow: filter.style == .window,
            size: CGSize(width: configuration.width, height: configuration.height)
        )

        do {
            if let stream {
                try await stream.updateContentFilter(filter)
                try await stream.updateConfiguration(configuration)
            } else {
                let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
                try await stream.startCapture()
                self.stream = stream
            }
            state = .capturing(source)
        } catch {
            stream = nil
            handle(error)
        }
    }

    private func handle(_ error: Error) {
        let nsError = error as NSError
        let isStreamError = nsError.domain == SCStreamErrorDomain

        if isStreamError && nsError.code == SCStreamError.Code.userStopped.rawValue {
            // Stopped from the system's screen sharing menu.
            state = .idle
        } else if (isStreamError && nsError.code == SCStreamError.Code.userDeclined.rawValue)
                    || !CGPreflightScreenCaptureAccess() {
            // Shows the system prompt the first time; afterwards it only
            // registers the app in System Settings → Screen Recording.
            CGRequestScreenCaptureAccess()
            state = .permissionDenied
        } else {
            state = .failed(error.localizedDescription)
        }
    }

    private static func configuration(for filter: SCContentFilter) -> SCStreamConfiguration {
        let scale = CGFloat(filter.pointPixelScale)
        var width = filter.contentRect.width * scale
        var height = filter.contentRect.height * scale
        let fit = min(1, maxPixelEdge / max(width, height, 1))
        width *= fit
        height *= fit

        let configuration = SCStreamConfiguration()
        // Video encoders want even dimensions.
        configuration.width = max(2, Int(width) & ~1)
        configuration.height = max(2, Int(height) & ~1)
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: framesPerSecond)
        configuration.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        configuration.queueDepth = 5
        configuration.showsCursor = true
        return configuration
    }
}

// MARK: - SCContentSharingPickerObserver

extension ScreenCapturer: SCContentSharingPickerObserver {
    nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {}

    nonisolated func contentSharingPicker(
        _ picker: SCContentSharingPicker,
        didUpdateWith filter: SCContentFilter,
        for stream: SCStream?
    ) {
        Task { @MainActor in
            await self.start(with: filter)
        }
    }

    nonisolated func contentSharingPickerStartDidFailWithError(_ error: Error) {
        Task { @MainActor in
            self.handle(error)
        }
    }
}

// MARK: - SCStreamDelegate, SCStreamOutput

extension ScreenCapturer: SCStreamDelegate, SCStreamOutput {
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            guard self.stream === stream else { return }
            self.stream = nil
            self.handle(error)
        }
    }

    nonisolated func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of type: SCStreamOutputType
    ) {
        guard type == .screen, sampleBuffer.isValid, Self.isCompleteFrame(sampleBuffer) else { return }
        consumers.forEach { $0.consume(sampleBuffer) }
    }

    /// ScreenCaptureKit also delivers "idle" buffers without pixels when
    /// nothing changed on screen; only complete frames carry an image.
    private nonisolated static func isCompleteFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard
            let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
            let rawStatus = attachments.first?[.status] as? Int,
            let status = SCFrameStatus(rawValue: rawStatus)
        else { return false }
        return status == .complete
    }
}

// MARK: - ConsumerRegistry

/// Thread-safe list of weakly held consumers.
private final class ConsumerRegistry: @unchecked Sendable {
    private struct Entry { weak var consumer: VideoFrameConsumer? }

    private let lock = NSLock()
    private var entries: [Entry] = []

    func add(_ consumer: VideoFrameConsumer) {
        lock.withLock {
            entries.removeAll { $0.consumer == nil || $0.consumer === consumer }
            entries.append(Entry(consumer: consumer))
        }
    }

    func remove(_ consumer: VideoFrameConsumer) {
        lock.withLock { entries.removeAll { $0.consumer == nil || $0.consumer === consumer } }
    }

    func forEach(_ body: (VideoFrameConsumer) -> Void) {
        let current = lock.withLock { entries.compactMap(\.consumer) }
        current.forEach(body)
    }
}
