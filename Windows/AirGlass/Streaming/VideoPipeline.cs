using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AirGlass.Capture;
using AirGlass.Support;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirGlass.Streaming;

/// <summary>
/// Turns captured frames into H.264 for the viewer, like the macOS app's
/// video pipeline: frames are scaled to at most 1920 px on the long edge and
/// converted to NV12 on the GPU, then encoded on a worker thread at up to
/// 60 fps. Encoding only runs while a viewer is connected. On a still screen
/// the last frame is re-sent every 0.5 s, so a newly joined viewer gets a
/// picture and lost keyframes are refreshed.
/// </summary>
public sealed class VideoPipeline : IFrameConsumer, IDisposable
{
    /// <summary>Long edge cap; keeps the encode cheap and phone-friendly.</summary>
    public const int MaxPixelEdge = 1920;
    /// <summary>Smooth scrolling and cursor movement; idle screens cost nothing.</summary>
    public const int FramesPerSecond = 60;
    /// <summary>Generous for a LAN.</summary>
    public const int Bitrate = 8_000_000;
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);

    private readonly ID3D11DeviceContext _context;
    /// <summary>Null without GPU video processing (e.g. remote desktop or the
    /// basic display driver); frames are then converted on the CPU.</summary>
    private readonly ID3D11VideoDevice? _videoDevice;
    private readonly ID3D11VideoContext? _videoContext;
    /// <summary>Guards the latest frame and the GPU work on it.</summary>
    private readonly object _gpu = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ID3D11Texture2D? _latest;
    private int _latestWidth, _latestHeight;
    private bool _hasNewFrame;
    private IConverter? _converter;
    private H264Encoder? _encoder;
    private TimeSpan _lastEncoded = TimeSpan.MinValue;
    private volatile bool _isActive;
    private volatile bool _keyFrameRequested;
    private volatile bool _isDisposed;

    public VideoPipeline()
    {
        Device = Direct3D.CreateDevice();
        _context = Device.ImmediateContext;
        _videoDevice = Device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = _videoDevice == null ? null : _context.QueryInterfaceOrNull<ID3D11VideoContext>();
        if (_videoContext == null)
        {
            _videoDevice?.Dispose();
            _videoDevice = null;
            Log.Capture("No GPU video processor; converting frames on the CPU");
        }
        _worker = new Thread(Run) { IsBackground = true, Name = "AirGlass video", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    public ID3D11Device Device { get; }

    /// <summary>An encoded access unit and the time since the previous one;
    /// raised on the worker thread.</summary>
    public event Action<byte[], TimeSpan>? FrameEncoded;

    /// <summary>True while a viewer is connected and frames should be encoded.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value) return;
            _keyFrameRequested = true;
            _wake.Set();
        }
    }

    public void RequestKeyFrame()
    {
        _keyFrameRequested = true;
        _wake.Set();
    }

    // MARK: - IFrameConsumer (capture thread)

    public void Consume(ID3D11Texture2D texture, int width, int height)
    {
        lock (_gpu)
        {
            var description = texture.Description;
            if (_latest == null || _latest.Description.Width != description.Width
                || _latest.Description.Height != description.Height || _latest.Description.Format != description.Format)
            {
                _converter?.Dispose();
                _converter = null;
                _latest?.Dispose();
                var copy = new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = description.Format,
                    SampleDescription = SampleDescription.Default,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                };
                _latest = Device.CreateTexture2D(in copy);
            }
            _context.CopyResource(_latest, texture);
            _latestWidth = Math.Min(width, (int)description.Width);
            _latestHeight = Math.Min(height, (int)description.Height);
            _hasNewFrame = true;
        }
        _wake.Set();
    }

    /// <summary>Forgets the last frame, e.g. when capture stops, so nothing
    /// stale keeps being sent.</summary>
    public void ClearLastFrame()
    {
        lock (_gpu)
        {
            _converter?.Dispose();
            _converter = null;
            _latest?.Dispose();
            _latest = null;
            _hasNewFrame = false;
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _wake.Set();
    }

    // MARK: - Worker

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        while (!_isDisposed)
        {
            _wake.WaitOne(RepeatInterval);
            if (_isDisposed || !_isActive) continue;
            try
            {
                EncodeLatest();
            }
            catch (Exception error)
            {
                Log.Capture($"Encoding failed: {error}");
                lock (_gpu)
                {
                    _converter?.Dispose();
                    _converter = null;
                }
                _encoder?.Dispose();
                _encoder = null;
                Thread.Sleep(1000);
            }
        }
        _encoder?.Dispose();
    }

    private void EncodeLatest()
    {
        // At most 60 fps: a faster screen (e.g. 144 Hz) gets its newest frame sent.
        var sinceLast = _clock.Elapsed - _lastEncoded;
        if (sinceLast < FrameInterval) Thread.Sleep(FrameInterval - sinceLast);

        byte[] nv12;
        int width, height;
        lock (_gpu)
        {
            if (_latest == null) return;
            if (!_hasNewFrame && !_keyFrameRequested && _clock.Elapsed - _lastEncoded < RepeatInterval) return;
            _hasNewFrame = false;

            (width, height) = OutputSize(_latestWidth, _latestHeight);
            if (_converter == null || !_converter.Matches(_latest, _latestWidth, _latestHeight, width, height))
            {
                _converter?.Dispose();
                _converter = _videoDevice != null && _videoContext != null
                    ? new GpuConverter(Device, _context, _videoDevice, _videoContext, _latest, _latestWidth, _latestHeight, width, height)
                    : new CpuConverter(Device, _context, _latest, _latestWidth, _latestHeight, width, height);
            }
            nv12 = _converter.Convert();
        }

        if (_encoder == null || _encoder.Width != width || _encoder.Height != height)
        {
            _encoder?.Dispose();
            _encoder = new H264Encoder(width, height, FramesPerSecond, Bitrate);
        }
        if (_keyFrameRequested)
        {
            _keyFrameRequested = false;
            _encoder.RequestKeyFrame();
        }

        var now = _clock.Elapsed;
        var gap = _lastEncoded == TimeSpan.MinValue ? FrameInterval : now - _lastEncoded;
        _lastEncoded = now;
        foreach (var unit in _encoder.Encode(nv12, now.Ticks, FrameInterval.Ticks))
        {
            FrameEncoded?.Invoke(unit, gap);
        }
    }

    /// <summary>Fits the long edge into <see cref="MaxPixelEdge"/>; even sizes for 4:2:0.</summary>
    private static (int Width, int Height) OutputSize(int width, int height)
    {
        var fit = Math.Min(1.0, MaxPixelEdge / (double)Math.Max(width, height));
        return (Math.Max(2, (int)(width * fit) & ~1), Math.Max(2, (int)(height * fit) & ~1));
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    /// <summary>Scales and converts the latest BGRA frame to tightly packed NV12.</summary>
    private interface IConverter : IDisposable
    {
        bool Matches(ID3D11Texture2D source, int sourceWidth, int sourceHeight, int width, int height);
        byte[] Convert();
    }

    /// <summary>
    /// Scales and converts one BGRA texture to NV12 with the GPU's video
    /// processor and reads the result back for the encoder.
    /// </summary>
    private sealed class GpuConverter : IConverter
    {
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoContext _videoContext;
        private readonly ID3D11Texture2D _source;
        private readonly int _sourceWidth, _sourceHeight, _width, _height;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11VideoProcessorInputView _inputView;
        private readonly ID3D11Texture2D _output;
        private readonly ID3D11VideoProcessorOutputView _outputView;
        private readonly ID3D11Texture2D _staging;
        private readonly byte[] _frame;

        public GpuConverter(ID3D11Device device, ID3D11DeviceContext context, ID3D11VideoDevice videoDevice,
            ID3D11VideoContext videoContext, ID3D11Texture2D source, int sourceWidth, int sourceHeight, int width, int height)
        {
            _context = context;
            _videoContext = videoContext;
            _source = source;
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            _width = width;
            _height = height;
            _frame = new byte[width * height * 3 / 2];

            var sourceDescription = source.Description;
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(FramesPerSecond, 1),
                InputWidth = sourceDescription.Width,
                InputHeight = sourceDescription.Height,
                OutputFrameRate = new Rational(FramesPerSecond, 1),
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.OptimalQuality,
            };
            _enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
            _processor = videoDevice.CreateVideoProcessor(_enumerator, 0);

            _inputView = videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

            var outputDescription = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = SampleDescription.Default,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            };
            _output = device.CreateTexture2D(in outputDescription);
            _outputView = videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });

            var stagingDescription = outputDescription;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.BindFlags = BindFlags.None;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            _staging = device.CreateTexture2D(in stagingDescription);

            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, width, height));
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, width, height));
        }

        public bool Matches(ID3D11Texture2D source, int sourceWidth, int sourceHeight, int width, int height) =>
            source == _source && sourceWidth == _sourceWidth && sourceHeight == _sourceHeight
            && width == _width && height == _height;

        /// <summary>The current source frame as tightly packed NV12.</summary>
        public byte[] Convert()
        {
            var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, new[] { stream }).CheckError();
            _context.CopyResource(_staging, _output);

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pitch = (int)mapped.RowPitch;
                // Luma rows, then the interleaved chroma plane right after them.
                for (var y = 0; y < _height; y++)
                    Marshal.Copy(mapped.DataPointer + y * pitch, _frame, y * _width, _width);
                var chroma = mapped.DataPointer + pitch * _height;
                for (var y = 0; y < _height / 2; y++)
                    Marshal.Copy(chroma + y * pitch, _frame, _width * _height + y * _width, _width);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
            return _frame;
        }

        public void Dispose()
        {
            _staging.Dispose();
            _outputView.Dispose();
            _output.Dispose();
            _inputView.Dispose();
            _processor.Dispose();
            _enumerator.Dispose();
        }
    }

    /// <summary>
    /// The fallback without GPU video processing: reads the BGRA frame back
    /// and scales (averaging 2×2 source pixels) and converts it on the CPU,
    /// with the same BT.601 studio-range result as the video processor.
    /// </summary>
    private sealed unsafe class CpuConverter : IConverter
    {
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Texture2D _source;
        private readonly int _sourceWidth, _sourceHeight, _width, _height;
        private readonly ID3D11Texture2D _staging;
        private readonly byte[] _frame;

        public CpuConverter(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source,
            int sourceWidth, int sourceHeight, int width, int height)
        {
            _context = context;
            _source = source;
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            _width = width;
            _height = height;
            _frame = new byte[width * height * 3 / 2];

            var description = source.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            _staging = device.CreateTexture2D(in description);
        }

        public bool Matches(ID3D11Texture2D source, int sourceWidth, int sourceHeight, int width, int height) =>
            source == _source && sourceWidth == _sourceWidth && sourceHeight == _sourceHeight
            && width == _width && height == _height;

        public byte[] Convert()
        {
            _context.CopyResource(_staging, _source);
            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = (byte*)mapped.DataPointer;
                var pitch = (int)mapped.RowPitch;
                fixed (byte* frame = _frame)
                {
                    var chroma = frame + _width * _height;
                    for (var y = 0; y < _height; y += 2)
                    for (var x = 0; x < _width; x += 2)
                    {
                        int sumR = 0, sumG = 0, sumB = 0;
                        for (var dy = 0; dy < 2; dy++)
                        for (var dx = 0; dx < 2; dx++)
                        {
                            Sample(pixels, pitch, x + dx, y + dy, out var r, out var g, out var b);
                            frame[(y + dy) * _width + x + dx] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                            sumR += r;
                            sumG += g;
                            sumB += b;
                        }
                        int red = sumR >> 2, green = sumG >> 2, blue = sumB >> 2;
                        var uv = chroma + (y / 2) * _width + x;
                        uv[0] = (byte)(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                        uv[1] = (byte)(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
                    }
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
            return _frame;
        }

        /// <summary>The average of the 2×2 source pixels under output pixel (x, y).</summary>
        private void Sample(byte* pixels, int pitch, int x, int y, out int r, out int g, out int b)
        {
            var sourceX = Math.Min((int)((x + 0.5) * _sourceWidth / _width - 0.5), _sourceWidth - 2);
            var sourceY = Math.Min((int)((y + 0.5) * _sourceHeight / _height - 0.5), _sourceHeight - 2);
            sourceX = Math.Max(sourceX, 0);
            sourceY = Math.Max(sourceY, 0);
            var top = pixels + sourceY * pitch + sourceX * 4;
            var bottom = top + pitch;
            // BGRA
            b = (top[0] + top[4] + bottom[0] + bottom[4]) >> 2;
            g = (top[1] + top[5] + bottom[1] + bottom[5]) >> 2;
            r = (top[2] + top[6] + bottom[2] + bottom[6]) >> 2;
        }

        public void Dispose() => _staging.Dispose();
    }
}
