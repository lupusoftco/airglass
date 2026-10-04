import Foundation
import WebRTC

/// One viewer's WebRTC connection. The Mac offers a send-only video track;
/// SDP and ICE candidates travel over the viewer's HTTP signaling channel.
/// Use from the main thread only.
final class PeerSession: NSObject {
    /// Generous for a LAN; WebRTC still adapts down on a weak link.
    static let maxBitrate = 8_000_000
    static let maxFramerate = Int(ScreenCapturer.framesPerSecond)
    /// After one ICE restart, how long the connection may take to come back.
    static let recoveryTimeout: TimeInterval = 15

    /// Called once on the main thread when the session ends for any reason.
    var onEnd: (() -> Void)?

    private let channel: ViewerChannel
    private let peerConnection: RTCPeerConnection
    private let transceiver: RTCRtpTransceiver
    private var pendingCandidates: [RTCIceCandidate] = []
    private var hasRemoteDescription = false
    private var isEnded = false
    /// One ICE restart per drop; reset once connected again.
    private var didAttemptRestart = false
    private var recoveryWork: DispatchWorkItem?

    init?(channel: ViewerChannel, pipeline: VideoPipeline) {
        let configuration = RTCConfiguration()
        // Local network only: host candidates, no STUN or TURN servers.
        configuration.iceServers = []
        configuration.sdpSemantics = .unifiedPlan
        configuration.bundlePolicy = .maxBundle
        configuration.rtcpMuxPolicy = .require
        configuration.tcpCandidatePolicy = .disabled
        configuration.continualGatheringPolicy = .gatherOnce

        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        guard let peerConnection = pipeline.factory.peerConnection(
            with: configuration, constraints: constraints, delegate: nil
        ) else {
            Log.webrtc.error("Could not create RTCPeerConnection")
            return nil
        }

        let transceiverInit = RTCRtpTransceiverInit()
        transceiverInit.direction = .sendOnly
        transceiverInit.streamIds = ["airglass"]
        guard let transceiver = peerConnection.addTransceiver(with: pipeline.videoTrack, init: transceiverInit) else {
            Log.webrtc.error("Could not add video transceiver")
            peerConnection.close()
            return nil
        }

        self.channel = channel
        self.peerConnection = peerConnection
        self.transceiver = transceiver
        super.init()

        peerConnection.delegate = self
        channel.onMessage = { [weak self] message in self?.handle(message) }
        channel.onClose = { [weak self] in self?.end() }
    }

    func start() {
        sendOffer()
    }

    /// Creates an offer (the first one, or after `restartIce()` one with new
    /// ICE credentials) and sends it to the viewer.
    private func sendOffer() {
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        peerConnection.offer(for: constraints) { [weak self] offer, error in
            DispatchQueue.main.async {
                guard let self else { return }
                guard let offer, error == nil else {
                    Log.webrtc.error("createOffer failed: \(String(describing: error), privacy: .public)")
                    return self.end()
                }
                Log.webrtc.log("Offer created; video codecs: \(Self.videoCodecs(in: offer.sdp), privacy: .public)")
                self.peerConnection.setLocalDescription(offer) { [weak self] error in
                    DispatchQueue.main.async {
                        guard let self else { return }
                        guard error == nil else {
                            Log.webrtc.error("setLocalDescription failed: \(String(describing: error), privacy: .public)")
                            return self.end()
                        }
                        self.channel.send(["type": "offer", "sdp": offer.sdp])
                    }
                }
            }
        }
    }

    func end() {
        guard !isEnded else { return }
        isEnded = true
        Log.webrtc.log("Session ended")
        recoveryWork?.cancel()
        peerConnection.delegate = nil
        peerConnection.close()
        channel.close()
        onEnd?()
        onEnd = nil
    }

    // MARK: - Signaling

