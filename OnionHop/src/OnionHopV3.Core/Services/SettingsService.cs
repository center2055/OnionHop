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

        string json;
        try
        {
            json = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex)
        {
            // Could not even read the file (locked, permissions). Fall back to defaults rather than
            // crash; callers treat null as "use built-in defaults".
            StartupLogger.Write("SettingsService: could not read settings file; using defaults.", ex);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            // A corrupt, truncated (e.g. a crash mid-write) or hand-edited settings file must never
            // crash the app at startup. Quarantine the bad file so the user's data isn't silently
            // overwritten and so it can be inspected, then fall back to defaults.
            StartupLogger.Write("SettingsService: settings file was corrupt; quarantining and using defaults.", ex);
            TryQuarantineCorruptFile();
            return null;
        }
    }

    public void Save(UserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write: serialize to a sibling temp file, then replace. If the process is killed or
        // the machine loses power mid-write, the previous good settings.json is left intact instead
        // of a half-written file that would crash the next launch.
        var tempPath = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // If the atomic replace isn't available (rare filesystem), fall back to a direct write so
            // the setting still persists, and clean up the temp file.
            StartupLogger.Write("SettingsService: atomic settings save failed; writing directly.", ex);
            try
            {
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception inner)
            {
                StartupLogger.Write("SettingsService: settings save failed.", inner);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var quarantinePath = _settingsPath + $".corrupt-{stamp}";
            File.Move(_settingsPath, quarantinePath, overwrite: true);
        }
        catch
        {
            // Best effort. If we can't move it, leave it; the next Save will overwrite it atomically.
        }
    }
}
