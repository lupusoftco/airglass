using System;
using System.Threading;
using System.Windows;
using AirGlass.Support;
using AirGlass.Views;

namespace AirGlass;

/// <summary>
/// A notification-area (tray) app, the Windows counterpart of the macOS menu
/// bar app: no main window and no taskbar button, only the tray icon and its
/// popover.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppState? _state;
    private TrayIcon? _trayIcon;
    private PopoverWindow? _popover;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Before any WebRTC type is touched.
        WebRtcLogging.Install();

        if (Array.IndexOf(e.Args, "--self-test") >= 0)
        {
            RunSelfTest();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AirGlass", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        Theme.Apply(SystemTheme.AppsUseLightTheme);
        Localizer.Publish();
        SystemTheme.Changed += () => Dispatcher.Invoke(() =>
        {
            Theme.Apply(SystemTheme.AppsUseLightTheme);
            _trayIcon?.Refresh();
        });

        _state = new AppState();
        _popover = new PopoverWindow(_state);
        _trayIcon = new TrayIcon(_state);
        _trayIcon.Clicked += () => _popover.Toggle();
        _trayIcon.AboutRequested += AboutWindow.ShowSingle;
        _trayIcon.SettingsRequested += SettingsWindow.ShowSingle;
        _trayIcon.QuitRequested += Quit;
        _popover.QuitRequested += Quit;
    }

    private async void RunSelfTest()
    {
        var code = 1;
        try
        {
            code = await SelfTest.RunAsync();
        }
        catch (Exception error)
        {
            Log.App($"self-test crashed: {error}");
        }
        Shutdown(code);
    }

    private void Quit()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
