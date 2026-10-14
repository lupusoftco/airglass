using System.Net;

namespace AirGlass.Server;

/// <summary>
/// Local HTTP + WebSocket server
/// Serves viewer HTML and WebRTC signaling
/// </summary>
public class LocalHttpServer
{
    private HttpListener? _listener;
    private int _port;

    public async Task StartAsync()
    {
        // TODO: Find available port (127.0.0.1:random)
        // TODO: Set up HttpListener
        // TODO: Accept connections for:
        //   GET / -> index.html
        //   GET /offer -> SDP offer
        //   POST /answer -> SDP answer
        //   POST /ice-candidate -> ICE candidate

        await Task.CompletedTask;
    }

    public void Stop()
    {
        _listener?.Stop();
    }

    public string GetAccessUrl()
    {
        return $"http://127.0.0.1:{_port}";
    }
}
