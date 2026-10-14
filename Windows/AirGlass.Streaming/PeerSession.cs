using System.Text.Json;

namespace AirGlass.Streaming;

public class PeerSession : IDisposable
{
    private string _sessionId;
    private bool _isConnected;
    private List<string> _iceCandidates = new();

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<VideoFrameRequestEventArgs>? VideoFrameRequired;

    public PeerSession()
    {
        _sessionId = Guid.NewGuid().ToString("N")[..16];
        _isConnected = false;
    }

    public string SessionId => _sessionId;
    public bool IsConnected => _isConnected;

    public async Task<string> GetOfferAsync()
    {
        var offer = new
        {
            type = "offer",
            sdp = GenerateSdpOffer()
        };

        var json = JsonSerializer.Serialize(offer);
        return json;
    }

    public async Task SetAnswerAsync(string sdpAnswer)
    {
        try
        {
            var doc = JsonDocument.Parse(sdpAnswer);
            var answer = doc.RootElement.GetProperty("sdp").GetString() ?? "";

            _isConnected = true;
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { State = "connected" });
        }
        catch (Exception ex)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
            {
                State = "failed",
                Error = ex.Message
            });
        }
    }

    public void AddIceCandidate(string candidate, int sdpMLineIndex)
    {
        _iceCandidates.Add(candidate);
    }

    public void SendVideoFrame(byte[] h264Data)
    {
        if (_isConnected)
        {
            VideoFrameRequired?.Invoke(this, new VideoFrameRequestEventArgs
            {
                FrameData = h264Data
            });
        }
    }

    private string GenerateSdpOffer()
    {
        var sessionId = Guid.NewGuid().GetHashCode().ToString().Substring(1);
        var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return $@"v=0
o=AirGlass {sessionId} {version} IN IP4 127.0.0.1
s=AirGlass Streaming
t=0 0
a=group:BUNDLE 0
a=extmap-allow-mixed
a=msid-semantic: WMS stream
m=video 9 UDP/TLS/RTP/SAVPF 96
c=IN IP4 127.0.0.1
a=rtcp:9 IN IP4 127.0.0.1
a=ice-ufrag:airglass
a=ice-pwd:airglass1234567890ab
a=fingerprint:sha-256 00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00
a=setup:actpass
a=mid:0
a=bundle-only
a=rtcp-mux
a=rtpmap:96 H264/90000
a=fmtp:96 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f
a=msid:stream video0
a=sendrecv";
    }

    public void Dispose()
    {
        _isConnected = false;
        GC.SuppressFinalize(this);
    }
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public string State { get; set; } = "new";
    public string? Error { get; set; }
}

public class VideoFrameRequestEventArgs : EventArgs
{
    public byte[] FrameData { get; set; } = Array.Empty<byte>();
}
