import CoreMedia
import Foundation
import WebRTC

/// Feeds captured frames into a WebRTC video track. Lives for the whole
/// app; each viewer's peer connection sends this one track.
final class VideoPipeline: VideoFrameConsumer {
    /// ScreenCaptureKit only delivers frames when the screen changes. On a
    /// still screen the last frame is repeated so a newly joined viewer gets
    /// a picture and the encoder can refresh lost keyframes.
    static let repeatInterval: TimeInterval = 0.5

    let factory: RTCPeerConnectionFactory
    let videoTrack: RTCVideoTrack

    private let source: RTCVideoSource
    private let capturer: RTCVideoCapturer
    private let lock = NSLock()
    private var lastPixelBuffer: CVPixelBuffer?
    private var lastFrameTime: TimeInterval = 0
    private let repeatTimer: DispatchSourceTimer

    init() {
        RTCInitializeSSL()

        let encoderFactory = RTCDefaultVideoEncoderFactory()
        // H.264 Constrained Baseline: hardware-decoded by every iPhone and
        // supported by all Safari versions with WebRTC.
        if let baseline = RTCDefaultVideoEncoderFactory.supportedCodecs().first(where: {
            $0.name == kRTCVideoCodecH264Name && ($0.parameters["profile-level-id"]?.hasPrefix("42e0") ?? false)
        }) {
            encoderFactory.preferredCodec = baseline
        }

        factory = RTCPeerConnectionFactory(encoderFactory: encoderFactory, decoderFactory: RTCDefaultVideoDecoderFactory())
        // Screencast mode favours sharp detail over frame rate.
        source = factory.videoSource(forScreenCast: true)
        capturer = RTCVideoCapturer(delegate: source)
        videoTrack = factory.videoTrack(with: source, trackId: "screen")

        repeatTimer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "AirGlass.frame-repeat"))
        repeatTimer.schedule(deadline: .now() + Self.repeatInterval, repeating: Self.repeatInterval)
        repeatTimer.setEventHandler { [weak self] in self?.repeatLastFrameIfIdle() }
        repeatTimer.resume()
    }

    deinit {
        repeatTimer.cancel()
    }

    /// Forgets the last frame, e.g. when capture stops, so nothing stale
    /// keeps being sent.
    func clearLastFrame() {
        lock.withLock { lastPixelBuffer = nil }
    }

    func consume(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let now = Self.now()
        lock.withLock {
            lastPixelBuffer = pixelBuffer
            lastFrameTime = now
        }
        deliver(pixelBuffer, at: now)
    }

    private func repeatLastFrameIfIdle() {
        let now = Self.now()
        let pixelBuffer: CVPixelBuffer? = lock.withLock {
            guard let buffer = lastPixelBuffer, now - lastFrameTime >= Self.repeatInterval else { return nil }
            lastFrameTime = now
            return buffer
        }
        if let pixelBuffer {
            deliver(pixelBuffer, at: now)
        }
    }

    private func deliver(_ pixelBuffer: CVPixelBuffer, at time: TimeInterval) {
        let frame = RTCVideoFrame(
            buffer: RTCCVPixelBuffer(pixelBuffer: pixelBuffer),
            rotation: ._0,
            timeStampNs: Int64(time * 1_000_000_000)
        )
        source.capturer(capturer, didCapture: frame)
    }

    private static func now() -> TimeInterval {
        CMClockGetTime(CMClockGetHostTimeClock()).seconds
    }
}
