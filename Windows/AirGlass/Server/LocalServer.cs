using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AirGlass.Support;

namespace AirGlass.Server;

public abstract record ServerState
{
    public sealed record Starting : ServerState;
    public sealed record Ready(int Port) : ServerState;
    public sealed record Failed(string Message) : ServerState;
}

/// <summary>
/// Serves the viewer page and carries WebRTC signaling, both over plain HTTP
/// on one port. Requests are handled on thread-pool threads; events are
/// raised on the UI thread.
/// </summary>
public sealed class LocalServer
{
    /// <summary>3131 first; if it is taken, the next free one.</summary>
    private const int FirstPort = 3131, LastPort = 3140;
    /// <summary>Plenty for one viewer reloading; caps resource use on hostile networks.</summary>
    private const int MaxConnections = 16;
    /// <summary>Header carrying the session ID that /signal/hello hands out.</summary>
    private const string SessionHeader = "x-airglass-session";

    private readonly TokenStore _tokens;
    private readonly ViewerAssets _assets = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly object _gate = new();
    /// <summary>Authenticated viewers by session ID.</summary>
    private readonly Dictionary<string, ViewerChannel> _channels = new();
    private TcpListener? _listener;
    private int _connectionCount;

    public LocalServer(TokenStore tokens)
    {
        _tokens = tokens;
    }

    public event Action<ServerState>? StateChanged;
    public event Action<ViewerChannel>? ViewerConnected;

    public void Start()
    {
        Task.Run(Listen);
    }

    // MARK: - Listening

    private void Listen()
    {
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Taken, or inside a range Windows reserves (Hyper-V does that).
                Log.Server($"Port {port} unavailable: {error.SocketErrorCode}");
                continue;
            }
            catch (SocketException error)
            {
                Log.Server($"Listening on port {port} failed: {error.Message}");
                Report(new ServerState.Failed(error.Message));
                return;
            }

