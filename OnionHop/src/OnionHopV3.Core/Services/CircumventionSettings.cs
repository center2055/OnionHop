using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Tor's own per-country circumvention settings, the data behind Tor Browser's Connection Assist.
/// For each country Tor has curated, it lists the transports to try, best first, and for some of
/// them the exact bridge lines that work there: Snowflake with the domain fronts that get through in
/// that country, an Amazon SQS rendezvous for China, the AMP cache for Turkmenistan. A snapshot ships
/// with the app, because bridges.torproject.org is blocked in exactly those countries; a newer copy
/// replaces it whenever one could be downloaded.
/// </summary>
public sealed class CircumventionSettings
{
    public const string MapUrl = "https://bridges.torproject.org/moat/circumvention/map";

    /// <summary>When the bundled snapshot was taken.</summary>
    public const string BundledSnapshotDate = "2026-10-14";

    private const string BundledResourceName = "OnionHopV3.Core.Resources.circumvention-map.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// One transport Tor recommends for a country, with the bridge lines Tor ships for it there. Often
    /// there are none, and the bridges come from Tor's bridge service instead.
    /// </summary>
    public sealed record TransportSetting(string Transport, IReadOnlyList<string> BridgeLines);

    private static readonly object Gate = new();
    private static CircumventionSettings? _current;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<TransportSetting>> _byCountry;

    private CircumventionSettings(IReadOnlyDictionary<string, IReadOnlyList<TransportSetting>> byCountry, string source)
    {
        _byCountry = byCountry;
        Source = source;
    }

    /// <summary>Where these settings came from, for the log ("downloaded 2026-10-05", "bundled snapshot 2026-10-04").</summary>
    public string Source { get; }

    public IReadOnlyCollection<string> Countries => _byCountry.Keys.ToList();

    internal static string CachePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OnionHop",
        "circumvention-map.json");

    /// <summary>The newest settings available: a downloaded copy if there is a valid one, else the bundled snapshot.</summary>
    public static CircumventionSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= LoadCached() ?? LoadBundled();
            }
        }
    }

    /// <summary>Tor's recommendations for a country, best first; empty when Tor has none for it.</summary>
    public IReadOnlyList<TransportSetting> ForCountry(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return Array.Empty<TransportSetting>();
        }

        return _byCountry.TryGetValue(countryCode.Trim().ToLowerInvariant(), out var settings)
            ? settings
            : Array.Empty<TransportSetting>();
    }

    /// <summary>
    /// Download Tor's current map unless the saved copy is less than a day old. Best effort: failing
    /// (bridges.torproject.org is blocked in many of the countries this matters for) changes nothing.
    /// </summary>
    public static async Task<bool> TryRefreshAsync(HttpClient httpClient, Action<string>? log, CancellationToken token)
    {
        try
        {
            var cache = new FileInfo(CachePath);
            if (cache.Exists && DateTime.UtcNow - cache.LastWriteTimeUtc < RefreshInterval)
            {
                return false;
            }

            var json = await httpClient.GetStringAsync(MapUrl, token).ConfigureAwait(false);
            var parsed = Parse(json, $"downloaded {DateTime.UtcNow:yyyy-MM-dd}");
            if (parsed._byCountry.Count == 0)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
            lock (Gate)
            {
                _current = parsed;
            }

            log?.Invoke($"Updated Tor's circumvention settings ({parsed._byCountry.Count} countries).");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal static CircumventionSettings Parse(string json, string source)
    {
        var byCountry = new Dictionary<string, IReadOnlyList<TransportSetting>>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new CircumventionSettings(byCountry, source);
        }

        foreach (var country in document.RootElement.EnumerateObject())
        {
            if (country.Name.Length != 2 ||
                !country.Value.TryGetProperty("settings", out var settings) ||
                settings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var transports = new List<TransportSetting>();
            foreach (var setting in settings.EnumerateArray())
            {
                if (!setting.TryGetProperty("bridges", out var bridges) ||
                    !bridges.TryGetProperty("type", out var typeElement) ||
                    NormalizeTransport(typeElement.GetString()) is not { } transport)
                {
                    continue;
                }

                var lines = new List<string>();
                if (bridges.TryGetProperty("bridge_strings", out var strings) && strings.ValueKind == JsonValueKind.Array)
                {
                    lines.AddRange(strings.EnumerateArray()
                        .Select(line => line.GetString()?.Trim())
                        .Where(line => !string.IsNullOrEmpty(line))
                        .Select(line => line!));
                }

                // meek and meek-azure both map to OnionHop's meek transport: merge, keeping the order.
                var existing = transports.FindIndex(t => t.Transport == transport);
                if (existing >= 0)
                {
                    transports[existing] = transports[existing] with
                    {
                        BridgeLines = transports[existing].BridgeLines.Concat(lines).Distinct().ToList()
                    };
                }
                else
                {
                    transports.Add(new TransportSetting(transport, lines));
                }
            }

            if (transports.Count > 0)
            {
                byCountry[country.Name.ToLowerInvariant()] = transports;
            }
        }

        return new CircumventionSettings(byCountry, source);
    }

    // Tor's transport names to OnionHop's bridge types (the keys of pt_config.json's bridges).
    private static string? NormalizeTransport(string? type)
    {
        var normalized = type?.Trim().ToLowerInvariant();
        return normalized switch
        {
            null or "" => null,
            "meek-azure" or "meek_lite" => "meek",
            _ => normalized
        };
    }

    private static CircumventionSettings? LoadCached()
    {
        try
        {
            var cache = new FileInfo(CachePath);
            if (!cache.Exists)
            {
                return null;
            }

            var parsed = Parse(File.ReadAllText(CachePath), $"downloaded {cache.LastWriteTimeUtc:yyyy-MM-dd}");
            return parsed._byCountry.Count > 0 ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    internal static CircumventionSettings LoadBundled()
    {
        var source = $"bundled snapshot {BundledSnapshotDate}";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResourceName);
            if (stream is null)
            {
                return Parse("{}", source);
            }

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), source);
        }
        catch
        {
            return Parse("{}", source);
        }
    }

    /// <summary>Forget the loaded settings so the next <see cref="Current"/> reloads them (tests).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _current = null;
        }
    }
}
