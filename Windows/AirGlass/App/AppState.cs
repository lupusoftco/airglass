using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using AirGlass.Capture;
using AirGlass.Server;
using AirGlass.Streaming;
using AirGlass.Support;

namespace AirGlass;

/// <summary>What gets mirrored to the viewer.</summary>
public enum CaptureMode
{
    FullScreen,
    Window,
}

/// <summary>The popover designs (the same as on macOS; Windows needs no
/// screen recording permission, so that one never appears here).</summary>
public enum PopoverMode
{
    Waiting,
    Connected,
    Offline,
}

/// <summary>
/// Single source of truth for the UI, mirroring the macOS AppState. Capture,
/// server and WebRTC layers report into this object.
/// </summary>
public sealed class AppState : INotifyPropertyChanged
{
    private readonly TokenStore _tokens = new();
    private readonly LocalServer _server;
    private readonly LocalAddressMonitor _addressMonitor = new();
    private readonly VideoPipeline? _pipeline;
    private readonly ScreenCapturer? _capturer;
    private ViewerChannel? _viewer;
    private PeerSession? _session;

    private CaptureMode _captureMode = CaptureMode.FullScreen;
    private ServerState _serverState = new ServerState.Starting();
    private string? _localAddress;
    private string _token;
    private string? _viewerDevice;
    private string? _viewerAddress;
    private CaptureState _captureState = new CaptureState.Idle();

    public AppState()
    {
        try
        {
            _pipeline = new VideoPipeline();
            _capturer = new ScreenCapturer(_pipeline.Device, _pipeline);
            _capturer.StateChanged += state => CaptureState = state;
        }
        catch (Exception error)
        {
            // No usable GPU device; the popover says so when a source is chosen.
            Log.Capture($"Video pipeline unavailable: {error}");
        }

        _token = _tokens.Current;
        _server = new LocalServer(_tokens);
        _server.StateChanged += state => ServerState = state;
        _server.ViewerConnected += OnViewerConnected;
        _addressMonitor.Changed += address => LocalAddress = address;

        _server.Start();
        _addressMonitor.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureMode CaptureMode
    {
        get => _captureMode;
        private set => SetField(ref _captureMode, value);
    }

    public ServerState ServerState
    {
        get => _serverState;
        private set => SetField(ref _serverState, value);
    }

    public string? LocalAddress
    {
        get => _localAddress;
        private set => SetField(ref _localAddress, value);
    }

    public CaptureState CaptureState
    {
        get => _captureState;
        private set => SetField(ref _captureState, value);
    }

    /// <summary>The window that owns the system capture picker.</summary>
    public IntPtr PickerOwner
    {
        set
        {
            if (_capturer != null) _capturer.OwnerWindow = value;
        }
    }

    /// <summary>Mirrors the token store so the QR code refreshes when it rotates.</summary>
    public string Token
    {
        get => _token;
        private set => SetField(ref _token, value);
    }

    /// <summary>"iPhone — Safari" while a phone is watching.</summary>
    public string? ViewerDevice
    {
        get => _viewerDevice;
        private set => SetField(ref _viewerDevice, value);
    }

    /// <summary>The watching phone's IP address.</summary>
    public string? ViewerAddress
    {
        get => _viewerAddress;
        private set => SetField(ref _viewerAddress, value);
    }

    /// <summary>True while a phone is watching; the tray icon changes.</summary>
    public bool IsStreaming => _viewerDevice != null;

    public PopoverMode PopoverMode
    {
        get
        {
            if (IsStreaming) return PopoverMode.Connected;
            if (OfflineMessage != null) return PopoverMode.Offline;
            return PopoverMode.Waiting;
        }
    }

    /// <summary>Why there is no QR code, if there isn't one.</summary>
    public string? OfflineMessage
    {
        get
        {
            if (_serverState is ServerState.Failed) return Localizer.T("offline.serverFailed");
            if (_localAddress == null) return Localizer.T("offline.noNetwork");
            if (ViewerUrl == null) return Localizer.T("offline.starting");
            return null;
        }
    }

    /// <summary>"192.168.1.24:3131", shown under the QR code (without the token).</summary>
    public string? ViewerHost =>
        _localAddress != null && _serverState is ServerState.Ready ready ? $"{_localAddress}:{ready.Port}" : null;

    /// <summary>What the QR code encodes: the page on this PC, with the token in
    /// the fragment so it is never sent in an HTTP request.</summary>
    public string? ViewerUrl => ViewerHost is { } host ? $"http://{host}/#{_token}" : null;

    /// <summary>The server has accepted a viewer: its token is spent and no
    /// other viewer can join until this one is gone.</summary>
    private void OnViewerConnected(ViewerChannel channel)
    {
        if (_viewer != null || _pipeline == null || !IPAddress.TryParse(_localAddress ?? "", out var localAddress))
        {
            channel.Close();
            IssueNewToken();
            return;
        }

        PeerSession session;
        try
        {
            session = new PeerSession(channel, _pipeline, localAddress);
        }
        catch (Exception error)
        {
            Log.WebRtc($"Could not create the peer connection: {error}");
            channel.Close();
            IssueNewToken();
            return;
        }

        _viewer = channel;
        _session = session;
        session.Ended += () =>
        {
            if (_session != session) return;
            _session = null;
            _viewer = null;
            ViewerAddress = null;
            ViewerDevice = null;
            IssueNewToken();
        };
        Log.App($"Viewer connected: {channel.DeviceName} ({channel.RemoteAddress})");
        ViewerAddress = channel.RemoteAddress;
        ViewerDevice = channel.DeviceName;
        session.Start();
    }

    /// <summary>Every disconnect gets a fresh token, and with it a fresh QR code.</summary>
    private void IssueNewToken()
    {
        Token = _tokens.Rotate();
    }

    /// <summary>Starts capturing the screen, or opens the system picker.</summary>
    public async void ChooseSource(CaptureMode mode)
    {
        CaptureMode = mode;
        if (_capturer == null)
        {
            CaptureState = new CaptureState.Failed("capture.unavailable");
            return;
        }
        await _capturer.ChooseAsync(mode);
    }

    public void StopCapture()
    {
        _capturer?.Stop();
    }

    /// <summary>Ends the viewer's session; a new QR code follows.</summary>
    public void Disconnect()
    {
        _session?.End();
    }

    public void CopyViewerUrl()
    {
        if (ViewerUrl is { } url) Clipboard.SetText(url);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
