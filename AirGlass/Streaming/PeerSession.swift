import Foundation
import WebRTC

/// One viewer's WebRTC connection. The Mac offers a send-only video track;
/// SDP and ICE candidates travel over the viewer's WebSocket.
/// Use from the main thread only.
final class PeerSession: NSObject {
    /// Generous for a LAN; WebRTC still adapts down on a weak link.
    static let maxBitrate = 8_000_000
    static let maxFramerate = 30

    /// Called once on the main thread when the session ends for any reason.
    var onEnd: (() -> Void)?

    private let channel: ViewerChannel
    private let peerConnection: RTCPeerConnection
    private let transceiver: RTCRtpTransceiver
    private var pendingCandidates: [RTCIceCandidate] = []
    private var hasRemoteDescription = false
    private var isEnded = false

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
        ) else { return nil }

        let transceiverInit = RTCRtpTransceiverInit()
        transceiverInit.direction = .sendOnly
        transceiverInit.streamIds = ["airglass"]
        guard let transceiver = peerConnection.addTransceiver(with: pipeline.videoTrack, init: transceiverInit) else {
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
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        peerConnection.offer(for: constraints) { [weak self] offer, error in
            DispatchQueue.main.async {
                guard let self else { return }
                guard let offer, error == nil else { return self.end() }
                self.peerConnection.setLocalDescription(offer) { [weak self] error in
                    DispatchQueue.main.async {
                        guard let self else { return }
                        guard error == nil else { return self.end() }
                        self.channel.send(["type": "offer", "sdp": offer.sdp])
                    }
                }
            }
        }
    }

    func end() {
        guard !isEnded else { return }
        isEnded = true
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
            guard !hasRemoteDescription, let sdp = message["sdp"] as? String else { return }
            let answer = RTCSessionDescription(type: .answer, sdp: sdp)
            peerConnection.setRemoteDescription(answer) { [weak self] error in
                DispatchQueue.main.async {
                    guard let self else { return }
                    guard error == nil else { return self.end() }
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
        peerConnection.add(candidate) { _ in }
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
}

// MARK: - RTCPeerConnectionDelegate

// Called on WebRTC's signaling thread; hop to main before touching state.
extension PeerSession: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
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
        guard newState == .failed || newState == .closed else { return }
        DispatchQueue.main.async { [weak self] in self?.end() }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}
