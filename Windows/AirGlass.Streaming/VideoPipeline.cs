using AirGlass.Capture;
using AirGlass.Encoding;

namespace AirGlass.Streaming;

public class VideoPipeline : IDisposable
{
    private readonly ScreenCapture _capturer;
    private readonly H264Encoder _encoder;
    private PeerSession? _peerSession;
    private bool _isRunning;

    public event EventHandler<PipelineStateChangedEventArgs>? StateChanged;

    public VideoPipeline(int width, int height)
    {
        _capturer = new ScreenCapture();
        _encoder = new H264Encoder(width, height);
        _isRunning = false;

        _capturer.FrameCaptured += OnFrameCaptured;
        _encoder.FrameEncoded += OnFrameEncoded;
    }

    public PeerSession? CurrentSession => _peerSession;

    public void SetPeerSession(PeerSession peer)
    {
        _peerSession = peer;
    }

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs { State = "started" });
        _capturer.StartCapture();
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _capturer.StopCapture();
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs { State = "stopped" });
    }

    private void OnFrameCaptured(object? sender, FrameCapturedEventArgs e)
    {
        if (_isRunning)
        {
            _encoder.EncodeFrame(e.FrameData);
        }
    }

    private void OnFrameEncoded(object? sender, EncodedFrameEventArgs e)
    {
        if (_isRunning && _peerSession?.IsConnected == true)
        {
            _peerSession.SendVideoFrame(e.EncodedData);
        }
    }

    public void Dispose()
    {
        Stop();
        _capturer.Dispose();
        _encoder.Dispose();
        _peerSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class PipelineStateChangedEventArgs : EventArgs
{
    public string State { get; set; } = "";
}
