using System.Windows;
using System.Windows.Controls;
using AirGlass.Support;

namespace AirGlass.Views;

/// <summary>The Settings window (one at a time). Choosing a language applies it
/// right away, to this window too.</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;

    public SettingsWindow()
    {
        InitializeComponent();
        LanguageBox.ItemsSource = Localizer.Languages;
        LanguageBox.SelectedValue = Localizer.Current;
    }

    public static void ShowSingle()
    {
        if (_current is null)
        {
            _current = new SettingsWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }
        _current.Activate();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is string code) Localizer.SetLanguage(code);
    }
}
