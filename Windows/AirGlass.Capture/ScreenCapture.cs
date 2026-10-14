namespace AirGlass.Capture;

/// <summary>
/// Screen capture using Windows.Graphics.Capture API
/// Phase 1: Single frame capture proof-of-concept
/// Phase 2: 60 FPS capture loop
/// </summary>
public class ScreenCapture
{
    public event EventHandler<FrameCapturedEventArgs>? FrameCaptured;

    public void CaptureFrame()
    {
        // TODO: Use Windows.Graphics.Capture to grab frame
        // Convert to H.264 suitable format
    }

    public void StartCapture()
    {
        // TODO: Begin 60 FPS capture loop
    }

    public void StopCapture()
    {
        // TODO: Stop capture loop
    }
}

public class FrameCapturedEventArgs : EventArgs
{
    public byte[] FrameData { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
}
