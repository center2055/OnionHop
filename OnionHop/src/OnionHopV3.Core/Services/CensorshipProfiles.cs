using System;
using System.Collections.Generic;
using System.Linq;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Offline, built-in knowledge of where Tor is censored and which transports tend to survive there.
/// This is the "brain" Smart Connect falls back on when live OONI signals are unavailable - which is
/// exactly the situation in heavily censored countries, where api.ooni.io is itself often blocked.
///
/// The table encodes a <b>risk floor</b> per country: the final risk used to pick a strategy is never
/// allowed to drop below it. So a user in a country that bans Tor will never "start direct" just
/// because the live signal was thin or optimistic. It also carries a per-country preferred-transport
/// order, since different censors defeat different transports (e.g. China actively probes obfs4, so
/// snowflake/webtunnel/conjure do better; some Iran ISPs throttle snowflake, where webtunnel/obfs4
/// shine). Country data is intentionally conservative and based on widely reported Tor reachability;
/// it is a prior, not a verdict - live signals can push risk <i>higher</i>, never below the floor.
/// </summary>
public static class CensorshipProfiles
{
    /// <summary>
    /// A built-in censorship prior for a country: how restricted to assume Tor is, and the transport
    /// order most likely to get through there.
    /// </summary>
    public sealed record CountryProfile(
        SmartConnectAdvisor.RiskLevel RiskFloor,
        IReadOnlyList<string> PreferredTransports);

    // Transport-order presets. Ordered best-first for the relevant censorship style.
    private static readonly string[] HardGfwOrder = ["snowflake", "webtunnel", "conjure", "obfs4", "dnstt"];
    private static readonly string[] HardDpiOrder = ["webtunnel", "snowflake", "obfs4", "conjure", "dnstt"];
    private static readonly string[] ModerateOrder = ["obfs4", "snowflake", "webtunnel"];

    // Country -> prior. Tiers reflect reported Tor reachability, not politics.
    //  Severe     = Tor + most bridges actively blocked; direct never works (GFW-class).
    //  Restricted = Tor commonly blocked; bridges usually required.
    //  Moderate   = intermittent/regional blocking; direct may work but bridges often needed.
    private static readonly IReadOnlyDictionary<string, CountryProfile> Profiles =
        new Dictionary<string, CountryProfile>(StringComparer.OrdinalIgnoreCase)
        {
            // --- Severe: Tor heavily blocked, active probing, direct never works ---
            ["CN"] = new(SmartConnectAdvisor.RiskLevel.Severe, HardGfwOrder),  // Great Firewall
            ["IR"] = new(SmartConnectAdvisor.RiskLevel.Severe, HardDpiOrder),  // Iran
            ["TM"] = new(SmartConnectAdvisor.RiskLevel.Severe, HardDpiOrder),  // Turkmenistan (most aggressive)

            // --- Restricted: Tor commonly blocked, bridges usually required ---
            ["RU"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Russia (rolling Tor/bridge blocks)
            ["BY"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Belarus
            ["AZ"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Azerbaijan
            ["EG"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Egypt
            ["AE"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // UAE
            ["SA"] = new(SmartConnectAdvisor.RiskLevel.Restricted, ModerateOrder), // Saudi Arabia
            ["SY"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Syria
            ["VN"] = new(SmartConnectAdvisor.RiskLevel.Restricted, ModerateOrder), // Vietnam
            ["MM"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Myanmar
            ["KZ"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Kazakhstan
            ["UZ"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Uzbekistan
            ["TJ"] = new(SmartConnectAdvisor.RiskLevel.Restricted, HardDpiOrder),  // Tajikistan
            ["ET"] = new(SmartConnectAdvisor.RiskLevel.Restricted, ModerateOrder), // Ethiopia
            ["CU"] = new(SmartConnectAdvisor.RiskLevel.Restricted, ModerateOrder), // Cuba

            // --- Moderate: intermittent/regional blocking; bridges often help ---
            ["PK"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Pakistan
            ["TR"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Turkey
            ["IN"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // India
            ["ID"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Indonesia
            ["BD"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Bangladesh
            ["QA"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Qatar
            ["BH"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Bahrain
            ["OM"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Oman
            ["JO"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Jordan
            ["KW"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Kuwait
            ["IQ"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Iraq
            ["VE"] = new(SmartConnectAdvisor.RiskLevel.Moderate, ModerateOrder),   // Venezuela
        };

    /// <summary>
    /// Returns the built-in censorship prior for a country, or null when the country isn't in the
    /// table (treated as open/unknown - live signals decide).
    /// </summary>
    public static CountryProfile? TryGet(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        return Profiles.TryGetValue(countryCode.Trim().ToUpperInvariant(), out var profile)
            ? profile
            : null;
    }

    /// <summary>
    /// The risk floor for a country - the lowest risk level Smart Connect is allowed to act on there.
    /// Unknown countries return <see cref="SmartConnectAdvisor.RiskLevel.Unknown"/> (no floor).
    /// </summary>
    public static SmartConnectAdvisor.RiskLevel GetRiskFloor(string? countryCode)
    {
        return TryGet(countryCode)?.RiskFloor ?? SmartConnectAdvisor.RiskLevel.Unknown;
    }

    /// <summary>
    /// Raise <paramref name="liveRisk"/> to at least the country's floor. When live data is absent
    /// (<see cref="SmartConnectAdvisor.RiskLevel.Unknown"/>) the floor takes over entirely, so a
    /// hostile country still gets an aggressive strategy with zero signal.
    /// </summary>
    public static SmartConnectAdvisor.RiskLevel ApplyFloor(SmartConnectAdvisor.RiskLevel liveRisk, string? countryCode)
    {
        var floor = GetRiskFloor(countryCode);
        if (floor == SmartConnectAdvisor.RiskLevel.Unknown)
        {
            return liveRisk;
        }

        // RiskLevel is ordered Unknown(0) < Open < Moderate < Restricted < Severe, so a numeric max
        // gives "the more cautious of (live, floor)" - while never dropping below the floor.
        return (SmartConnectAdvisor.RiskLevel)Math.Max((int)liveRisk, (int)floor);
    }

    /// <summary>Preferred transport order for a country, or empty when unknown.</summary>
    public static IReadOnlyList<string> GetPreferredTransports(string? countryCode)
    {
        return TryGet(countryCode)?.PreferredTransports ?? Array.Empty<string>();
    }
}
