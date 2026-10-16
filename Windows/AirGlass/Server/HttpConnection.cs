using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AirGlass.Support;

namespace AirGlass.Server;

public sealed class HttpRequest
{
    public string Method { get; private init; } = "";
    /// <summary>Path without query string.</summary>
    public string Path { get; private init; } = "";
    /// <summary>Header names are lowercased.</summary>
    public Dictionary<string, string> Headers { get; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The body parsed as a JSON object, if it is one.</summary>
    public JsonObject? JsonObject
    {
        get
        {
            try
            {
                return JsonNode.Parse(new ReadOnlySpan<byte>(Body)) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public static HttpRequest? Parse(byte[] buffer, int headLength)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(buffer, 0, headLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

        var request = new HttpRequest
        {
            Method = requestLine[0],
            Path = requestLine[1].Split('?', 2)[0],
        };
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon < 0) continue;
            var name = lines[i][..colon].Trim().ToLowerInvariant();
            request.Headers[name] = lines[i][(colon + 1)..].Trim();
        }
        return request;
    }
}

/// <summary>
/// Reads one HTTP/1.1 request (head and Content-Length body) from a TCP
/// connection and writes one response; every connection is closed after its
/// response. The response may come later (long polling) and from any thread.
/// </summary>
public sealed class HttpConnection
{
    public const int MaxHeadSize = 8 * 1024;
    /// <summary>Signaling messages (SDP, ICE) are a few KB at most.</summary>
    public const int MaxBodySize = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Socket _socket;
    private int _hasResponded;
    private volatile bool _isFinished;

    public HttpConnection(Socket socket)
    {
        _socket = socket;
        var endpoint = socket.RemoteEndPoint as IPEndPoint;
        var address = endpoint?.Address;
        if (address != null && address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        RemoteAddress = address?.ToString() ?? "?";
        RemoteDescription = $"{RemoteAddress}:{endpoint?.Port}";
    }

    /// <summary>The client's IP address.</summary>
    public string RemoteAddress { get; }
    /// <summary>"192.168.1.31:54978"</summary>
    public string RemoteDescription { get; }

    /// <summary>True once the socket is gone, e.g. the client went away while
    /// a long poll was pending.</summary>
    public bool IsFinished => _isFinished;

    /// <summary>Reads the request, hands it to <paramref name="onRequest"/> and
    /// completes when the connection is gone.</summary>
    public async Task RunAsync(Action<HttpRequest, HttpConnection> onRequest)
    {
        try
        {
            var request = await ReadRequestAsync();
            if (request == null) return;
            try
            {
                onRequest(request, this);
            }
            catch (Exception error)
            {
                Log.Server($"Handling {request.Method} {request.Path} failed: {error}");
                Respond(500, "Internal Server Error");
            }
            await DrainAsync();
        }
        finally
        {
            _isFinished = true;
            // A pending long poll whose client went away: nothing will be sent.
            if (Volatile.Read(ref _hasResponded) == 0) Close();
        }
    }

    public void Respond(int status, string reason, IEnumerable<(string Name, string Value)>? headers = null, byte[]? body = null)
    {
        if (Interlocked.Exchange(ref _hasResponded, 1) == 1) return;
        if (_isFinished)
        {
            Close();
            return;
        }
        body ??= Array.Empty<byte>();

        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        if (headers != null)
            foreach (var (name, value) in headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\nConnection: close\r\n\r\n");

        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        var data = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(data, 0);
        body.CopyTo(data, headBytes.Length);
        _ = SendAsync(data);
    }

    public void RespondJson(JsonNode json, int status = 200)
    {
        Respond(status, status == 200 ? "OK" : "Error", JsonHeaders, Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    /// <summary>A JSON array of already serialized elements.</summary>
    public void RespondJsonArray(IReadOnlyCollection<string> elements)
    {
        Respond(200, "OK", JsonHeaders, Encoding.UTF8.GetBytes("[" + string.Join(",", elements) + "]"));
    }

    private static readonly (string, string)[] JsonHeaders =
    {
        ("Content-Type", "application/json; charset=utf-8"),
        ("Cache-Control", "no-store"),
        ("X-Content-Type-Options", "nosniff"),
    };

    // MARK: - Reading

    private async Task<HttpRequest?> ReadRequestAsync()
    {
        var buffer = new byte[MaxHeadSize + HeadTerminator.Length + MaxBodySize];
        var count = 0;
        HttpRequest? pending = null;
        var bodyStart = 0;
        var contentLength = 0;
        using var timeout = new CancellationTokenSource(RequestTimeout);

        while (true)
        {
            int received;
            try
            {
                received = await _socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Drop clients that open a socket and never finish a request.
                Log.Server($"TCP {RemoteDescription}: no complete request within {RequestTimeout.TotalSeconds} s ({count} bytes); closing");
                return null;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                Log.Server($"TCP {RemoteDescription} failed: {error.Message}");
                return null;
            }
            if (received == 0)
            {
                Log.Server($"TCP {RemoteDescription} closed before a full request ({count} bytes)");
                return null;
            }
            count += received;

            if (pending == null)
            {
                var end = buffer.AsSpan(0, Math.Min(count, MaxHeadSize + HeadTerminator.Length)).IndexOf(HeadTerminator);
                if (end < 0)
                {
                    if (count > MaxHeadSize) return Invalid(431, "Request Header Fields Too Large");
                    continue;
                }
                pending = HttpRequest.Parse(buffer, end);
                if (pending == null) return Invalid(400, "Bad Request");
                if (pending.Header("transfer-encoding") != null) return Invalid(501, "Not Implemented");
                if (!int.TryParse(pending.Header("content-length") ?? "0", out contentLength) || contentLength < 0)
                    return Invalid(400, "Bad Request");
                if (contentLength > MaxBodySize) return Invalid(413, "Payload Too Large");
                bodyStart = end + HeadTerminator.Length;
            }

            if (count - bodyStart >= contentLength)
            {
                pending.Body = buffer.AsSpan(bodyStart, contentLength).ToArray();
                return pending;
            }
        }
    }

    private HttpRequest? Invalid(int status, string reason)
    {
        Respond(status, reason);
        // The caller returns null; drain so the response is not cut off by a RST.
        _ = DrainAsync();
        return null;
    }

    /// <summary>
    /// Reads and discards until the client closes. Closing a socket that
    /// still has unread input makes the kernel send a RST, which can discard
    /// our response before the client reads it. Keeping a read pending also
    /// notices a client that goes away during a long poll.
    /// </summary>
    private async Task DrainAsync()
    {
        var scratch = new byte[4096];
        try
        {
            while (await _socket.ReceiveAsync(scratch, SocketFlags.None) > 0) { }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
        }
        _isFinished = true;
    }

    private async Task SendAsync(byte[] data)
    {
        try
        {
            await _socket.SendAsync(data, SocketFlags.None);
            _socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            Log.Server($"Sending response to {RemoteDescription} failed: {error.Message}");
            Close();
            return;
        }
        // The client normally closes right away, which the drain loop
        // notices; this only bounds how long we wait for it.
        await Task.Delay(LingerTimeout);
        Close();
    }

    private void Close()
    {
        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
