using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirGlass.Server;
using AirGlass.Support;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AirGlass.Streaming;

/// <summary>
/// One viewer's WebRTC connection, mirroring the macOS PeerSession: the PC
/// offers a send-only H.264 video track; SDP and ICE candidates travel over
/// the viewer's HTTP signaling channel. Local network only: host candidates
/// on the LAN address, no STUN or TURN servers. Use from the UI thread.
/// </summary>
public sealed class PeerSession
{
    /// <summary>After one ICE restart, how long the connection may take to come back.</summary>
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(15);
    private const int H264PayloadType = 102;

    private readonly ViewerChannel _channel;
    private readonly VideoPipeline _pipeline;
    private readonly RTCPeerConnection _peer;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly List<RTCIceCandidate> _pendingLocalCandidates = new();
    private bool _hasSentOffer;
    private bool _hasRemoteDescription;
    private readonly List<RTCIceCandidateInit> _pendingRemoteCandidates = new();
    private bool _isEnded;
    /// <summary>One ICE restart per drop; reset once connected again.</summary>
    private bool _didAttemptRestart;
    private CancellationTokenSource? _recovery;
    private volatile bool _isConnected;

    public PeerSession(ViewerChannel channel, VideoPipeline pipeline, IPAddress localAddress)
    {
        _channel = channel;
        _pipeline = pipeline;

        _peer = CreatePeer(localAddress);

        _peer.onicecandidate += candidate => Post(() => LocalCandidate(candidate));
        _peer.onconnectionstatechange += state => Post(() => ConnectionStateChanged(state));
        _peer.oniceconnectionstatechange += state => Log.WebRtc($"iceConnectionState → {state}");
        _peer.onsignalingstatechange += () => Log.WebRtc($"signalingState → {_peer.signalingState}");
        _peer.OnReceiveReport += (_, _, report) => OnReport(report);

        _channel.MessageReceived += Handle;
        _channel.Closed += End;
        _pipeline.FrameEncoded += OnFrameEncoded;
    }

