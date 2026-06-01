using System.Linq;
using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

using Risk = OnionHopV3.Core.Services.SmartConnectAdvisor.RiskLevel;

namespace OnionHopV3.Tests.Services;

public sealed class CensorshipProfilesTests
{
    [Theory]
    [InlineData("CN", Risk.Severe)]
    [InlineData("IR", Risk.Severe)]
    [InlineData("TM", Risk.Severe)]
    [InlineData("RU", Risk.Restricted)]
    [InlineData("AZ", Risk.Restricted)]
    [InlineData("TR", Risk.Moderate)]
    public void GetRiskFloor_returns_expected_tier(string country, Risk expected)
    {
        Assert.Equal(expected, CensorshipProfiles.GetRiskFloor(country));
    }

    [Fact]
    public void Unknown_country_has_no_floor()
    {
        Assert.Equal(Risk.Unknown, CensorshipProfiles.GetRiskFloor("US"));
        Assert.Equal(Risk.Unknown, CensorshipProfiles.GetRiskFloor(null));
        Assert.Equal(Risk.Unknown, CensorshipProfiles.GetRiskFloor(""));
    }

    [Fact]
    public void ApplyFloor_raises_optimistic_live_risk_in_a_hostile_country()
    {
        // This is the AZ tester's exact bug: live signal said "Moderate" but AZ blocks Tor.
        Assert.Equal(Risk.Restricted, CensorshipProfiles.ApplyFloor(Risk.Moderate, "AZ"));
        Assert.Equal(Risk.Severe, CensorshipProfiles.ApplyFloor(Risk.Open, "CN"));
        Assert.Equal(Risk.Severe, CensorshipProfiles.ApplyFloor(Risk.Unknown, "IR"));
    }

    [Fact]
    public void ApplyFloor_never_lowers_live_risk()
    {
        // A live Severe reading in a country we only floor at Moderate must stay Severe.
        Assert.Equal(Risk.Severe, CensorshipProfiles.ApplyFloor(Risk.Severe, "TR"));
        // Unknown country: live risk passes through untouched.
        Assert.Equal(Risk.Moderate, CensorshipProfiles.ApplyFloor(Risk.Moderate, "US"));
    }

    [Fact]
    public void AZ_no_longer_starts_direct()
    {
        // Regression test for the reported failure: in AZ, Smart Connect must lead with bridges,
        // not a direct attempt.
        var risk = CensorshipProfiles.ApplyFloor(Risk.Moderate, "AZ");
        var preferred = CensorshipProfiles.GetPreferredTransports("AZ");
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(new OnionHopConnectOptions(), risk, preferred);

        Assert.NotEqual("direct", strategies[0].Name);
        Assert.StartsWith("bridge:", strategies[0].Name);
    }

    [Fact]
    public void Severe_ladder_includes_dnstt_last_resort()
    {
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(
            new OnionHopConnectOptions(),
            Risk.Severe,
            CensorshipProfiles.GetPreferredTransports("CN"));

        Assert.Contains(strategies, s => s.Name == "bridge:dnstt");
        // Direct must be the very last resort under Severe.
        Assert.Equal("direct", strategies[^1].Name);
    }

    [Fact]
    public void Preferred_transports_lead_the_ladder()
    {
        // China's profile prefers snowflake first.
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(
            new OnionHopConnectOptions(),
            Risk.Severe,
            CensorshipProfiles.GetPreferredTransports("CN"));

        var firstBridge = strategies.First(s => s.Name.StartsWith("bridge:"));
        Assert.Equal("bridge:snowflake", firstBridge.Name);
    }
}
