using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AirGlass.Support;
using Vortice.MediaFoundation;

namespace AirGlass.Streaming;

/// <summary>
/// Windows' built-in H.264 encoder (a Media Foundation transform), set up
/// like the macOS app's encoder: Constrained Baseline, which every iPhone
/// decodes in hardware, low latency, no B-frames. Takes NV12 frames, returns
/// Annex B access units. Use from one thread.
/// </summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    // Media Foundation attribute keys and values (mfapi.h, codecapi.h).
    private static readonly Guid MajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid Subtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid FrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid FrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    private static readonly Guid PixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    private static readonly Guid InterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    private static readonly Guid AvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid Mpeg2Profile = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    private static readonly Guid SequenceHeader = new("3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3");
    private static readonly Guid VideoMediaType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid H264Format = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12Format = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid LowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid GopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid BPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid ForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    private static readonly Guid MicrosoftH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid TransformId = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid CodecApiId = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");

    private const int ProgressiveInterlace = 2;
    private const int BaselineProfile = 66;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int ProvidesSamples = 0x100;

    private static bool _isStarted;

    private readonly IMFTransform _transform;
    private readonly IntPtr _codecApi;
    private readonly int _outputBufferSize;
    private readonly bool _transformProvidesSamples;
    private byte[]? _sequenceHeader;
    private volatile bool _keyFrameRequested = true;

    public H264Encoder(int width, int height, int framesPerSecond, int bitrate)
    {
        Width = width;
        Height = height;
        if (!_isStarted)
        {
            MediaFactory.MFStartup(true).CheckError();
            _isStarted = true;
        }

        _transform = new IMFTransform(CreateInstance(MicrosoftH264Encoder, TransformId));
        _codecApi = _transform.QueryInterface(CodecApiId, out var codecApi).Success ? codecApi : IntPtr.Zero;
        SetCodecValue(LowLatencyMode, true);
        SetCodecValue(BPictureCount, 0u);
        // A keyframe at least every 2 s; the viewer also asks for one after loss.
        SetCodecValue(GopSize, (uint)(framesPerSecond * 2));

        // Output first: the encoder only offers input types once it has one.
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MajorType, VideoMediaType);
            output.Set(Subtype, H264Format);
            output.Set(AvgBitrate, (uint)bitrate);
            output.Set(FrameSize, Pack(width, height));
            output.Set(FrameRate, Pack(framesPerSecond, 1));
            output.Set(PixelAspectRatio, Pack(1, 1));
            output.Set(InterlaceMode, (uint)ProgressiveInterlace);
            output.Set(Mpeg2Profile, (uint)BaselineProfile);
            _transform.SetOutputType(0, output, 0);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MajorType, VideoMediaType);
            input.Set(Subtype, Nv12Format);
            input.Set(FrameSize, Pack(width, height));
            input.Set(FrameRate, Pack(framesPerSecond, 1));
            input.Set(PixelAspectRatio, Pack(1, 1));
            input.Set(InterlaceMode, (uint)ProgressiveInterlace);
            _transform.SetInputType(0, input, 0);
        }

        var info = _transform.GetOutputStreamInfo(0);
        _transformProvidesSamples = (info.Flags & ProvidesSamples) != 0;
        _outputBufferSize = Math.Max(info.Size, width * height * 3 / 2);
        ReadSequenceHeader();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Log.Capture($"H.264 encoder ready: {width}×{height} @ {framesPerSecond} fps, {bitrate / 1000} kbit/s");
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The next frame becomes a keyframe (a new viewer, or the viewer
    /// lost packets).</summary>
    public void RequestKeyFrame() => _keyFrameRequested = true;

    /// <summary>Encodes one NV12 frame (Width × Height, tightly packed).</summary>
    public List<byte[]> Encode(byte[] nv12, long time100ns, long duration100ns)
    {
        if (_keyFrameRequested)
        {
            _keyFrameRequested = false;
            SetCodecValue(ForceKeyFrame, 1u);
        }

        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(nv12, 0, pointer, nv12.Length);
            buffer.Unlock();
            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = time100ns;
            sample.SampleDuration = duration100ns;
            _transform.ProcessInput(0, sample, 0);
        }
        return Drain();
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (Exception)
        {
        }
        if (_codecApi != IntPtr.Zero) Marshal.Release(_codecApi);
        _transform.Dispose();
    }

    // MARK: - Private

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    private List<byte[]> Drain()
    {
        var units = new List<byte[]>();
        while (true)
        {
            using var ownSample = _transformProvidesSamples ? null : MediaFactory.MFCreateSample();
            using var ownBuffer = _transformProvidesSamples ? null : MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            if (ownSample != null) ownSample.AddBuffer(ownBuffer!);

            var output = new OutputDataBuffer { Sample = ownSample?.NativePointer ?? IntPtr.Zero };
            uint status = 0;
            // IMFTransform::ProcessOutput, called directly so that sample
            // ownership stays explicit.
            var processOutput = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)
                (*(IntPtr**)_transform.NativePointer)[25];
            var hr = processOutput(_transform.NativePointer, 0, 1, &output, &status);
            if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);

            if (hr == NeedMoreInput) break;
            if (hr == StreamChange)
            {
                // The encoder settled on its output type; accept it and go on.
                using var type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                ReadSequenceHeader();
                continue;
            }
            Marshal.ThrowExceptionForHR(hr);

            var sample = ownSample ?? new IMFSample(output.Sample);
            try
            {
                using var contiguous = sample.ConvertToContiguousBuffer();
                contiguous.Lock(out var pointer, out _, out var length);
                var data = new byte[length];
                Marshal.Copy(pointer, data, 0, length);
                contiguous.Unlock();
                units.Add(WithParameterSets(data));
            }
            finally
            {
                if (ownSample == null) sample.Dispose();
            }
        }
        return units;
    }

    /// <summary>Makes sure every IDR frame carries SPS and PPS, so a viewer can
    /// start decoding from any keyframe.</summary>
    private byte[] WithParameterSets(byte[] unit)
    {
        if (_sequenceHeader == null || !H264.ContainsNal(unit, 5) || H264.ContainsNal(unit, 7)) return unit;
        var combined = new byte[_sequenceHeader.Length + unit.Length];
        _sequenceHeader.CopyTo(combined, 0);
        unit.CopyTo(combined, _sequenceHeader.Length);
        return combined;
    }

    private void ReadSequenceHeader()
    {
        try
        {
            using var type = _transform.GetOutputCurrentType(0);
            var header = type.GetBlob(SequenceHeader);
            if (header is { Length: > 0 }) _sequenceHeader = header;
        }
        catch (Exception)
        {
            // Not known yet; in-band parameter sets are used as they come.
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    // MARK: - ICodecAPI

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public short Bool;
    }

    private void SetCodecValue(Guid api, uint value) =>
        SetCodecValue(api, new Variant { Type = 19 /* VT_UI4 */, UInt32 = value });

    private void SetCodecValue(Guid api, bool value) =>
        SetCodecValue(api, new Variant { Type = 11 /* VT_BOOL */, Bool = (short)(value ? -1 : 0) });

    private void SetCodecValue(Guid api, Variant value)
    {
        if (_codecApi == IntPtr.Zero) return;
        // ICodecAPI::SetValue
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)(*(IntPtr**)_codecApi)[9];
        var hr = setValue(_codecApi, &api, &value);
        if (hr < 0) Log.Capture($"Encoder setting {api} rejected: 0x{hr:X8}");
    }

    private static IntPtr CreateInstance(Guid classId, Guid interfaceId)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref interfaceId, out var instance));
        return instance;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
}

internal static class H264
{
    /// <summary>True if an Annex B access unit contains a NAL unit of the given type
    /// (5 IDR slice, 7 SPS, 8 PPS).</summary>
    public static bool ContainsNal(byte[] unit, int type)
    {
        for (var i = 0; i + 3 < unit.Length; i++)
        {
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1)
            {
                if ((unit[i + 3] & 0x1F) == type) return true;
                i += 2;
            }
        }
        return false;
    }
}
