using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AirGlass.Streaming;
using AirGlass.Support;
using SIPSorcery.Net;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirGlass;

/// <summary>
/// <c>AirGlass.exe --self-test</c>: exercises the streaming stack without a
/// phone, for CI. H.264 encoding, GPU scaling + encoding, and a WebRTC
/// loopback call that carries encoded frames. Parts the machine lacks (no GPU
/// video support, no Media Foundation) are reported as skipped. The report goes
/// to %LOCALAPPDATA%\AirGlass\self-test.txt; the exit code is 0 unless a part failed.
/// </summary>
internal static class SelfTest
{
    private static readonly List<string> Report = new();
    private static bool _failed;

    public static async Task<int> RunAsync()
    {
        var units = Run("H.264 encoder", EncoderTest) ?? new List<byte[]>();
        await RunAsync("GPU scaling and encoding", PipelineTestAsync);
        await RunAsync("WebRTC loopback", () => LoopbackTestAsync(units));

        var path = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "self-test.txt");
        File.WriteAllLines(path, Report);
        return _failed ? 1 : 0;
    }

    private static List<byte[]> EncoderTest()
    {
        using var encoder = new H264Encoder(640, 360, 60, 2_000_000);
        var units = new List<byte[]>();
        for (var i = 0; i < 30; i++)
        {
            units.AddRange(encoder.Encode(TestFrame(640, 360, i), i * 166_667L, 166_667L));
        }
        Check(units.Count > 0, "no output");
        Check(units.Any(unit => H264.ContainsNal(unit, 5)), "no IDR frame");
        Check(units.Any(unit => H264.ContainsNal(unit, 7) && H264.ContainsNal(unit, 8)), "no SPS/PPS");
        Pass($"{units.Count} access units, {units.Sum(unit => unit.Length)} bytes");
        return units;
    }

    private static async Task PipelineTestAsync()
    {
        using var pipeline = new VideoPipeline();
        var description = new Texture2DDescription
        {
            Width = 2560,
            Height = 1440,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };
        using var texture = pipeline.Device.CreateTexture2D(ref description);
        var encoded = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.FrameEncoded += (unit, _) => encoded.TrySetResult(unit);
        pipeline.IsActive = true;
        pipeline.Consume(texture, 2560, 1440);

        var finished = await Task.WhenAny(encoded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Check(finished == encoded.Task, "no frame encoded within 10 s (see AirGlass.log)");
        Check(H264.ContainsNal(encoded.Task.Result, 5), "first frame is not a keyframe");
        Pass($"2560×1440 → 1920×1080 keyframe, {encoded.Task.Result.Length} bytes");
    }

    private static async Task LoopbackTestAsync(List<byte[]> units)
    {
        if (units.Count == 0)
        {
            // No encoder here: a hand-made IDR slice still tests the transport.
            units.Add(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x1F, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00 });
        }

        var loopback = IPAddress.Loopback;
        using var sender = PeerSession.CreatePeer(loopback);
        using var receiver = new RTCPeerConnection(new RTCConfiguration { X_BindAddress = loopback, X_UseRtpFeedbackProfile = true });
        receiver.addTrack(new MediaStreamTrack(PeerSession.H264Format, MediaStreamStatusEnum.RecvOnly));

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected) connected.TrySetResult();
        };
        receiver.OnVideoFrameReceived += (_, _, frame, _) => received.TrySetResult(frame.Length);

        var offer = sender.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
        await sender.setLocalDescription(offer);
        var offerResult = receiver.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = PeerSession.WithPictureLossFeedback(offer.sdp),
        });
        Check(offerResult == SetDescriptionResultEnum.OK, $"offer rejected: {offerResult}");
        var answer = receiver.createAnswer(new RTCAnswerOptions { X_WaitForIceGatheringToComplete = true });
        await receiver.setLocalDescription(answer);
        var answerResult = sender.setRemoteDescription(answer);
        Check(answerResult == SetDescriptionResultEnum.OK, $"answer rejected: {answerResult}");

        var finished = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Check(finished == connected.Task, $"not connected within 15 s (state {sender.connectionState})");

        for (var i = 0; i < 20 && !received.Task.IsCompleted; i++)
        {
            foreach (var unit in units.Take(5)) sender.SendVideo(1500, unit);
            await Task.Delay(100);
        }
        finished = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Check(finished == received.Task, "no video frame arrived");
        Pass($"connected over DTLS-SRTP, received a {received.Task.Result}-byte frame");
        sender.close();
        receiver.close();
    }

    // MARK: - Helpers

    private sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

    private static string _current = "";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new CheckFailed(message);
    }

    private static void Pass(string detail) => Add($"PASS  {_current}: {detail}");

    private static T? Run<T>(string name, Func<T> test) where T : class
    {
        _current = name;
        try
        {
            return test();
        }
        catch (Exception error)
        {
            Record(error);
            return null;
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        _current = name;
        try
        {
            await test();
        }
        catch (Exception error)
        {
            Record(error);
        }
    }

    private static void Record(Exception error)
    {
        if (IsUnavailable(error))
        {
            Add($"SKIP  {_current}: not available on this machine ({error.GetType().Name}: {error.Message})");
            return;
        }
        _failed = true;
        Add($"FAIL  {_current}: {(error is CheckFailed ? error.Message : error.ToString())}");
    }

    /// <summary>Missing Media Foundation, or no GPU video processing (CI runners).</summary>
    private static bool IsUnavailable(Exception error)
    {
        var hr = error is COMException com ? com.HResult : (error as SharpGen.Runtime.SharpGenException)?.ResultCode.Code ?? 0;
        return hr is unchecked((int)0x80040154) // REGDB_E_CLASSNOTREG
            or unchecked((int)0x887A0004)       // DXGI_ERROR_UNSUPPORTED
            or unchecked((int)0x80004001)       // E_NOTIMPL
            or unchecked((int)0xC00D36B0)       // MF_E_PLATFORM_NOT_INITIALIZED
            or unchecked((int)0x8007007E);      // module not found
    }

    private static void Add(string line)
    {
        Report.Add(line);
        Log.App("self-test " + line);
    }

    /// <summary>A moving gradient, so frames differ.</summary>
    private static byte[] TestFrame(int width, int height, int index)
    {
        var frame = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            frame[y * width + x] = (byte)(x + y + index * 4);
        Array.Fill(frame, (byte)128, width * height, width * height / 2);
        return frame;
    }
}