            _listener = listener;
            Log.Server($"Listening on port {port}");
            Report(new ServerState.Ready(port));
            _ = AcceptLoop(listener);
            return;
        }
        Report(new ServerState.Failed("Boş port bulunamadı"));
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                Log.Server($"Listener stopped: {error.Message}");
                Report(new ServerState.Failed("Yerel sunucu durdu"));
                return;
            }
            socket.NoDelay = true;

            if (Interlocked.Increment(ref _connectionCount) > MaxConnections)
            {
                Interlocked.Decrement(ref _connectionCount);
                Log.Server($"Too many connections; dropping {socket.RemoteEndPoint}");
                socket.Dispose();
                continue;
            }
            _ = Serve(new HttpConnection(socket));
        }
    }

    private async Task Serve(HttpConnection http)
    {
        Log.Server($"TCP accepted from {http.RemoteDescription} ({Volatile.Read(ref _connectionCount)} open)");
        try
        {
            await http.RunAsync(Route);
        }
        finally
        {
            Interlocked.Decrement(ref _connectionCount);
        }
    }

    // MARK: - Routing

    private void Route(HttpRequest request, HttpConnection http)
    {
        Log.Server($"{request.Method} {request.Path} from {http.RemoteDescription}");

        if (request.Path.StartsWith("/signal/", StringComparison.Ordinal))
        {
            RouteSignaling(request, http);
            return;
        }
        if (request.Method != "GET")
        {
            http.Respond(405, "Method Not Allowed", new[] { ("Allow", "GET") });
            return;
        }
        var asset = _assets[request.Path];
        if (asset != null) RespondWithAsset(asset, request, http);
        else http.Respond(404, "Not Found");
    }

    /// <summary>Serves a bundled file, honouring a single byte range: Safari
    /// only plays media from servers that support range requests.</summary>
    private static void RespondWithAsset(ViewerAssets.Asset asset, HttpRequest request, HttpConnection http)
    {
        var headers = AssetHeaders(asset.ContentType);
        headers.Add(("Accept-Ranges", "bytes"));
        var total = asset.Data.Length;

        var rangeHeader = request.Header("range");
        if (rangeHeader == null)
        {
            http.Respond(200, "OK", headers, asset.Data);
            return;
        }
        var range = ByteRange(rangeHeader, total);
        if (range == null)
        {
            headers.Add(("Content-Range", $"bytes */{total}"));
            http.Respond(416, "Range Not Satisfiable", headers);
            return;
        }
        var (start, end) = range.Value;
        headers.Add(("Content-Range", $"bytes {start}-{end}/{total}"));
        http.Respond(206, "Partial Content", headers, asset.Data.AsSpan(start, end - start + 1).ToArray());
    }

    /// <summary>"bytes=0-1", "bytes=100-" or "bytes=-500" → an inclusive range.</summary>
    private static (int Start, int End)? ByteRange(string header, int total)
    {
        if (total <= 0 || !header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return null;
        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2) return null;
        int? start = int.TryParse(parts[0].Trim(), out var s) ? s : null;
        int? end = int.TryParse(parts[1].Trim(), out var e) ? e : null;

        if (start is { } a && end is { } b && a >= 0 && a <= b && a < total) return (a, Math.Min(b, total - 1));
        if (start is { } c && end == null && c >= 0 && c < total) return (c, total - 1);
        if (start == null && end is { } suffix && suffix > 0 && parts[0].Trim().Length == 0)
            return (Math.Max(0, total - suffix), total - 1);
        return null;
    }

    private static List<(string, string)> AssetHeaders(string contentType) => new()
    {
        ("Content-Type", contentType),
        ("Cache-Control", "no-store"),
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
        ("Content-Security-Policy",
         "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
         + "media-src 'self' blob:; img-src 'self' data:; base-uri 'none'; form-action 'none'; "
         + "frame-ancestors 'none'"),
    };

    // MARK: - Signaling

    // POST /signal/hello  {token}      → {session}; the viewer is in
    // POST /signal/send   {message}    → 204   (session header)
    // GET  /signal/poll                → [messages] (long poll; session header)
    // POST /signal/bye                 → 204   (session header)
    //
    // Every failure gets the same bare 403: nothing is revealed about why.
    private void RouteSignaling(HttpRequest request, HttpConnection http)
    {
        // Only our own page may talk to us (guards against other sites in the
        // phone's browser and DNS rebinding).
        var origin = request.Header("origin");
        if (origin != null && origin != $"http://{request.Header("host") ?? ""}")
        {
            Log.Server($"Signaling rejected: origin {origin}");
            http.Respond(403, "Forbidden");
            return;
        }

        switch (request.Method, request.Path)
        {
            case ("POST", "/signal/hello"):
                Hello(request, http);
                break;

            case ("POST", "/signal/send"):
            {
                var channel = ChannelFor(request);
                var message = request.JsonObject;
                if (channel == null || message == null)
                {
                    Forbid(http, "send without a valid session or body");
                    return;
                }
                channel.Receive(message);
                http.Respond(204, "No Content");
                break;
            }

            case ("GET", "/signal/poll"):
            {
                var channel = ChannelFor(request);
                if (channel == null)
                {
                    Forbid(http, "poll without a valid session");
                    return;
                }
                channel.Poll(http);
                break;
            }

            case ("POST", "/signal/bye"):
                ChannelFor(request)?.CloseByViewer();
                http.Respond(204, "No Content");
                break;

            default:
                http.Respond(404, "Not Found");
                break;
        }
    }

    private void Hello(HttpRequest request, HttpConnection http)
    {
        ViewerChannel channel;
        string sessionId;
        lock (_gate)
        {
            // One viewer at a time: while someone is watching, nobody else gets
            // in, whatever they present.
            if (_channels.Count > 0)
            {
                Forbid(http, "hello while a viewer is already connected");
                return;
            }
            // The token is consumed here; the QR code it came from is now dead.
            if (request.JsonObject?["token"] is not JsonValue tokenValue
                || !tokenValue.TryGetValue(out string? token)
                || token == null
                || !_tokens.Consume(token))
            {
                Forbid(http, "hello with a wrong or already used token, or a malformed body");
                return;
            }

            sessionId = TokenStore.Generate();
            channel = new ViewerChannel(DeviceName(request.Header("user-agent")), http.RemoteAddress, _ui);
            _channels[sessionId] = channel;
            channel.Ended = () =>
            {
                lock (_gate) _channels.Remove(sessionId);
            };
        }

        Log.Server($"Viewer authenticated ({channel.DeviceName}, UA: {request.Header("user-agent") ?? "-"})");
        http.RespondJson(new JsonObject { ["session"] = sessionId });
        Post(() => ViewerConnected?.Invoke(channel));
    }

    private ViewerChannel? ChannelFor(HttpRequest request)
    {
        var session = request.Header(SessionHeader);
        if (session == null) return null;
        lock (_gate) return _channels.TryGetValue(session, out var channel) ? channel : null;
    }

    private static void Forbid(HttpConnection http, string reason)
    {
        Log.Server($"Signaling rejected: {reason}");
        http.Respond(403, "Forbidden");
    }

    /// <summary>"iPhone — Safari" from a User-Agent header.</summary>
    private static string DeviceName(string? userAgent)
    {
        if (userAgent == null) return "Tarayıcı";

        string device;
        if (userAgent.Contains("iPhone")) device = "iPhone";
        else if (userAgent.Contains("iPad")) device = "iPad";
        else if (userAgent.Contains("Android")) device = "Android";
        // iPadOS Safari reports itself as a Mac.
        else if (userAgent.Contains("Macintosh")) device = "iPad / Mac";
        else device = "Tarayıcı";

        string? browser;
        // Order matters: Edge and Chrome also claim to be Safari, Edge also Chrome.
        if (userAgent.Contains("EdgiOS") || userAgent.Contains("Edg/") || userAgent.Contains("EdgA/")) browser = "Edge";
        else if (userAgent.Contains("FxiOS") || userAgent.Contains("Firefox/")) browser = "Firefox";
        else if (userAgent.Contains("CriOS") || userAgent.Contains("Chrome/")) browser = "Chrome";
        else if (userAgent.Contains("Safari/")) browser = "Safari";
        else browser = null;

        return browser == null ? device : $"{device} — {browser}";
    }

    private void Report(ServerState state) => Post(() => StateChanged?.Invoke(state));

    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }
}