    private func handle(_ message: [String: Any]) {
        guard !isEnded else { return }
        switch message["type"] as? String {
        case "answer":
            guard peerConnection.signalingState == .haveLocalOffer, let sdp = message["sdp"] as? String else { return }
            let answer = RTCSessionDescription(type: .answer, sdp: sdp)
            peerConnection.setRemoteDescription(answer) { [weak self] error in
                DispatchQueue.main.async {
                    guard let self else { return }
                    guard error == nil else {
                        Log.webrtc.error("setRemoteDescription(answer) failed: \(String(describing: error), privacy: .public)")
                        return self.end()
                    }
                    Log.webrtc.log("Answer applied; video codecs: \(Self.videoCodecs(in: sdp), privacy: .public)")
                    self.hasRemoteDescription = true
                    self.configureSender()
                    self.pendingCandidates.forEach(self.addCandidate)
                    self.pendingCandidates = []
                }
            }

        case "candidate":
            guard let sdp = message["candidate"] as? String, !sdp.isEmpty else { return }
            let candidate = RTCIceCandidate(
                sdp: sdp,
                sdpMLineIndex: Int32(message["sdpMLineIndex"] as? Int ?? 0),
                sdpMid: message["sdpMid"] as? String
            )
            Log.webrtc.log("Remote candidate: \(Log.describeCandidate(sdp), privacy: .public)")
            if hasRemoteDescription {
                addCandidate(candidate)
            } else {
                pendingCandidates.append(candidate)
            }

        default:
            break
        }
    }

    private func addCandidate(_ candidate: RTCIceCandidate) {
        peerConnection.add(candidate) { error in
            if let error {
                Log.webrtc.error("addIceCandidate failed: \(String(describing: error), privacy: .public)")
            }
        }
    }

    // MARK: - Recovery

    /// A brief Wi-Fi hiccup should not end the session: on failure, try one
    /// ICE restart over the still-working HTTP signaling channel.
    private func connectionStateChanged(_ state: RTCPeerConnectionState) {
        guard !isEnded else { return }
        switch state {
        case .connected:
            recoveryWork?.cancel()
            recoveryWork = nil
            didAttemptRestart = false
        case .failed:
            if didAttemptRestart {
                end()
            } else {
                restartIce()
            }
        case .closed:
            end()
        default:
            break
        }
    }

    private func restartIce() {
        didAttemptRestart = true
        Log.webrtc.log("Connection failed; restarting ICE")
        peerConnection.restartIce()
        sendOffer()

        recoveryWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.peerConnection.connectionState != .connected else { return }
            Log.webrtc.error("ICE restart did not recover the connection")
            self.end()
        }
        recoveryWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.recoveryTimeout, execute: work)
    }

    /// Screen content: keep text sharp and drop frames rather than resolution.
    private func configureSender() {
        let parameters = transceiver.sender.parameters
        parameters.degradationPreference = NSNumber(value: RTCDegradationPreference.maintainResolution.rawValue)
        for encoding in parameters.encodings {
            encoding.maxBitrateBps = NSNumber(value: Self.maxBitrate)
            encoding.maxFramerate = NSNumber(value: Self.maxFramerate)
        }
        transceiver.sender.parameters = parameters
    }

    private static func name(_ rawValue: Int, _ names: [String]) -> String {
        names.indices.contains(rawValue) ? names[rawValue] : "\(rawValue)"
    }

    /// "H264/90000, rtx/90000" from the first video m-section.
    private static func videoCodecs(in sdp: String) -> String {
        sdp.components(separatedBy: "\r\n")
            .filter { $0.hasPrefix("a=rtpmap:") }
            .compactMap { $0.split(separator: " ", maxSplits: 1).last.map(String.init) }
            .joined(separator: ", ")
    }
}

// MARK: - RTCPeerConnectionDelegate

// Called on WebRTC's signaling thread; hop to main before touching state.
extension PeerSession: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        Log.webrtc.log("Local candidate: \(Log.describeCandidate(candidate.sdp), privacy: .public)")
        let message: [String: Any] = [
            "type": "candidate",
            "candidate": candidate.sdp,
            "sdpMid": candidate.sdpMid ?? "",
            "sdpMLineIndex": Int(candidate.sdpMLineIndex),
        ]
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.isEnded else { return }
            self.channel.send(message)
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCPeerConnectionState) {
        Log.webrtc.log("connectionState → \(Self.name(newState.rawValue, ["new", "connecting", "connected", "disconnected", "failed", "closed"]), privacy: .public)")
        DispatchQueue.main.async { [weak self] in self?.connectionStateChanged(newState) }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {
        Log.webrtc.log("signalingState → \(Self.name(stateChanged.rawValue, ["stable", "have-local-offer", "have-local-pranswer", "have-remote-offer", "have-remote-pranswer", "closed"]), privacy: .public)")
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        Log.webrtc.log("iceConnectionState → \(Self.name(newState.rawValue, ["new", "checking", "connected", "completed", "failed", "disconnected", "closed"]), privacy: .public)")
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {
        Log.webrtc.log("iceGatheringState → \(Self.name(newState.rawValue, ["new", "gathering", "complete"]), privacy: .public)")
    }
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}
