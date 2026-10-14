using AirGlass.Capture;
using AirGlass.Encoding;
using AirGlass.Server;
using AirGlass.Streaming;
using QRCoder;

namespace AirGlass;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║        AirGlass - Windows Edition      ║");
        Console.WriteLine("║    Wireless Screen Mirroring to iPhone ║");
        Console.WriteLine("╚════════════════════════════════════════╝");
        Console.WriteLine();

        var app = new AirGlassApplication();
        await app.RunAsync();
    }
}

class AirGlassApplication
{
    private LocalHttpServer? _server;
    private VideoPipeline? _pipeline;
    private PeerSession? _peerSession;
    private ScreenCapture? _capturer;
    private H264Encoder? _encoder;

    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        _isRunning = false;

        try
        {
            PrintMenu();

            while (!_cts.Token.IsCancellationRequested)
            {
                Console.Write("\n> ");
                var cmd = Console.ReadLine()?.ToLower() ?? "";

                switch (cmd)
                {
                    case "start":
                        await StartStreaming();
                        break;
                    case "stop":
                        StopStreaming();
                        break;
                    case "status":
                        PrintStatus();
                        break;
                    case "help":
                        PrintMenu();
                        break;
                    case "exit":
                    case "quit":
                        goto done;
                    default:
                        Console.WriteLine("Unknown command. Type 'help' for commands.");
                        break;
                }
            }

            done:
            StopStreaming();
            Console.WriteLine("\n✓ AirGlass stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Error: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
        }
    }

    private void PrintMenu()
    {
        Console.WriteLine("\nCommands:");
        Console.WriteLine("  start  - Start screen mirroring");
        Console.WriteLine("  stop   - Stop streaming");
        Console.WriteLine("  status - Show status");
        Console.WriteLine("  help   - Show this menu");
        Console.WriteLine("  exit   - Quit AirGlass");
    }

    private async Task StartStreaming()
    {
        if (_isRunning)
        {
            Console.WriteLine("✗ Streaming already running.");
            return;
        }

        try
        {
            Console.WriteLine("\n⏳ Starting AirGlass streaming...");

            // Initialize encoder
            _encoder = new H264Encoder(1920, 1080, 5000);
            _encoder.Initialize();

            // Create pipeline
            _pipeline = new VideoPipeline(1920, 1080);
            _pipeline.StateChanged += (s, e) => Console.WriteLine($"  Pipeline: {e.State}");

            // Create peer session
            _peerSession = new PeerSession();
            _peerSession.ConnectionStateChanged += (s, e) =>
                Console.WriteLine($"  Connection: {e.State}" + (e.Error != null ? $" ({e.Error})" : ""));

            _pipeline.SetPeerSession(_peerSession);

            // Start HTTP server
            _server = new LocalHttpServer();
            _server.OfferRequested += async (s, e) =>
            {
                var offer = await _peerSession.GetOfferAsync();
                e.SdpOffer = offer;
                Console.WriteLine("  → Offer sent to client");
            };
            _server.AnswerReceived += async (s, e) =>
            {
                await _peerSession.SetAnswerAsync(e.SdpAnswer);
                Console.WriteLine("  ← Answer received from client");
            };
            _server.IceCandidateReceived += (s, e) =>
            {
                _peerSession.AddIceCandidate(e.Candidate, e.SdpMLineIndex);
            };

            await _server.StartAsync();

            _isRunning = true;

            // Display connection info
            Console.WriteLine("\n✓ AirGlass is ready!");
            Console.WriteLine($"\nAccess URL: {_server.GetAccessUrl()}");
            Console.WriteLine("Connection Type: Local WiFi (127.0.0.1)");

            // Generate QR code
            GenerateAndDisplayQrCode(_server.GetAccessUrl());

            Console.WriteLine("\n⏳ Waiting for iPhone connection... (Ctrl+C to stop)");

            // Start pipeline when connected
            Console.WriteLine("\nNote: In a real scenario, stream would start when iPhone connects.");
            Console.WriteLine("For now, manually type 'stop' to exit.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to start: {ex.Message}");
            _isRunning = false;
        }
    }

    private void StopStreaming()
    {
        if (!_isRunning)
        {
            Console.WriteLine("✗ Streaming not running.");
            return;
        }

        Console.WriteLine("\n⏳ Stopping AirGlass...");

        _pipeline?.Stop();
        _server?.Stop();
        _peerSession?.Dispose();
        _encoder?.Dispose();

        _isRunning = false;
        Console.WriteLine("✓ Streaming stopped.");
    }

    private void PrintStatus()
    {
        Console.WriteLine("\n┌─ AirGlass Status ─────────────────┐");
        Console.WriteLine($"│ Running: {(_isRunning ? "✓ Yes" : "✗ No",-31}│");
        Console.WriteLine($"│ Server: {(_server != null ? "Ready" : "N/A",-33}│");
        Console.WriteLine($"│ Connected: {(_peerSession?.IsConnected == true ? "✓ Yes" : "✗ No",-28}│");
        Console.WriteLine("└─────────────────────────────────────┘");
    }

    private void GenerateAndDisplayQrCode(string url)
    {
        try
        {
            using (var qrGenerator = new QRCodeGenerator())
            {
                var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                var qrCode = new AsciiQRCode(qrCodeData);
                var qrCodeAsAsciiString = qrCode.GetGraphic(1);

                Console.WriteLine("\n📱 Scan this QR code on your iPhone:");
                Console.WriteLine(qrCodeAsAsciiString);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Could not generate QR code: {ex.Message}");
            Console.WriteLine($"URL: {url}");
        }
    }
}