    /// <summary>A peer connection that sends one H.264 track from <paramref name="localAddress"/>.</summary>
    internal static RTCPeerConnection CreatePeer(IPAddress localAddress)
    {
        var peer = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>(),
            bundlePolicy = RTCBundlePolicy.max_bundle,
            rtcpMuxPolicy = RTCRtcpMuxPolicy.require,
            // Only the LAN address: the one host candidate a phone on the same
            // network can reach, and no other local addresses revealed.
            X_BindAddress = localAddress,
            X_UseRtpFeedbackProfile = true,
        });
        peer.addTrack(new MediaStreamTrack(H264Format, MediaStreamStatusEnum.SendOnly));
        return peer;
    }

    /// <summary>Constrained Baseline, as the macOS app sends.</summary>
    internal static readonly VideoFormat H264Format = new(VideoCodecsEnum.H264, H264PayloadType,
        VideoFormat.DEFAULT_CLOCK_RATE, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");

    /// <summary>Raised once on the UI thread when the session ends for any reason.</summary>
    public event Action? Ended;

    public void Start() => _ = SendOfferAsync();

    public void End()
    {
        if (_isEnded) return;
        _isEnded = true;
        Log.WebRtc("Session ended");
        _isConnected = false;
        _recovery?.Cancel();
        _pipeline.FrameEncoded -= OnFrameEncoded;
        _pipeline.IsActive = false;
        _channel.MessageReceived -= Handle;
        _channel.Closed -= End;
        try
        {
            _peer.close();
        }
        catch (Exception error)
        {
            Log.WebRtc($"close failed: {error.Message}");
        }
        _channel.Close();
        var ended = Ended;
        Ended = null;
        ended?.Invoke();
    }

    // MARK: - Signaling

    /// <summary>Creates an offer (the first one, or after restartIce() one with
    /// new ICE credentials) and sends it to the viewer.</summary>
    private async Task SendOfferAsync()
    {
        try
        {
            var offer = _peer.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            await _peer.setLocalDescription(offer);
            if (_isEnded) return;
            Log.WebRtc($"Offer created; video codecs: {VideoCodecs(offer.sdp)}");
            _channel.Send(new JsonObject { ["type"] = "offer", ["sdp"] = WithPictureLossFeedback(offer.sdp) });
            _hasSentOffer = true;
            foreach (var candidate in _pendingLocalCandidates) SendCandidate(candidate);
            _pendingLocalCandidates.Clear();
        }
        catch (Exception error)
        {
            Log.WebRtc($"Creating the offer failed: {error}");
            End();
        }
    }

    private void Handle(JsonObject message)
    {
        if (_isEnded) return;
        switch (Json.String(message["type"]))
        {
            case "answer":
            {
                if (Json.String(message["sdp"]) is not { } sdp) return;
                var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
                if (result != SetDescriptionResultEnum.OK)
                {
                    Log.WebRtc($"setRemoteDescription(answer) failed: {result}");
                    End();
                    return;
                }
                Log.WebRtc($"Answer applied; video codecs: {VideoCodecs(sdp)}");
                _hasRemoteDescription = true;
                foreach (var candidate in _pendingRemoteCandidates) AddCandidate(candidate);
                _pendingRemoteCandidates.Clear();
                break;
            }
            case "candidate":
            {
                if (Json.String(message["candidate"]) is not { Length: > 0 } candidate) return;
                var init = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = Json.String(message["sdpMid"]),
                    sdpMLineIndex = (ushort)(Json.Int(message["sdpMLineIndex"]) ?? 0),
                };
                Log.WebRtc($"Remote candidate: {DescribeCandidate(candidate)}");
                if (_hasRemoteDescription) AddCandidate(init);
                else _pendingRemoteCandidates.Add(init);
                break;
            }
        }
    }

    private void AddCandidate(RTCIceCandidateInit candidate)
    {
        try
        {
            _peer.addIceCandidate(candidate);
        }
        catch (Exception error)
        {
            Log.WebRtc($"addIceCandidate failed: {error.Message}");
        }
    }

    private void LocalCandidate(RTCIceCandidate candidate)
    {
        if (_isEnded) return;
        Log.WebRtc($"Local candidate: {DescribeCandidate(candidate.candidate)}");
        if (_hasSentOffer) SendCandidate(candidate);
        else _pendingLocalCandidates.Add(candidate);
    }

    private void SendCandidate(RTCIceCandidate candidate)
    {
        _channel.Send(new JsonObject
        {
            ["type"] = "candidate",
            ["candidate"] = "candidate:" + candidate.candidate,
            ["sdpMid"] = candidate.sdpMid ?? "0",
            ["sdpMLineIndex"] = (int)candidate.sdpMLineIndex,
        });
    }

    // MARK: - Media

    private void OnFrameEncoded(byte[] accessUnit, TimeSpan sincePrevious)
    {
        if (!_isConnected) return;
        var duration = (uint)Math.Clamp(sincePrevious.TotalSeconds * VideoFormat.DEFAULT_CLOCK_RATE, 1, VideoFormat.DEFAULT_CLOCK_RATE);
        try
        {
            _peer.SendVideo(duration, accessUnit);
        }
        catch (Exception error)
        {
            Log.WebRtc($"Sending video failed: {error.Message}");
        }
    }

    /// <summary>The viewer lost packets and asks for a fresh keyframe.</summary>
    private void OnReport(RTCPCompoundPacket report)
    {
        var header = report.Feedback?.Header;
        if (header == null || header.PacketType != RTCPReportTypesEnum.PSFB) return;
        if (header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
        {
            Log.WebRtc($"Viewer requested a keyframe ({header.PayloadFeedbackMessageType})");
            _pipeline.RequestKeyFrame();
        }
    }

    /// <summary>SIPSorcery only advertises transport-cc; without "nack pli" the
    /// browser never asks for a keyframe after packet loss.</summary>
    internal static string WithPictureLossFeedback(string sdp)
    {
        var line = $"a=rtpmap:{H264PayloadType} H264/90000";
        var index = sdp.IndexOf(line, StringComparison.Ordinal);
        if (index < 0 || sdp.Contains($"a=rtcp-fb:{H264PayloadType} nack pli")) return sdp;
        var end = sdp.IndexOf("\r\n", index, StringComparison.Ordinal);
        if (end < 0) return sdp;
        return sdp.Insert(end + 2, $"a=rtcp-fb:{H264PayloadType} nack pli\r\na=rtcp-fb:{H264PayloadType} ccm fir\r\n");
    }

    // MARK: - Recovery

    /// <summary>A brief Wi-Fi hiccup should not end the session: on failure,
    /// try one ICE restart over the still-working HTTP signaling channel.</summary>
    private void ConnectionStateChanged(RTCPeerConnectionState state)
    {
        Log.WebRtc($"connectionState → {state}");
        if (_isEnded) return;
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                _recovery?.Cancel();
                _recovery = null;
                _didAttemptRestart = false;
                _isConnected = true;
                _pipeline.IsActive = true;
                break;
            case RTCPeerConnectionState.failed:
                _isConnected = false;
                if (_didAttemptRestart) End();
                else RestartIce();
                break;
            case RTCPeerConnectionState.closed:
                End();
                break;
            case RTCPeerConnectionState.disconnected:
                _isConnected = false;
                break;
        }
    }

    private void RestartIce()
    {
        _didAttemptRestart = true;
        Log.WebRtc("Connection failed; restarting ICE");
        _peer.restartIce();
        _ = SendOfferAsync();

        _recovery?.Cancel();
        var recovery = new CancellationTokenSource();
        _recovery = recovery;
        Task.Delay(RecoveryTimeout, recovery.Token).ContinueWith(task =>
        {
            if (task.IsCanceled) return;
            Post(() =>
            {
                if (_isEnded || _peer.connectionState == RTCPeerConnectionState.connected) return;
                Log.WebRtc("ICE restart did not recover the connection");
                End();
            });
        }, TaskScheduler.Default);
    }

    // MARK: - Helpers

    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }

    /// <summary>"H264/90000" from the video m-section.</summary>
    private static string VideoCodecs(string sdp) => string.Join(", ",
        sdp.Split("\r\n").Where(line => line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            .Select(line => line.Split(' ', 2).Last()));

    /// <summary>"host 192.168.1.5:50000 udp" from an SDP candidate line.</summary>
    private static string DescribeCandidate(string candidate)
    {
        var fields = Regex.Split(candidate.Trim(), @"\s+");
        if (fields.Length < 8) return candidate;
        var typeIndex = Array.IndexOf(fields, "typ");
        var type = typeIndex >= 0 && typeIndex + 1 < fields.Length ? fields[typeIndex + 1] : "?";
        return $"{type} {fields[4]}:{fields[5]} {fields[2]}";
    }
}
