using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnionHopV3.Core.Models;

namespace OnionHopV3.Core.Services;

/// <summary>
/// The user's published onion services (#77). Kept in its own small JSON file under app data rather
/// than in settings.json, mirroring <see cref="SavedBridgeStore"/>: atomic writes and corrupt-file
/// quarantine, so a bad file can never cost someone the keys to their addresses or crash the app.
///
/// Each entry carries the ED25519-V3 key that reproduces its address, encrypted at rest by
/// <see cref="ProtectedStringJsonConverter"/>.
/// </summary>
public sealed class OnionServiceStore
{
    private readonly string _path;

    public OnionServiceStore()
        : this(null)
    {
    }

    /// <summary>Optional override for the storage directory (used by tests).</summary>
    public OnionServiceStore(string? overrideDirectory)
    {
        var dir = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "onion-services.json");
    }

    public string StorePath => _path;

    public IReadOnlyList<OnionService> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<OnionService>();
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("OnionServiceStore: could not read services; treating as empty.", ex);
            return Array.Empty<OnionService>();
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<OnionService>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return items ?? new List<OnionService>();
        }
        catch (Exception ex)
        {
            // Quarantine rather than delete: the file holds private keys, and losing one silently
            // means losing that .onion address for good.
            StartupLogger.Write("OnionServiceStore: file was corrupt; quarantining.", ex);
            TryQuarantineCorruptFile();
            return Array.Empty<OnionService>();
        }
    }

    public void SaveAll(IEnumerable<OnionService> items)
    {
        var list = items?.ToList() ?? new List<OnionService>();
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write (temp + move) so a crash mid-write leaves the previous good file intact.
        var tempPath = _path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            // Tighten permissions before the file is in place, so there is no window in which key
            // material sits at the default mode. On Windows DPAPI already scopes it to this user;
            // elsewhere SecretProtector stores plaintext, which makes the mode the only protection.
            RestrictToOwner(tempPath);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("OnionServiceStore: atomic save failed; writing directly.", ex);
            try
            {
                File.WriteAllText(_path, json);
                RestrictToOwner(_path);
            }
            catch (Exception inner)
            {
                StartupLogger.Write("OnionServiceStore: save failed.", inner);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>Add a service, assigning an id when it has none. Returns the stored entry.</summary>
    public OnionService Add(OnionService service)
    {
        var current = Load().ToList();
        if (string.IsNullOrEmpty(service.Id))
        {
            service.Id = NewId();
        }

        current.Add(service);
        SaveAll(current);
        return service;
    }

    /// <summary>Replace an entry by id, keeping the stored key when the update does not carry one.</summary>
    public void Update(OnionService service)
    {
        if (string.IsNullOrEmpty(service.Id))
        {
            return;
        }

        var current = Load().ToList();
        var index = current.FindIndex(s => string.Equals(s.Id, service.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(service.PrivateKey))
        {
            // Never drop a key by saving an edit that did not include it: that would change the
            // address the user has already handed out.
            service.PrivateKey = current[index].PrivateKey;
        }

        current[index] = service;
        SaveAll(current);
    }

    public void Remove(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var current = Load().ToList();
        var kept = current.Where(s => !string.Equals(s.Id, id, StringComparison.Ordinal)).ToList();
        if (kept.Count != current.Count)
        {
            SaveAll(kept);
        }
    }

    internal static string NewId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>
    /// Make the file readable and writable by its owner only. This matters most on Linux and macOS,
    /// where <see cref="OnionHopV3.Core.Security.SecretProtector"/> has no backend and the keys are
    /// stored in plaintext, so the file mode is what stops another local account reading them.
    /// </summary>
    internal static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("OnionServiceStore: could not restrict file permissions.", ex);
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_path, $"{_path}.corrupt-{stamp}", overwrite: true);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("OnionServiceStore: could not quarantine corrupt file.", ex);
        }
    }
}
