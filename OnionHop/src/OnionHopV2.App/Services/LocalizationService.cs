using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace OnionHopV2.App.Services;

public static class LocalizationService
{
    private static readonly Dictionary<string, string> LanguageResources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "avares://OnionHopV3/Resources/Strings.en.axaml",
        ["de"] = "avares://OnionHopV3/Resources/Strings.de.axaml",
        ["fr"] = "avares://OnionHopV3/Resources/Strings.fr.axaml",
        ["ru"] = "avares://OnionHopV3/Resources/Strings.ru.axaml",
        ["zh"] = "avares://OnionHopV3/Resources/Strings.zh.axaml"
    };

    public static string CurrentLanguage { get; private set; } = "en";

    public static event EventHandler? LanguageChanged;

    public static void ApplyLanguage(string? languageCode)
    {
        var code = Normalize(languageCode);
        CurrentLanguage = code;

        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        // Remove any previously merged string dictionaries.
        var dictionariesToRemove = app.Resources.MergedDictionaries
            .OfType<ResourceInclude>()
            .Where(static dictionary => dictionary.Source?.OriginalString.Contains("/Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        foreach (var dictionary in dictionariesToRemove)
        {
            app.Resources.MergedDictionaries.Remove(dictionary);
        }

        // Always merge English first as the fallback base. Avalonia resolves merged dictionaries
        // last-wins, so overlaying the selected language afterward overrides translated keys while
        // any untranslated key gracefully falls back to English (instead of showing the raw key).
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://OnionHopV3/"))
        {
            Source = new Uri(LanguageResources["en"])
        });

        if (!string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) &&
            LanguageResources.TryGetValue(code, out var overlaySource))
        {
            try
            {
                var overlay = new ResourceInclude(new Uri("avares://OnionHopV3/"))
                {
                    Source = new Uri(overlaySource)
                };
                _ = overlay.Loaded.Count; // force the resource to load so a bad file fails here...
                app.Resources.MergedDictionaries.Add(overlay);
            }
            catch
            {
                // ...and is contained: keep the English base so the UI never blanks out.
            }
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var value) == true &&
            value is string text)
        {
            return text;
        }

        return key;
    }

    private static string Normalize(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var trimmed = languageCode.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("de", StringComparison.Ordinal)) return "de";
        if (trimmed.StartsWith("fr", StringComparison.Ordinal)) return "fr";
        if (trimmed.StartsWith("ru", StringComparison.Ordinal)) return "ru";
        if (trimmed.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        return "en";
    }
}
