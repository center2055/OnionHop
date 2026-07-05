using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnionHopV3.Core.Models;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Persistent library of saved bridges and SNI hosts (v3.6). Bridges found by the scanner used to be
/// forgotten as soon as the scan finished; this keeps the ones the user wants in a small JSON file
/// under app data (mirroring <see cref="SettingsService"/>: atomic writes, corrupt-file quarantine),
/// so they can be reused, labelled, and re-applied later. Entries are de-duplicated by their
/// normalized line.
/// </summary>
public sealed class SavedBridgeStore
{
    private readonly string _path;

    public SavedBridgeStore()
        : this(null)
    {
    }

    /// <summary>Optional override for the storage directory (used by tests).</summary>
    public SavedBridgeStore(string? overrideDirectory)
    {
        var dir = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "saved-bridges.json");
    }

    public string StorePath => _path;

    public IReadOnlyList<SavedBridge> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<SavedBridge>();
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("SavedBridgeStore: could not read library; treating as empty.", ex);
            return Array.Empty<SavedBridge>();
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<SavedBridge>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return items ?? new List<SavedBridge>();
        }
        catch (Exception ex)
        {
            // A corrupt library must never crash the app; quarantine and start fresh.
            StartupLogger.Write("SavedBridgeStore: library file was corrupt; quarantining.", ex);
            TryQuarantineCorruptFile();
            return Array.Empty<SavedBridge>();
        }
    }

    public void SaveAll(IEnumerable<SavedBridge> items)
    {
        var list = items?.ToList() ?? new List<SavedBridge>();
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write (temp + move) so a crash mid-write leaves the previous good file intact.
        var tempPath = _path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            StartupLogger.Write("SavedBridgeStore: atomic save failed; writing directly.", ex);
            try
            {
                File.WriteAllText(_path, json);
            }
            catch (Exception inner)
            {
                StartupLogger.Write("SavedBridgeStore: save failed.", inner);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Add entries that are not already present (de-duped by id), returning the number actually added.
    /// Existing entries are left untouched so a re-save does not clobber a user's label.
    /// </summary>
    public int AddRange(IEnumerable<SavedBridge> entries)
    {
        var current = Load().ToList();
        var seen = new HashSet<string>(current.Select(e => e.Id), StringComparer.Ordinal);
        var added = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Line))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Id))
            {
                entry.Id = MakeId(entry.Kind, entry.Line);
            }

            if (seen.Add(entry.Id))
            {
                current.Add(entry);
                added++;
            }
        }

        if (added > 0)
        {
            SaveAll(current);
        }

        return added;
    }

    public void Remove(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var current = Load().ToList();
        var kept = current.Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal)).ToList();
        if (kept.Count != current.Count)
        {
            SaveAll(kept);
        }
    }

    public void SetLabel(string id, string label)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var current = Load().ToList();
        var entry = current.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (entry != null)
        {
            entry.Label = label ?? string.Empty;
            SaveAll(current);
        }
    }

    public void Clear() => SaveAll(Array.Empty<SavedBridge>());

    /// <summary>Stable id for an entry: SHA-256 of "kind|normalized-line", first 16 hex chars. Case- and
    /// whitespace-insensitive so the same bridge saved twice de-dupes.</summary>
    public static string MakeId(string kind, string line)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant()
            + "|"
            + (line ?? string.Empty).Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(_path, _path + $".corrupt-{stamp}", overwrite: true);
        }
        catch
        {
            // Best effort; the next SaveAll overwrites it atomically anyway.
        }
    }
}
