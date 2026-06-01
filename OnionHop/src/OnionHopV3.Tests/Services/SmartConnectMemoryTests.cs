using System;
using System.IO;
using System.Linq;
using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class SmartConnectMemoryTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"sc-memory-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public void BuildNetworkKey_uses_country_and_ipv4_16_prefix()
    {
        Assert.Equal("AZ/31.171", SmartConnectMemory.BuildNetworkKey("az", "31.171.101.214"));
        // Same /16, different host -> same key (survives a dynamic-IP change within the ISP region).
        Assert.Equal("AZ/31.171", SmartConnectMemory.BuildNetworkKey("AZ", "31.171.100.182"));
    }

    [Fact]
    public void BuildNetworkKey_null_when_nothing_usable()
    {
        Assert.Null(SmartConnectMemory.BuildNetworkKey(null, null));
        Assert.Null(SmartConnectMemory.BuildNetworkKey(null, "not-an-ip"));
    }

    [Fact]
    public void Records_and_recalls_a_success()
    {
        var memory = new SmartConnectMemory(_tempFile);
        var key = SmartConnectMemory.BuildNetworkKey("AZ", "31.171.101.214");

        Assert.Null(memory.TryGet(key));
        memory.RecordSuccess(key, "snowflake", useBridges: true);

        var entry = memory.TryGet(key);
        Assert.NotNull(entry);
        Assert.Equal("snowflake", entry!.Transport);
        Assert.True(entry.UseBridges);
    }

    [Fact]
    public void Persists_across_instances()
    {
        var key = SmartConnectMemory.BuildNetworkKey("IR", "5.22.1.1");
        new SmartConnectMemory(_tempFile).RecordSuccess(key, "webtunnel", true);

        var reloaded = new SmartConnectMemory(_tempFile).TryGet(key);
        Assert.Equal("webtunnel", reloaded!.Transport);
    }

    [Fact]
    public void Invalidate_forgets_an_entry()
    {
        var memory = new SmartConnectMemory(_tempFile);
        var key = SmartConnectMemory.BuildNetworkKey("RU", "95.1.2.3");
        memory.RecordSuccess(key, "obfs4", true);

        memory.Invalidate(key);

        Assert.Null(memory.TryGet(key));
    }
}

public sealed class PromoteRememberedStrategyTests
{
    private static System.Collections.Generic.IReadOnlyList<SmartConnectAdvisor.Strategy> Severe()
        => SmartConnectAdvisor.BuildStrategiesForRisk(
            new OnionHopConnectOptions(),
            SmartConnectAdvisor.RiskLevel.Severe,
            CensorshipProfiles.GetPreferredTransports("CN"));

    [Fact]
    public void Promotes_remembered_bridge_to_front()
    {
        var strategies = Severe();
        Assert.Contains(strategies, s => s.Name == "bridge:obfs4");
        Assert.NotEqual("bridge:obfs4", strategies[0].Name); // not already first

        var promoted = SmartConnectAdvisor.PromoteRememberedStrategy(strategies, "obfs4");

        Assert.Equal("bridge:obfs4", promoted[0].Name);
        // Same set, just reordered.
        Assert.Equal(strategies.Count, promoted.Count);
        Assert.Equal(
            strategies.Select(s => s.Name).OrderBy(n => n),
            promoted.Select(s => s.Name).OrderBy(n => n));
    }

    [Fact]
    public void Unknown_remembered_transport_is_a_noop()
    {
        var strategies = Severe();
        var promoted = SmartConnectAdvisor.PromoteRememberedStrategy(strategies, "nonexistent");
        Assert.Same(strategies, promoted);
    }

    [Fact]
    public void GetStrategyTransport_strips_bridge_prefix()
    {
        var strategies = Severe();
        var bridge = strategies.First(s => s.Name.StartsWith("bridge:"));
        Assert.Equal(bridge.Name["bridge:".Length..], SmartConnectAdvisor.GetStrategyTransport(bridge));
    }
}
