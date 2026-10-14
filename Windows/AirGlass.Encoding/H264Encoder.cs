using System.Diagnostics;
using SharpDX;
using SharpDX.MediaFoundation;

namespace AirGlass.Encoding;

/// <summary>
/// H.264 video encoder using Windows MediaFoundation.
/// Encodes raw BGRA frames to H.264 bitstream suitable for WebRTC.
/// </summary>
public class H264Encoder : IDisposable
{
    private const int KeyFrameIntervalMs = 2000; // Key frame every 2 seconds
    private const long HnsPerSecond = 10_000_000;

    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrateKbps;

    private IMFTransform? _encoder;
    private IMFMediaType? _inputMediaType;
    private IMFMediaType? _outputMediaType;

    private long _sampleTimestamp; // In 100-nanosecond units (HNS)
    private long _nextKeyFrameTime;
    private long _frameNumber;

    private object _lockObject = new();
    private bool _initialized;

    public event EventHandler<EncodedFrameEventArgs>? FrameEncoded;

    /// <summary>
    /// Initializes the H.264 encoder with the specified parameters.
    /// </summary>
    public H264Encoder(int width, int height, int bitrateKbps = 5000)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive");
        if (bitrateKbps <= 0)
            throw new ArgumentException("Bitrate must be positive");

        _width = width;
        _height = height;
        _bitrateKbps = bitrateKbps;

