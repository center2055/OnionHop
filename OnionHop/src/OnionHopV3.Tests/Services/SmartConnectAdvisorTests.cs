using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class SmartConnectAdvisorTests
{
    [Fact]
    public async System.Threading.Tasks.Task BuildPlanAsync_custom_source_uses_custom_bridges_without_racing()
    {
        // Issue #70: when the user picked the Custom bridge source, Smart Connect must use their
        // bridges as-is, not race its own transports (which reset the source and dropped the lines).
        // The short-circuit returns before any network call, so this test needs no I/O.
        var advisor = new SmartConnectAdvisor();
        var options = new OnionHopConnectOptions
        {
            BridgeSourceMode = OnionHopConnectOptions.BridgeSourceCustom,
            CustomBridges = "obfs4 1.2.3.4:443 74FAD13168806246602538555B5521A0383A1875 cert=abc iat-mode=0",
            SelectedBridgeType = "obfs4"
        };

        var plan = await advisor.BuildPlanAsync(options, null, System.Threading.CancellationToken.None);

        var strategy = Assert.Single(plan.Strategies);
        Assert.Equal("custom", strategy.Name);
        Assert.True(strategy.Options.UseTorBridges);
        Assert.Equal(OnionHopConnectOptions.BridgeSourceCustom, strategy.Options.BridgeSourceMode);
        Assert.Equal(options.CustomBridges, strategy.Options.CustomBridges);
    }

    [Fact]
    public void ComputeRestrictionScore_IncreasesWhenFailuresIncrease()
    {
        var low = SmartConnectAdvisor.ComputeRestrictionScore(
            failureCount: 50,
            sampleCount: 1000,
            failingNetworks: 2,
            networkCount: 40,
            notOkNetworks: 1);

        var high = SmartConnectAdvisor.ComputeRestrictionScore(
            failureCount: 700,
            sampleCount: 1000,
            failingNetworks: 20,
            networkCount: 40,
            notOkNetworks: 10);

        Assert.True(high > low);
    }

    [Theory]
    [InlineData(0.05, true, SmartConnectAdvisor.RiskLevel.Open)]
    // ~0.21 is the kind of score a free country (e.g. Germany) gets from OONI vanilla_tor noise; it
    // must stay Open so Smart Connect leads with direct, not bridges.
    [InlineData(0.21, true, SmartConnectAdvisor.RiskLevel.Open)]
    [InlineData(0.25, true, SmartConnectAdvisor.RiskLevel.Open)]
    [InlineData(0.50, true, SmartConnectAdvisor.RiskLevel.Moderate)]
    [InlineData(0.70, true, SmartConnectAdvisor.RiskLevel.Restricted)]
    [InlineData(0.85, true, SmartConnectAdvisor.RiskLevel.Severe)]
    [InlineData(0.85, false, SmartConnectAdvisor.RiskLevel.Unknown)]
    public void DetermineRiskLevel_MapsExpectedBuckets(double score, bool reliable, SmartConnectAdvisor.RiskLevel expected)
    {
        Assert.Equal(expected, SmartConnectAdvisor.DetermineRiskLevel(score, reliable));
    }

    [Theory]
    [InlineData(SmartConnectAdvisor.RiskLevel.Open)]
    [InlineData(SmartConnectAdvisor.RiskLevel.Unknown)]
    public void BuildStrategiesForRisk_DirectIsPrimary_GetsFullBootstrapBudget(SmartConnectAdvisor.RiskLevel risk)
    {
        // Where direct is the expected path, it must get the generous bootstrap budget so a slow-but-
        // working cold start isn't cut off at the brief fail-fast and pushed onto bridges.
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(new OnionHopConnectOptions(), risk);

        var direct = strategies.First(s => s.Name == "direct");
        Assert.Equal(SmartConnectAdvisor.DirectPrimaryAttemptTimeoutSeconds, direct.Options.SmartConnectAttemptTimeoutSeconds);
    }

    [Fact]
    public void BuildStrategiesForRisk_Open_StartsWithDirect()
    {
        var options = new OnionHopConnectOptions();

        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(options, SmartConnectAdvisor.RiskLevel.Open);

        Assert.NotEmpty(strategies);
        Assert.Equal("direct", strategies[0].Name);
        Assert.False(strategies[0].Options.UseTorBridges);
        Assert.False(strategies[0].Options.UseCensoredMode);
    }

    [Fact]
    public void BuildStrategiesForRisk_Restricted_StartsWithBridges()
    {
        var options = new OnionHopConnectOptions();

        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(options, SmartConnectAdvisor.RiskLevel.Restricted);

        Assert.NotEmpty(strategies);
        Assert.True(strategies[0].Options.UseTorBridges);
        Assert.True(strategies[0].Options.UseCensoredMode);
    }

    [Fact]
    public void Strategies_carry_fail_fast_attempt_timeouts()
    {
        // Every Smart Connect strategy must set a short per-attempt timeout so a dead strategy
        // escalates quickly instead of waiting out the multi-minute automatic default.
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(
            new OnionHopConnectOptions(), SmartConnectAdvisor.RiskLevel.Severe);

        Assert.All(strategies, s => Assert.True(s.Options.SmartConnectAttemptTimeoutSeconds is > 0));

        var direct = strategies.First(s => s.Name == "direct");
        Assert.Equal(SmartConnectAdvisor.DirectAttemptTimeoutSeconds, direct.Options.SmartConnectAttemptTimeoutSeconds);

        var bridge = strategies.First(s => s.Name.StartsWith("bridge:"));
        Assert.Equal(SmartConnectAdvisor.BridgeAttemptTimeoutSeconds, bridge.Options.SmartConnectAttemptTimeoutSeconds);
    }

    [Fact]
    public void BuildStrategiesForRisk_Severe_LeadsWithSnowflakeAmp()
    {
        var options = new OnionHopConnectOptions { UseSnowflakeAmp = false };

        // Severe (no country preference) leads with the snowflake bridge and forces AMP fronting,
        // which is the most blocking-resistant first move.
        var strategies = SmartConnectAdvisor.BuildStrategiesForRisk(options, SmartConnectAdvisor.RiskLevel.Severe);

        Assert.NotEmpty(strategies);
        Assert.Equal("bridge:snowflake", strategies[0].Name);
        Assert.True(strategies[0].Options.UseSnowflakeAmp);
        // dnstt is present as the last-resort transport, and direct is the final fallback.
        Assert.Contains(strategies, s => s.Name == "bridge:dnstt");
        Assert.Equal("direct", strategies[^1].Name);
    }

    [Fact]
    public void ComputeMeasurementRestrictionScore_IncreasesWithFailuresAndAnomalies()
    {
        var low = SmartConnectAdvisor.ComputeMeasurementRestrictionScore(
            failureCount: 5,
            sampleCount: 100,
            anomalyCount: 1);
        var high = SmartConnectAdvisor.ComputeMeasurementRestrictionScore(
            failureCount: 60,
            sampleCount: 100,
            anomalyCount: 30);

        Assert.True(high > low);
    }

    [Fact]
    public void ComputeSignalConfidence_IncreasesWithSampleAndAsnDiversity()
    {
        var now = DateTimeOffset.UtcNow;
        var low = SmartConnectAdvisor.ComputeSignalConfidence(
            sampleCount: 10,
            uniqueAsnCount: 1,
            newestMeasurementUtc: now,
            maxConfidence: 1.0);
        var high = SmartConnectAdvisor.ComputeSignalConfidence(
            sampleCount: 500,
            uniqueAsnCount: 12,
            newestMeasurementUtc: now,
            maxConfidence: 1.0);

        Assert.True(high > low);
    }

    [Fact]
    public void CombineSignalScores_WeightsByConfidence()
    {
        var signals = new List<(string Source, double Score, double Confidence)>
        {
            ("primary", 0.2, 0.9),
            ("secondary", 0.8, 0.1)
        };

        var combined = SmartConnectAdvisor.CombineSignalScores(signals);

        Assert.InRange(combined.Score, 0.2, 0.35);
        Assert.True(combined.Confidence > 0);
    }

    [Fact]
    public void ParseCsvRow_HandlesQuotedCommas()
    {
        var row = SmartConnectAdvisor.ParseCsvRow("US,vanilla_tor,\"AS123,ISP\",true");

        Assert.Equal(4, row.Count);
        Assert.Equal("AS123,ISP", row[2]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void ParseCsvBoolean_ParsesExpectedValues(string value, bool expected)
    {
        Assert.Equal(expected, SmartConnectAdvisor.ParseCsvBoolean(value));
    }
}
