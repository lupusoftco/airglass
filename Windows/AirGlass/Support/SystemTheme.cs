using System;
using Microsoft.Win32;

namespace AirGlass.Support;

/// <summary>
/// Windows' light/dark settings. Apps and the taskbar have separate
/// switches, so the popover and the tray icon each follow their own.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Raised (on a background thread) when either setting changes.</summary>
    public static event Action? Changed;

    static SystemTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) Changed?.Invoke();
        };
    }

    public static bool AppsUseLightTheme => ReadFlag("AppsUseLightTheme", defaultValue: true);

    public static bool TaskbarUsesLightTheme => ReadFlag("SystemUsesLightTheme", defaultValue: false);

    private static bool ReadFlag(string name, bool defaultValue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value != 0 : defaultValue;
    }
}
