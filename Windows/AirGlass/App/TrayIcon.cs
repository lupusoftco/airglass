using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using AirGlass.Support;
using Forms = System.Windows.Forms;

namespace AirGlass;

/// <summary>
/// The notification-area icon. Like the macOS menu bar icon it changes while
/// a phone is watching, and it follows the taskbar's light/dark theme.
/// Left click opens the popover; right click shows the usual small menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppState _state;
    private readonly Forms.NotifyIcon _icon;
    private Icon? _currentImage;
    private readonly Forms.ToolStripMenuItem _aboutItem, _settingsItem, _quitItem;

    public event Action? Clicked;
    public event Action? AboutRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon(AppState state)
    {
        _state = state;

        var menu = new Forms.ContextMenuStrip();
        _aboutItem = new Forms.ToolStripMenuItem("", null, (_, _) => AboutRequested?.Invoke());
        _settingsItem = new Forms.ToolStripMenuItem("", null, (_, _) => SettingsRequested?.Invoke());
        _quitItem = new Forms.ToolStripMenuItem("", null, (_, _) => QuitRequested?.Invoke());
        menu.Items.Add(_aboutItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_quitItem);

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };

        _state.PropertyChanged += OnStateChanged;
        Localizer.Changed += Refresh;
        Refresh();
        _icon.Visible = true;
    }

    /// <summary>Re-reads the state and the taskbar theme.</summary>
    public void Refresh()
    {
        var variant = _state.IsStreaming ? "active" : "idle";
        // "light" icons are dark glyphs for a light taskbar, and vice versa.
        var theme = SystemTheme.TaskbarUsesLightTheme ? "light" : "dark";
        var uri = new Uri($"pack://application:,,,/Assets/Tray-{variant}-{theme}.ico");

        using var stream = Application.GetResourceStream(uri)!.Stream;
        var image = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        _icon.Icon = image;
        _currentImage?.Dispose();
        _currentImage = image;

        _icon.Text = _state.IsStreaming ? Localizer.T("watching") : "AirGlass";
        _aboutItem.Text = Localizer.T("about");
        _settingsItem.Text = Localizer.T("settings");
        _quitItem.Text = Localizer.T("quit");
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ViewerDevice)) Refresh();
    }

    public void Dispose()
    {
        _state.PropertyChanged -= OnStateChanged;
        Localizer.Changed -= Refresh;
        _icon.Visible = false;
        _icon.Dispose();
        _currentImage?.Dispose();
    }
}
