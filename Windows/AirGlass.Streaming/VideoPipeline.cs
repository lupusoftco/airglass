using AirGlass.Capture;
using AirGlass.Encoding;

namespace AirGlass.Streaming;

/// <summary>
/// Orchestrates the pipeline: Capture → Encode → WebRTC
/// </summary>
public class VideoPipeline
{
    private readonly ScreenCapture _capturer;
    private readonly H264Encoder _encoder;

    public VideoPipeline(int width, int height)
    {
        _capturer = new ScreenCapture();
        _encoder = new H264Encoder(width, height);

        _capturer.FrameCaptured += OnFrameCaptured;
        _encoder.FrameEncoded += OnFrameEncoded;
    }

    public void Start()
    {
        _capturer.StartCapture();
    }

    public void Stop()
    {
        _capturer.StopCapture();
    }

    private void OnFrameCaptured(object? sender, FrameCapturedEventArgs e)
    {
        _encoder.EncodeFrame(e.FrameData);
    }

    private void OnFrameEncoded(object? sender, EncodedFrameEventArgs e)
    {
        // TODO: Send to WebRTC peer
    }
}
