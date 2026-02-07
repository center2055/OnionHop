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
        ["en"] = "avares://OnionHopV2/Resources/Strings.en.axaml",
        ["de"] = "avares://OnionHopV2/Resources/Strings.de.axaml"
    };

    private static ResourceInclude? _activeDictionary;

    public static string CurrentLanguage { get; private set; } = "en";

    public static event EventHandler? LanguageChanged;

    public static void ApplyLanguage(string? languageCode)
    {
        var code = Normalize(languageCode);
        if (!LanguageResources.TryGetValue(code, out var source))
        {
            code = "en";
            source = LanguageResources[code];
        }

        CurrentLanguage = code;
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var dictionariesToRemove = app.Resources.MergedDictionaries
            .OfType<ResourceInclude>()
            .Where(static dictionary => dictionary.Source?.OriginalString.Contains("/Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        foreach (var dictionary in dictionariesToRemove)
        {
            app.Resources.MergedDictionaries.Remove(dictionary);
        }

        if (_activeDictionary != null && app.Resources.MergedDictionaries.Contains(_activeDictionary))
        {
            app.Resources.MergedDictionaries.Remove(_activeDictionary);
        }

        _activeDictionary = new ResourceInclude(new Uri("avares://OnionHopV2/"))
        {
            Source = new Uri(source)
        };

        app.Resources.MergedDictionaries.Add(_activeDictionary);
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

        var trimmed = languageCode.Trim();
        if (trimmed.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return "de";
        }

        return "en";
    }
}
