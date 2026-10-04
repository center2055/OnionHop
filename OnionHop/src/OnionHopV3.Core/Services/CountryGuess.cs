using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Where the user probably is when Smart Connect cannot look it up: in the countries that matter
/// most, the online lookups are often blocked too, and "country unknown" means trying direct Tor
/// first, which is exactly what does not work there. Two offline answers, in order: the country
/// detected on an earlier connect, and this computer's time zone (narrowed down by its region
/// setting where a time zone spans several countries). Only countries Smart Connect treats
/// differently are recognized; anywhere else the guess is "unknown", as before.
/// </summary>
public static class CountryGuess
{
    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    internal static string MemoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OnionHop",
        "smartconnect-country.json");

    // Windows time zone IDs of the countries Smart Connect has a profile for. Several cover more
    // than one country; the region setting decides between them. Without a deciding region the
    // first, most censored one is used: this guess only runs when the lookups failed, and then
    // starting with bridges is the safe side (they work anywhere, direct Tor does not).
    private static readonly IReadOnlyDictionary<string, string[]> WindowsZones =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Iran Standard Time"] = ["IR"],
            ["China Standard Time"] = ["CN", "HK", "MO"],
            ["Russian Standard Time"] = ["RU"],
            ["Kaliningrad Standard Time"] = ["RU"],
            ["Russia Time Zone 3"] = ["RU"],
            ["Astrakhan Standard Time"] = ["RU"],
            ["Saratov Standard Time"] = ["RU"],
            ["Volgograd Standard Time"] = ["RU"],
            ["Ekaterinburg Standard Time"] = ["RU"],
            ["Omsk Standard Time"] = ["RU"],
            ["N. Central Asia Standard Time"] = ["RU"],
            ["Altai Standard Time"] = ["RU"],
            ["Tomsk Standard Time"] = ["RU"],
            ["North Asia Standard Time"] = ["RU"],
            ["North Asia East Standard Time"] = ["RU"],
            ["Yakutsk Standard Time"] = ["RU"],
            ["Transbaikal Standard Time"] = ["RU"],
            ["Vladivostok Standard Time"] = ["RU"],
            ["Sakhalin Standard Time"] = ["RU"],
            ["Magadan Standard Time"] = ["RU"],
            ["Russia Time Zone 10"] = ["RU"],
            ["Russia Time Zone 11"] = ["RU"],
            ["Belarus Standard Time"] = ["BY"],
            ["Myanmar Standard Time"] = ["MM"],
            ["Egypt Standard Time"] = ["EG"],
            ["West Asia Standard Time"] = ["TM", "UZ", "TJ", "KZ"],
            ["Central Asia Standard Time"] = ["KZ"],
            ["Azerbaijan Standard Time"] = ["AZ"],
            ["Arabian Standard Time"] = ["AE", "OM"],
            ["Arab Standard Time"] = ["SA", "KW", "QA", "BH"],
            ["Syria Standard Time"] = ["SY"],
            ["SE Asia Standard Time"] = ["VN", "ID"],
            ["Cuba Standard Time"] = ["CU"],
            ["E. Africa Standard Time"] = ["ET"],
            ["Pakistan Standard Time"] = ["PK"],
            ["Turkey Standard Time"] = ["TR"],
            ["India Standard Time"] = ["IN"],
            ["Bangladesh Standard Time"] = ["BD"],
            ["Jordan Standard Time"] = ["JO"],
            ["Arabic Standard Time"] = ["IQ"],
            ["Venezuela Standard Time"] = ["VE"],
        };

    // IANA time zones (Linux, macOS) of the same countries. Each belongs to one country.
    private static readonly IReadOnlyDictionary<string, string> IanaZones = BuildIanaZones();

    private static IReadOnlyDictionary<string, string> BuildIanaZones()
    {
        var zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string country, params string[] ids)
        {
            foreach (var id in ids)
            {
                zones[id] = country;
            }
        }

        Add("IR", "Asia/Tehran", "Iran");
        Add("CN", "Asia/Shanghai", "Asia/Urumqi", "Asia/Chongqing", "Asia/Chungking", "Asia/Harbin", "Asia/Kashgar", "PRC");
        Add("HK", "Asia/Hong_Kong", "Hongkong");
        Add("MO", "Asia/Macau", "Asia/Macao");
        Add("TM", "Asia/Ashgabat", "Asia/Ashkhabad");
        Add("RU", "Europe/Moscow", "W-SU", "Europe/Kaliningrad", "Europe/Samara", "Europe/Ulyanovsk", "Europe/Astrakhan",
            "Europe/Saratov", "Europe/Volgograd", "Europe/Kirov", "Asia/Yekaterinburg", "Asia/Omsk", "Asia/Novosibirsk",
            "Asia/Barnaul", "Asia/Tomsk", "Asia/Novokuznetsk", "Asia/Krasnoyarsk", "Asia/Irkutsk", "Asia/Chita",
            "Asia/Yakutsk", "Asia/Khandyga", "Asia/Vladivostok", "Asia/Ust-Nera", "Asia/Sakhalin", "Asia/Magadan",
            "Asia/Srednekolymsk", "Asia/Kamchatka", "Asia/Anadyr");
        Add("BY", "Europe/Minsk");
        Add("MM", "Asia/Yangon", "Asia/Rangoon");
        Add("EG", "Africa/Cairo", "Egypt");
        Add("AZ", "Asia/Baku");
        Add("AE", "Asia/Dubai");
        Add("SA", "Asia/Riyadh");
        Add("SY", "Asia/Damascus");
        Add("VN", "Asia/Ho_Chi_Minh", "Asia/Saigon");
        Add("KZ", "Asia/Almaty", "Asia/Qyzylorda", "Asia/Qostanay", "Asia/Aqtobe", "Asia/Aqtau", "Asia/Atyrau", "Asia/Oral");
        Add("UZ", "Asia/Tashkent", "Asia/Samarkand");
        Add("TJ", "Asia/Dushanbe");
        Add("ET", "Africa/Addis_Ababa");
        Add("CU", "America/Havana", "Cuba");
        Add("PK", "Asia/Karachi");
        Add("TR", "Europe/Istanbul", "Asia/Istanbul", "Turkey");
        Add("IN", "Asia/Kolkata", "Asia/Calcutta");
        Add("ID", "Asia/Jakarta", "Asia/Pontianak", "Asia/Makassar", "Asia/Jayapura");
        Add("BD", "Asia/Dhaka", "Asia/Dacca");
        Add("QA", "Asia/Qatar");
        Add("BH", "Asia/Bahrain");
        Add("OM", "Asia/Muscat");
        Add("JO", "Asia/Amman");
        Add("KW", "Asia/Kuwait");
        Add("IQ", "Asia/Baghdad");
        Add("VE", "America/Caracas");
        return zones;
    }

    public sealed record Guess(string Country, string Basis);

    /// <summary>The best offline guess, or null: the remembered country first, then the time zone.</summary>
    public static Guess? TryGuess()
    {
        if (TryGetRemembered() is { } remembered)
        {
            return new Guess(remembered, "the country detected on an earlier connect");
        }

        string? region = null;
        try
        {
            region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch
        {
            // Invariant globalization: no region setting.
        }

        return FromTimeZone(TimeZoneInfo.Local.Id, region);
    }

    internal static Guess? FromTimeZone(string? timeZoneId, string? region)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        string[] candidates;
        if (WindowsZones.TryGetValue(timeZoneId, out var windowsCandidates))
        {
            candidates = windowsCandidates;
        }
        else if (IanaZones.TryGetValue(timeZoneId, out var ianaCountry))
        {
            candidates = [ianaCountry];
        }
        else
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(region) &&
            candidates.Contains(region, StringComparer.OrdinalIgnoreCase))
        {
            return new Guess(region.ToUpperInvariant(), "this computer's time zone and region");
        }

        return new Guess(candidates[0], "this computer's time zone");
    }

    /// <summary>Remember a country Smart Connect detected online, for when it cannot.</summary>
    public static void Remember(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Trim().Length != 2)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryPath)!);
            File.WriteAllText(MemoryPath, JsonSerializer.Serialize(new Remembered(countryCode.Trim().ToUpperInvariant(), DateTimeOffset.UtcNow)));
        }
        catch
        {
            // Best effort.
        }
    }

    private static string? TryGetRemembered()
    {
        try
        {
            if (!File.Exists(MemoryPath))
            {
                return null;
            }

            var remembered = JsonSerializer.Deserialize<Remembered>(File.ReadAllText(MemoryPath));
            return remembered is { Country.Length: 2 } && DateTimeOffset.UtcNow - remembered.DetectedUtc <= RememberFor
                ? remembered.Country
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record Remembered(string Country, DateTimeOffset DetectedUtc);
}
