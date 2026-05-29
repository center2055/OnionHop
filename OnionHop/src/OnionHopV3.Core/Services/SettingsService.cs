using System;
using System.IO;
using System.Text.Json;
using OnionHopV3.Core.Models;

namespace OnionHopV3.Core.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
        : this(null)
    {
    }

    /// <summary>Optional override for settings directory (e.g. for tests). When null, uses default AppData.</summary>
    public SettingsService(string? overrideSettingsDirectory)
    {
        var dir = overrideSettingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public UserSettings? Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        var json = File.ReadAllText(_settingsPath);
        return JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public void Save(UserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}

