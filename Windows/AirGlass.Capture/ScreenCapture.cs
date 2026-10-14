using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using SharpDX;
using SharpDX.DXGI;
using SharpDX.Direct3D11;

namespace AirGlass.Capture;

/// <summary>
/// Screen capture using Windows.Graphics.Capture API
/// Captures frames at 60 FPS and converts them to raw BGRA format for encoding.
/// </summary>
public class ScreenCapture : IDisposable
{
    private const int TargetFrameRateHz = 60;
    private const int FrameIntervalMs = 1000 / TargetFrameRateHz; // ~16.67 ms

    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;
    private SharpDX.Direct3D11.Device? _device;
    private DeviceContext? _deviceContext;

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    private int _width;
    private int _height;
    private bool _isCapturing;
    private object _lockObject = new();

    public event EventHandler<FrameCapturedEventArgs>? FrameCaptured;

    /// <summary>
    /// Initializes the screen capture with the specified dimensions.
    /// If not called, will auto-detect from the primary display.
    /// </summary>
    public void Initialize(int width = 1920, int height = 1080)
    {
        lock (_lockObject)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Cannot initialize while capturing");

            _width = width;
            _height = height;

            // Create Direct3D 11 device
            var dxgiFactory = new Factory1();
            var dxgiAdapter = dxgiFactory.GetAdapter1(0);

            var device = new SharpDX.Direct3D11.Device(dxgiAdapter);
            _device = device;
            _deviceContext = device.ImmediateContext;
        }
    }

    /// <summary>
    /// Begins continuous screen capture at 60 FPS.
    /// </summary>
    public void StartCapture()
    {
        lock (_lockObject)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Capture already running");

            // Initialize if not already done
            if (_device == null)
                Initialize();

            _isCapturing = true;
            _captureCts = new CancellationTokenSource();

            // Create frame pool for capturing
            _framePool = Direct3D11CaptureFramePool.CreateCaptureFramePool(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2, // number of buffers
                new SizeInt32 { Width = _width, Height = _height }
            );

            _framePool.FrameArrived += OnFrameArrived;

            // Create capture session from primary display
            var displayItem = GetPrimaryDisplayItem();
            if (displayItem == null)
                throw new InvalidOperationException("No display found");

            _session = _framePool.CreateCaptureSession(displayItem);
            _session.IsCursorCaptureEnabled = false;

            // Start background capture thread
            _captureTask = Task.Run(() => CaptureLoop(_captureCts.Token), _captureCts.Token);
        }
    }

    /// <summary>
    /// Stops the continuous screen capture.
    /// </summary>
    public void StopCapture()
    {
        lock (_lockObject)
        {
            if (!_isCapturing)
                return;

            _isCapturing = false;

            // Stop the session
            if (_session != null)
            {
                _session.IsCursorCaptureEnabled = false;
                _session = null;
            }

            // Cancel the capture task
            _captureCts?.Cancel();
            try
            {
                _captureTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }

            // Clean up frame pool
            if (_framePool != null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool?.Dispose();
                _framePool = null;
            }
        }
    }

    /// <summary>
    /// Captures a single frame synchronously (blocking).
    /// </summary>
    public byte[]? CaptureFrame()
    {
        if (_framePool == null || _device == null)
            return null;

        try
        {
            using var frame = _framePool.TryGetNextFrame();
            if (frame == null)
                return null;

            return ExtractFrameBytes(frame);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error capturing frame: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the primary display item for capture.
    /// </summary>
    private GraphicsCaptureItem? GetPrimaryDisplayItem()
    {
        try
        {
            // Get all display targets
            var displayItems = Windows.Graphics.Capture.GraphicsCapturePicker.CreateDefault()
                .PickSingleItemAsync()
                .AsTask()
                .Result;

            return displayItems;
        }
        catch
        {
            // Fallback: try to get primary display from DXGI
            return CreateDisplayItemFromDxgi(0);
        }
    }

    /// <summary>
    /// Creates a GraphicsCaptureItem from DXGI display adapter.
    /// </summary>
    private GraphicsCaptureItem? CreateDisplayItemFromDxgi(int adapterIndex)
    {
        try
        {
            using var factory = new Factory1();
            using var adapter = factory.GetAdapter1(adapterIndex);
            using var output = adapter.GetOutput(0);

            // Get the monitor handle
            var desc = output.Description;

            // For now, we'll let Windows.Graphics.Capture handle display enumeration
            // This requires UI interaction via GraphicsCapturePicker
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Background capture loop that maintains ~60 FPS.
    /// </summary>
    private async void CaptureLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long frameCount = 0;

        try
        {
            while (!ct.IsCancellationRequested && _isCapturing)
            {
                var frameTimeMs = (int)sw.ElapsedMilliseconds;
                long targetMs = (frameCount + 1) * FrameIntervalMs;
                long delayMs = targetMs - frameTimeMs;

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }

                // Try to get frame
                try
                {
                    var frameData = CaptureFrame();
                    if (frameData != null)
                    {
                        var timestamp = sw.ElapsedMilliseconds;
                        FrameCaptured?.Invoke(this, new FrameCapturedEventArgs
                        {
                            FrameData = frameData,
                            Width = _width,
                            Height = _height,
                            TimestampMs = timestamp
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Frame capture error: {ex.Message}");
                }

                frameCount++;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        finally
        {
            sw.Stop();
        }
    }

    /// <summary>
    /// Handles frame arrival from the capture frame pool.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Frame is handled in CaptureLoop via TryGetNextFrame
    }

    /// <summary>
    /// Extracts raw BGRA bytes from a Direct3D 11 captured frame.
    /// </summary>
    private byte[] ExtractFrameBytes(Direct3D11CaptureFrame frame)
    {
        var surface = frame.Surface;
        using var dxgiSurface = surface.As<SharpDX.DXGI.Surface>();
        using var d3dSurface = dxgiSurface.As<Texture2D>();

        // Get texture description
        var desc = d3dSurface.Description;

        // Create a staging texture for reading
        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        };

        using var stagingTexture = new Texture2D(_device, stagingDesc);
        _deviceContext?.CopyResource(d3dSurface, stagingTexture);

        // Read the texture data
        var box = _deviceContext?.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);
        if (box == null)
            throw new InvalidOperationException("Failed to map staging texture");

        var rowPitch = box.Value.RowPitch;
        var totalBytes = rowPitch * desc.Height;
        var frameBytes = new byte[totalBytes];

        // Copy row by row (handles pitch)
        unsafe
        {
            var ptr = (byte*)box.Value.DataPointer.ToPointer();
            for (int y = 0; y < desc.Height; y++)
            {
                Marshal.Copy(
                    new IntPtr(ptr + y * rowPitch),
                    frameBytes,
                    y * rowPitch,
                    rowPitch
                );
            }
        }

        _deviceContext?.UnmapSubresource(stagingTexture, 0);

        return frameBytes;
    }

    public void Dispose()
    {
        StopCapture();

        lock (_lockObject)
        {
            _deviceContext?.Dispose();
            _device?.Dispose();
            _captureCts?.Dispose();
        }
    }
}

/// <summary>
/// EventArgs for frame capture events.
/// </summary>
public class FrameCapturedEventArgs : EventArgs
{
    /// <summary>
    /// Raw BGRA frame data.
    /// </summary>
    public byte[] FrameData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Frame width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Frame height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Frame timestamp in milliseconds (from capture start).
    /// </summary>
    public long TimestampMs { get; set; }
}
