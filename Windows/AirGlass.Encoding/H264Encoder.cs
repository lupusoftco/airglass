namespace AirGlass.Encoding;

/// <summary>
/// H.264 video encoder using Windows MediaFoundation
/// Input: Raw frames from ScreenCapture
/// Output: H.264 bitstream for WebRTC
/// </summary>
public class H264Encoder
{
    private int _width;
    private int _height;
    private int _bitrateKbps;

    public event EventHandler<EncodedFrameEventArgs>? FrameEncoded;

    public H264Encoder(int width, int height, int bitrateKbps = 5000)
    {
        _width = width;
        _height = height;
        _bitrateKbps = bitrateKbps;
        // TODO: Initialize MediaFoundation encoder
    }

    public void EncodeFrame(byte[] rawFrame)
    {
        // TODO: Send frame to H.264 encoder
        // Get bitstream from encoder
        // Raise FrameEncoded event
    }
}

public class EncodedFrameEventArgs : EventArgs
{
    public byte[] EncodedData { get; set; } = Array.Empty<byte>();
    public bool IsKeyFrame { get; set; }
}