        _sampleTimestamp = 0;
        _nextKeyFrameTime = KeyFrameIntervalMs * HnsPerSecond / 1000;
        _frameNumber = 0;
        _initialized = false;
    }

    /// <summary>
    /// Initializes the MediaFoundation encoder.
    /// Must be called before EncodeFrame().
    /// </summary>
    public void Initialize()
    {
        lock (_lockObject)
        {
            if (_initialized)
                return;

            MediaManager.Startup();

            try
            {
                // Create H.264 encoder
                _encoder = MediaFactory.CreateTransformByName(
                    "H264 Video Encoder MFT",
                    null
                ) as IMFTransform;

                if (_encoder == null)
                    throw new InvalidOperationException("H.264 encoder not available");

                // Set up input media type (BGRA)
                _inputMediaType = MediaFactory.CreateMediaType();
                _inputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                _inputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.RGB32);
                _inputMediaType.Set(MediaTypeAttributeKeys.FrameSize, ((long)_width << 32) | (uint)_height);
                _inputMediaType.Set(MediaTypeAttributeKeys.FrameRate, (long)(30 << 32) | 1);
                _inputMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (long)1 << 32 | 1);

                _encoder.SetInputType(0, _inputMediaType, 0);

                // Set up output media type (H.264)
                _outputMediaType = MediaFactory.CreateMediaType();
                _outputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                _outputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                _outputMediaType.Set(MediaTypeAttributeKeys.FrameSize, ((long)_width << 32) | (uint)_height);
                _outputMediaType.Set(MediaTypeAttributeKeys.FrameRate, (long)(30 << 32) | 1);
                _outputMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, (long)_bitrateKbps * 1000);

                _encoder.SetOutputType(0, _outputMediaType, 0);

                // Configure encoder attributes
                if (_encoder is IMFAttributes attrs)
                {
                    // Request key frame at regular intervals
                    attrs.SetUnsignedInteger(
                        new Guid(0x0d418e5e, 0x1108, 0x4ef7, 0x87, 0x0a, 0x70, 0xa8, 0x0c, 0xff, 0x80, 0x1f),
                        KeyFrameIntervalMs
                    );

                    // Enable low latency mode
                    attrs.SetUnsignedInteger(
                        new Guid(0xfb90680e, 0x51dc, 0x4db5, 0xab, 0xe0, 0x3c, 0xa4, 0x45, 0xfd, 0x69, 0x72),
                        1
                    );
                }

                // Process message
                _encoder.ProcessMessage(MFTMessageType.CommandFlush, IntPtr.Zero);
                _encoder.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
                _encoder.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);

                _initialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize H.264 encoder: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Encodes a raw BGRA frame to H.264.
    /// </summary>
    public void EncodeFrame(byte[] rawFrame)
    {
        if (rawFrame == null)
            throw new ArgumentNullException(nameof(rawFrame));

        lock (_lockObject)
        {
            if (!_initialized)
                Initialize();

            if (_encoder == null)
                throw new InvalidOperationException("Encoder not initialized");

            try
            {
                // Check if we need a key frame
                bool forceKeyFrame = _frameNumber > 0 &&
                    (_sampleTimestamp >= _nextKeyFrameTime);

                if (forceKeyFrame)
                {
                    _frameNumber++;
                    _sampleTimestamp = _frameNumber * (HnsPerSecond / 30); // Assume 30 FPS
                    _nextKeyFrameTime = _sampleTimestamp + (KeyFrameIntervalMs * HnsPerSecond / 1000);
                }
                else
                {
                    _frameNumber++;
                    _sampleTimestamp = _frameNumber * (HnsPerSecond / 30);
                }

                // Create input sample
                using var inputSample = CreateInputSample(rawFrame, _sampleTimestamp, forceKeyFrame);

                // Process input
                int status = 0;
                var result = _encoder.ProcessInput(0, inputSample, 0);
                if (result != 0 && result != SharpDX.MediaFoundation.ResultCode.MFE_NOTACCEPTING)
                {
                    throw new InvalidOperationException($"ProcessInput failed: {result}");
                }

                // Get output
                ProcessEncoderOutput();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Frame encoding error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Creates an IMFSample from raw frame data.
    /// </summary>
    private IMFSample CreateInputSample(byte[] frameData, long timestamp, bool forceKeyFrame)
    {
        var sample = MediaFactory.CreateSample();

        // Create memory buffer
        using var buffer = MediaFactory.CreateMemoryBuffer(frameData.Length);
        buffer.CurrentLength = frameData.Length;

        // Copy frame data into buffer
        unsafe
        {
            fixed (byte* pData = frameData)
            {
                var pBuffer = buffer.Buffer;
                Marshal.Copy(frameData, 0, pBuffer, frameData.Length);
            }
        }

        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = HnsPerSecond / 30; // 30 FPS

        // Set attributes for key frame request
        if (forceKeyFrame)
        {
            var sampleAttrs = sample as IMFAttributes;
            if (sampleAttrs != null)
            {
                sampleAttrs.SetUnsignedInteger(
                    MFSampleAttributeKeys.CleanPoint,
                    1 // Request key frame
                );
            }
        }

        return sample;
    }

    /// <summary>
    /// Processes output from the encoder and raises FrameEncoded events.
    /// </summary>
    private void ProcessEncoderOutput()
    {
        if (_encoder == null)
            return;

        MFTOutputDataBuffer[] outputBuffers = new MFTOutputDataBuffer[1];
        outputBuffers[0].DwStreamID = 0;
        outputBuffers[0].DwStatus = 0;
        outputBuffers[0].PSample = null;

        while (true)
        {
            int status = 0;
            var result = _encoder.ProcessOutput(
                MFTProcessOutputFlags.None,
                1,
                outputBuffers,
                out uint pdwStatus
            );

            if (result == SharpDX.MediaFoundation.ResultCode.MFE_TRANSFORM_STREAM_CHANGE)
            {
                // Output type changed, not typical for H.264
                continue;
            }
            else if (result != 0)
            {
                // No output available or error
                if (outputBuffers[0].PSample != null)
                {
                    var sample = outputBuffers[0].PSample;

                    // Extract H.264 data
                    uint bufferCount = 0;
                    sample.GetBufferCount(out bufferCount);

                    for (uint i = 0; i < bufferCount; i++)
                    {
                        sample.GetBufferByIndex(i, out var buffer);
                        if (buffer != null)
                        {
                            buffer.GetCurrentLength(out uint length);
                            var ptr = buffer.GetBuffer(out _);
                            var encodedData = new byte[length];
                            Marshal.Copy(ptr, encodedData, 0, (int)length);

                            // Check if this is a key frame
                            var sampleAttrs = sample as IMFAttributes;
                            bool isKeyFrame = false;
                            if (sampleAttrs != null)
                            {
                                try
                                {
                                    sampleAttrs.GetUnsignedInteger(MFSampleAttributeKeys.CleanPoint, out uint cleanPoint);
                                    isKeyFrame = cleanPoint != 0;
                                }
                                catch
                                {
                                    // Not a key frame attribute present
                                }
                            }

                            FrameEncoded?.Invoke(this, new EncodedFrameEventArgs
                            {
                                EncodedData = encodedData,
                                IsKeyFrame = isKeyFrame
                            });

                            if (buffer is IDisposable d)
                                d.Dispose();
                        }
                    }

                    if (sample is IDisposable disp)
                        disp.Dispose();
                }
                break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            if (_encoder != null)
            {
                _encoder.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero);
                _encoder.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero);
                _encoder.ProcessMessage(MFTMessageType.CommandFlush, IntPtr.Zero);
            }

            _inputMediaType?.Dispose();
            _outputMediaType?.Dispose();
            _encoder?.Dispose();

            if (_initialized)
            {
                MediaManager.Shutdown();
                _initialized = false;
            }
        }
    }
}

/// <summary>
/// EventArgs for encoded frame events.
/// </summary>
public class EncodedFrameEventArgs : EventArgs
{
    /// <summary>
    /// Encoded H.264 bitstream data (NAL units).
    /// </summary>
    public byte[] EncodedData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Whether this frame is a key frame (I-frame).
    /// </summary>
    public bool IsKeyFrame { get; set; }
}
