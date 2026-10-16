using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AirGlass;

/// <summary>What gets mirrored to the viewer.</summary>
public enum CaptureMode
{
    FullScreen,
    Window,
}

/// <summary>The popover designs (the same four as on macOS; Windows needs no
/// screen recording permission, so that one never appears here).</summary>
public enum PopoverMode
{
    Waiting,
    Connected,
    Offline,
}

/// <summary>
/// Single source of truth for the UI, mirroring the macOS AppState. Capture,
/// server and WebRTC layers report into this object in later milestones.
/// </summary>
public sealed class AppState : INotifyPropertyChanged
{
    private CaptureMode _captureMode = CaptureMode.FullScreen;
    private bool _isStreaming;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureMode CaptureMode
    {
        get => _captureMode;
        private set => SetField(ref _captureMode, value);
    }

    /// <summary>True while a phone is watching; the tray icon changes.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetField(ref _isStreaming, value);
    }

    public PopoverMode PopoverMode => PopoverMode.Waiting;

    /// <summary>URL encoded in the QR code. Placeholder until the local server exists.</summary>
    public string ViewerUrl => "http://192.168.1.2:3131/#placeholder-token";

    /// <summary>"192.168.1.2:3131", shown under the QR code (without the token).</summary>
    public string ViewerHost => "192.168.1.2:3131";

    public void ChooseSource(CaptureMode mode)
    {
        // The system capture picker arrives in the next milestone.
        CaptureMode = mode;
    }

    public void CopyViewerUrl()
    {
        Clipboard.SetText(ViewerUrl);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
