using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using AirGlass.Support;

namespace AirGlass.Views;

/// <summary>The About AirGlass window (one at a time).</summary>
public partial class AboutWindow : Window
{
    private static AboutWindow? _current;

    public AboutWindow()
    {
        InitializeComponent();
        AppIcon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/AirGlass.ico"));
        ShowVersion();
        Localizer.Changed += ShowVersion;
        Closed += (_, _) => Localizer.Changed -= ShowVersion;
    }

    private void ShowVersion() => VersionText.Text = Localizer.T("version", AppVersion);

    /// <summary>"0.2.0", from the assembly version set in the project file.</summary>
    public static string AppVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static void ShowSingle()
    {
        if (_current is null)
        {
            _current = new AboutWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }
        _current.Activate();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
