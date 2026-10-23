using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace AirGlass.Support;

/// <summary>
/// The app's interface language, chosen in Settings. Texts come from
/// Localization/Strings.json, the table the macOS app uses too. Each text is
/// also published as the application resource "S.&lt;key&gt;", so XAML that
/// uses <c>{DynamicResource S.key}</c> updates when the language changes.
/// </summary>
public static class Localizer
{
    public sealed record Language(string Code, string Name);

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirGlass", "settings.json");

    static Localizer()
    {
        var languages = new List<Language>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Localization/Strings.json");
        if (stream != null)
        {
            using var table = JsonDocument.Parse(stream);
            foreach (var entry in table.RootElement.GetProperty("languages").EnumerateArray())
                languages.Add(new Language(entry.GetProperty("code").GetString()!, entry.GetProperty("name").GetString()!));
            foreach (var key in table.RootElement.GetProperty("strings").EnumerateObject())
                Strings[key.Name] = key.Value.EnumerateObject().ToDictionary(text => text.Name, text => text.Value.GetString() ?? "");
        }
        Languages = languages;
        var codes = languages.Select(language => language.Code).ToList();
        var saved = LoadSavedLanguage();
        Current = saved != null && codes.Contains(saved) ? saved : SystemLanguage(codes);
    }

    public static IReadOnlyList<Language> Languages { get; }

    /// <summary>One of <see cref="Languages"/>' codes.</summary>
    public static string Current { get; private set; }

    /// <summary>Raised on the UI thread after the language changed.</summary>
    public static event Action? Changed;

    /// <summary>The text for <paramref name="key"/>; "{0}" becomes <paramref name="argument"/>.</summary>
    public static string T(string key, string? argument = null)
    {
        var text = Strings.TryGetValue(key, out var texts)
            ? texts.GetValueOrDefault(Current) ?? texts.GetValueOrDefault("en") ?? key
            : key;
        return argument == null ? text : text.Replace("{0}", argument);
    }

    public static void SetLanguage(string code)
    {
        if (code == Current || Languages.All(language => language.Code != code)) return;
        Current = code;
        Save();
        Publish();
        Changed?.Invoke();
    }

    /// <summary>Puts every text into the application resources (at startup and on change).</summary>
    public static void Publish()
    {
        foreach (var key in Strings.Keys) Application.Current.Resources["S." + key] = T(key);
    }

    /// <summary>The Windows display language if AirGlass has it, otherwise English.</summary>
    private static string SystemLanguage(List<string> codes)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (culture.TwoLetterISOLanguageName == "zh" && codes.Contains("zh-Hans")) return "zh-Hans";
        return codes.FirstOrDefault(code => code == culture.TwoLetterISOLanguageName) ?? "en";
    }

    private static string? LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return settings.RootElement.TryGetProperty("language", out var language) ? language.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["language"] = Current }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.App($"Saving settings failed: {error.Message}");
        }
    }
}
