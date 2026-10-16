using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AirGlass.Support;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace AirGlass.Capture;

/// <summary>What is being captured.</summary>
public sealed record CaptureSource(bool IsWindow, string Name);

public abstract record CaptureState
{
    public sealed record Idle : CaptureState;
    public sealed record Capturing(CaptureSource Source) : CaptureState;
    public sealed record Failed(string Message) : CaptureState;
}

/// <summary>Receives every captured frame on the capture thread; must copy
/// what it needs and return quickly.</summary>
public interface IFrameConsumer
{
    void Consume(ID3D11Texture2D texture, int width, int height);
    void ClearLastFrame();
}

/// <summary>
/// Owns the Windows.Graphics.Capture session. The whole screen is captured
/// directly; a window (or, with several monitors, a screen) is chosen with
/// the system capture picker. Frames go to the consumer (the video pipeline).
/// State changes are reported on the UI thread.
/// </summary>
public sealed class ScreenCapturer
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly IFrameConsumer _consumer;
    private readonly IDirect3DDevice _device;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly object _gate = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private CaptureState _state = new CaptureState.Idle();

    public ScreenCapturer(ID3D11Device device, IFrameConsumer consumer)
    {
        _consumer = consumer;
        _device = Direct3D.CreateWinRtDevice(device);
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<CaptureState>? StateChanged;

    public CaptureState State => _state;

    /// <summary>The window that owns the system picker.</summary>
    public IntPtr OwnerWindow { get; set; }

    public async Task ChooseAsync(CaptureMode mode)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            Report(new CaptureState.Failed("Bu Windows sürümünde ekran yakalama desteklenmiyor"));
            return;
        }

        GraphicsCaptureItem? item;
        bool isWindow;
        try
        {
            if (mode == CaptureMode.FullScreen && System.Windows.Forms.Screen.AllScreens.Length == 1)
            {
                item = Direct3D.CreateItemForMonitor(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));
                isWindow = false;
            }
            else
            {
                // The system picker lists windows and screens; with several
                // monitors it is also how a screen is chosen.
                var picker = new GraphicsCapturePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerWindow);
                item = await picker.PickSingleItemAsync();
                if (item == null) return; // Cancelled: keep whatever was shown.
                isWindow = mode == CaptureMode.Window;
            }
        }
        catch (Exception error)
        {
            Log.Capture($"Choosing a source failed: {error}");
            Report(new CaptureState.Failed("Kaynak seçilemedi"));
            return;
        }

        Start(item, isWindow);
    }

    public void Stop()
    {
        StopSession();
        Report(new CaptureState.Idle());
    }

    // MARK: - Private

    private void Start(GraphicsCaptureItem item, bool isWindow)
    {
        StopSession();
        try
        {
            lock (_gate)
            {
                _item = item;
                _poolSize = item.Size;
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 2, item.Size);
                _framePool.FrameArrived += OnFrameArrived;
                _session = _framePool.CreateCaptureSession(item);
                _session.IsCursorCaptureEnabled = true;
                // Windows 11: no yellow frame around what is captured.
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                {
                    try
                    {
                        _session.IsBorderRequired = false;
                    }
                    catch (Exception error)
                    {
                        Log.Capture($"Borderless capture unavailable: {error.Message}");
                    }
                }
                item.Closed += OnItemClosed;
                _session.StartCapture();
            }
        }
        catch (Exception error)
        {
            Log.Capture($"Starting capture failed: {error}");
            StopSession();
            Report(new CaptureState.Failed("Yakalama başlatılamadı"));
            return;
        }

        Log.Capture($"Capturing {(isWindow ? "window" : "screen")} \"{item.DisplayName}\" {item.Size.Width}×{item.Size.Height}");
        Report(new CaptureState.Capturing(new CaptureSource(isWindow, item.DisplayName)));
    }

    private void StopSession()
    {
        lock (_gate)
        {
            if (_item != null) _item.Closed -= OnItemClosed;
            if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            _item = null;
        }
        _consumer.ClearLastFrame();
    }

    /// <summary>The captured window was closed.</summary>
    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Log.Capture("Capture item closed");
        Post(() =>
        {
            if (_item != sender) return;
            Stop();
        });
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;

            // A resized window: frames keep the old size until the pool is recreated.
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                lock (_gate)
                {
                    if (_framePool != sender) return;
                    _poolSize = size;
                    sender.Recreate(_device, PixelFormat, 2, size);
                }
            }
            if (size.Width < 2 || size.Height < 2) return;

            using var texture = Direct3D.TextureFrom(frame.Surface);
            _consumer.Consume(texture, size.Width, size.Height);
        }
        catch (Exception error)
        {
            Log.Capture($"Frame handling failed: {error.Message}");
        }
    }

    private void Report(CaptureState state) => Post(() =>
    {
        _state = state;
        StateChanged?.Invoke(state);
    });

    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
}
