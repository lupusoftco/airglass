using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AirGlass.Server;

public class LocalHttpServer : IDisposable
{
    private HttpListener? _listener;
    private int _port;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public event EventHandler<WebSocketConnectionEventArgs>? ClientConnected;
    public event EventHandler<SdpOfferRequestEventArgs>? OfferRequested;
    public event EventHandler<SdpAnswerReceivedEventArgs>? AnswerReceived;
    public event EventHandler<IceCandidateEventArgs>? IceCandidateReceived;

    public LocalHttpServer()
    {
        _port = FindAvailablePort();
    }

    public async Task StartAsync()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _serverTask = HandleConnectionsAsync(_cts.Token);

        await Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    public string GetAccessUrl() => $"http://127.0.0.1:{_port}";
    public int GetPort() => _port;

    private async Task HandleConnectionsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                var context = await _listener.GetContextAsync();
                if (ct.IsCancellationRequested) break;

                _ = Task.Run(() => ProcessRequestAsync(context, ct), ct);
            }
        }
        catch (HttpListenerException) { }
    }

    private async Task ProcessRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/")
            {
                await SendHtmlAsync(resp);
            }
            else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/offer")
            {
                var args = new SdpOfferRequestEventArgs();
                OfferRequested?.Invoke(this, args);
                await SendJsonAsync(resp, new { offer = args.SdpOffer });
            }
            else if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/answer")
            {
                var sdp = await ReadBodyAsync(req);
                AnswerReceived?.Invoke(this, new SdpAnswerReceivedEventArgs { SdpAnswer = sdp });
                resp.StatusCode = 200;
            }
            else if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/ice-candidate")
            {
                var json = await ReadBodyAsync(req);
                var doc = JsonDocument.Parse(json);
                var candidate = doc.RootElement.GetProperty("candidate").GetString() ?? "";
                var sdpMLineIndex = doc.RootElement.GetProperty("sdpMLineIndex").GetInt32();

                IceCandidateReceived?.Invoke(this, new IceCandidateEventArgs
                {
                    Candidate = candidate,
                    SdpMLineIndex = sdpMLineIndex
                });
                resp.StatusCode = 200;
            }
            else
            {
                resp.StatusCode = 404;
            }

            resp.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Request error: {ex.Message}");
        }
    }

    private async Task SendHtmlAsync(HttpListenerResponse resp)
    {
        var html = EmbeddedViewerAssets.GetHtml();
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = Encoding.UTF8.GetByteCount(html);
        await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html));
    }

    private async Task SendJsonAsync(HttpListenerResponse resp, object data)
    {
        var json = JsonSerializer.Serialize(data);
        resp.ContentType = "application/json";
        resp.ContentLength64 = Encoding.UTF8.GetByteCount(json);
        await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
    }

    private async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private int FindAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WebSocketConnectionEventArgs : EventArgs
{
    public string ClientId { get; set; } = "";
}

public class SdpOfferRequestEventArgs : EventArgs
{
    public string SdpOffer { get; set; } = "";
}

public class SdpAnswerReceivedEventArgs : EventArgs
{
    public string SdpAnswer { get; set; } = "";
}

public class IceCandidateEventArgs : EventArgs
{
    public string Candidate { get; set; } = "";
    public int SdpMLineIndex { get; set; }
}
