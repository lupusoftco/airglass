using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AirGlass.Capture;
using AirGlass.Support;
using Forms = System.Windows.Forms;

namespace AirGlass.Views;

/// <summary>
/// The tray popover. It opens next to the tray icon, closes when it loses
/// focus (like the macOS menu bar window) and is reused between openings.
/// </summary>
public partial class PopoverWindow : Window
{
    /// <summary>Distance between the panel and the taskbar / screen edge, in DIPs.</summary>
    private const double EdgeGap = 12;
    /// <summary>The transparent margin around the panel that holds its shadow.</summary>
    private const double ShadowMargin = 24;

    private readonly AppState _state;
    private readonly DispatcherTimer _copyReset = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private DateTime _lastHidden = DateTime.MinValue;
    /// <summary>Where the tray icon was clicked; the panel stays anchored there
    /// when its height changes (e.g. a phone connects while it is open).</summary>
    private System.Drawing.Point _anchor;

    public event Action? QuitRequested;

    public PopoverWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        _state.PropertyChanged += OnStateChanged;

        _copyReset.Tick += (_, _) =>
        {
            _copyReset.Stop();
            ShowCopied(false);
        };

        Deactivated += (_, _) => HideNow();
        SizeChanged += (_, _) =>
        {
            if (IsVisible) PlaceNearTray();
        };
        PreviewKeyDown += OnKeyDown;
        // The picker needs an owner window; the popover itself is never captured
        // (like the macOS app, which excludes itself from the picker).
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _state.PickerOwner = handle;
        SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);

        Render();
    }

    /// <summary>Opens or closes the popover (tray icon click).</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideNow();
            return;
        }
        // Clicking the tray icon while open first deactivates (and hides) the
        // popover; don't let that same click open it again.
        if (DateTime.UtcNow - _lastHidden < TimeSpan.FromMilliseconds(300)) return;

        Render();
        _anchor = Forms.Cursor.Position;
        Opacity = 0;
        Show();
        PlaceNearTray();
        Opacity = 1;
        Activate();
    }

    private void HideNow()
    {
        if (!IsVisible) return;
        _lastHidden = DateTime.UtcNow;
        Hide();
    }

    private string? _renderedUrl;

    private void Render()
    {
        var mode = _state.PopoverMode;
        WaitingPanel.Visibility = mode == PopoverMode.Waiting ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanel.Visibility = mode == PopoverMode.Connected ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = mode == PopoverMode.Offline ? Visibility.Visible : Visibility.Collapsed;

        // Status badge: hollow dot when waiting or offline, green when live.
        StatusText.Text = mode switch
        {
            PopoverMode.Connected => "Yayında",
            PopoverMode.Offline => "Çevrimdışı",
            _ => "Bekliyor",
        };
        if (mode == PopoverMode.Connected)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GreenBrush");
            StatusDot.StrokeThickness = 0;
        }
        else
        {
            StatusDot.Fill = null;
            StatusDot.StrokeThickness = 1.5;
        }

        var url = _state.ViewerUrl;
        if (url != _renderedUrl)
        {
            _renderedUrl = url;
            QrImage.Source = url != null ? QrCodeImage.Create(url) : null;
        }
        HostText.Text = _state.ViewerHost ?? "";
        CopyButton.IsEnabled = url != null;
        OfflineText.Text = _state.OfflineMessage ?? "";

        DeviceText.Text = _state.ViewerDevice ?? "";
        DeviceAddressText.Text = $"Bağlı · {_state.ViewerAddress}";

        var capturing = (_state.CaptureState as CaptureState.Capturing)?.Source;
        WaitingCapturePanel.Visibility = capturing != null ? Visibility.Visible : Visibility.Collapsed;
        ConnectedSourcePanel.Visibility = capturing != null ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPicker.Visibility = capturing == null ? Visibility.Visible : Visibility.Collapsed;
        if (capturing != null)
        {
            ShowSource(capturing, WaitingSourceIcon, WaitingSourceName, WaitingSourceKind);
            ShowSource(capturing, ConnectedSourceIcon, ConnectedSourceName, ConnectedSourceKind);
        }
        var failure = (_state.CaptureState as CaptureState.Failed)?.Message;
        CaptureFailedText.Text = failure ?? "";
        CaptureFailedText.Visibility = failure != null ? Visibility.Visible : Visibility.Collapsed;

        var fullScreen = _state.CaptureMode == CaptureMode.FullScreen;
        FullScreenSegment.IsChecked = fullScreen;
        WindowSegment.IsChecked = !fullScreen;
        ConnectedFullScreenSegment.IsChecked = fullScreen;
        ConnectedWindowSegment.IsChecked = !fullScreen;
    }

    // A display, and a window with a title bar, in the style of the SF Symbols on macOS.
    private static readonly Geometry DisplayIcon = Geometry.Parse(
        "M2,1.5 H13 A1.2,1.2 0 0 1 14.2,2.7 V9.3 A1.2,1.2 0 0 1 13,10.5 H2 A1.2,1.2 0 0 1 0.8,9.3 V2.7 A1.2,1.2 0 0 1 2,1.5 Z M5,13.5 H10 M7.5,10.5 V13.5");
    private static readonly Geometry WindowIcon = Geometry.Parse(
        "M2,1.8 H13 A1.2,1.2 0 0 1 14.2,3 V12 A1.2,1.2 0 0 1 13,13.2 H2 A1.2,1.2 0 0 1 0.8,12 V3 A1.2,1.2 0 0 1 2,1.8 Z M0.8,5 H14.2");

    private static void ShowSource(CaptureSource source, System.Windows.Shapes.Path icon,
        System.Windows.Controls.TextBlock name, System.Windows.Controls.TextBlock kind)
    {
        icon.Data = source.IsWindow ? WindowIcon : DisplayIcon;
        name.Text = source.Name;
        name.ToolTip = source.Name;
        kind.Text = source.IsWindow ? "Pencere" : "Tüm ekran";
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(Render);
    }

    // MARK: - Actions

    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        try
        {
            _state.CopyViewerUrl();
        }
        catch (COMException)
        {
            // The clipboard is briefly locked by another app; nothing to show.
            return;
        }
        ShowCopied(true);
        _copyReset.Stop();
        _copyReset.Start();
    }

    private void ShowCopied(bool copied)
    {
        CopyIcon.Text = copied ? "" : "";
        if (copied)
            CopyIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "GreenBrush");
        else
            CopyIcon.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        CopyText.Text = copied ? "Kopyalandı" : "Linki kopyala";
    }

    // Every click opens the source picker, also on the already selected side.
    private void OnFullScreenSegment(object sender, RoutedEventArgs e) => _state.ChooseSource(CaptureMode.FullScreen);

    private void OnWindowSegment(object sender, RoutedEventArgs e) => _state.ChooseSource(CaptureMode.Window);

    private void OnDisconnect(object sender, RoutedEventArgs e) => _state.Disconnect();

    private void OnChangeSource(object sender, RoutedEventArgs e) => _state.ChooseSource(_state.CaptureMode);

    private void OnStopCapture(object sender, RoutedEventArgs e) => _state.StopCapture();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        HideNow();
        AboutWindow.ShowSingle();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideNow();
            e.Handled = true;
        }
        else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            QuitRequested?.Invoke();
            e.Handled = true;
        }
    }

    // MARK: - Placement

    /// <summary>
    /// Puts the panel next to the tray icon: above a bottom taskbar (below a
    /// top one, beside a side one), horizontally centered on the click and
    /// kept inside the work area. Done in physical pixels with the DPI of the
    /// monitor that holds the tray, since the window may still be on another.
    /// </summary>
    private void PlaceNearTray()
    {
        var cursor = _anchor;
        var screen = Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var scale = MonitorScale(cursor);

        var panelWidth = (ActualWidth - 2 * ShadowMargin) * scale;
        var panelHeight = (ActualHeight - 2 * ShadowMargin) * scale;
        var gap = EdgeGap * scale;

        double x, y;
        if (work.Bottom < bounds.Bottom || work == bounds)
        {
            // Taskbar at the bottom (or hidden): above it, centered on the icon.
            x = Clamp(cursor.X - panelWidth / 2, work.Left + gap, work.Right - panelWidth - gap);
            y = work.Bottom - panelHeight - gap;
        }
        else if (work.Top > bounds.Top)
        {
            x = Clamp(cursor.X - panelWidth / 2, work.Left + gap, work.Right - panelWidth - gap);
            y = work.Top + gap;
        }
        else if (work.Left > bounds.Left)
        {
            x = work.Left + gap;
            y = Clamp(cursor.Y - panelHeight / 2, work.Top + gap, work.Bottom - panelHeight - gap);
        }
        else
        {
            x = work.Right - panelWidth - gap;
            y = Clamp(cursor.Y - panelHeight / 2, work.Top + gap, work.Bottom - panelHeight - gap);
        }

        var margin = ShadowMargin * scale;
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(x - margin), (int)Math.Round(y - margin), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    private static double MonitorScale(System.Drawing.Point point)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
