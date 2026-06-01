using System.Collections.Generic;
using System.Linq;
using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class ReachabilityReorderTests
{
    private static IReadOnlyList<SmartConnectAdvisor.Strategy> SevereLadder()
        => SmartConnectAdvisor.BuildStrategiesForRisk(
            new OnionHopConnectOptions(),
            SmartConnectAdvisor.RiskLevel.Severe,
            CensorshipProfiles.GetPreferredTransports("CN"));

    [Fact]
    public void Leads_with_the_transport_that_has_reachable_bridges()
    {
        var ladder = SevereLadder();
        var reach = new Dictionary<string, (int, int?)>
        {
            ["obfs4"] = (12, 80),   // reachable here
            ["webtunnel"] = (0, null), // proven dead here
        };

        var reordered = SmartConnectAdvisor.ReorderByReachability(ladder, reach);

        var firstBridge = reordered.First(s => s.Name.StartsWith("bridge:"));
        Assert.Equal("bridge:obfs4", firstBridge.Name);
    }

    [Fact]
    public void Dead_transports_sink_below_unprobed_ones()
    {
        var ladder = SevereLadder();
        var reach = new Dictionary<string, (int, int?)>
        {
            ["obfs4"] = (0, null), // proven dead
            // webtunnel not probed -> unknown, should rank above the dead obfs4
        };

        var reordered = SmartConnectAdvisor.ReorderByReachability(ladder, reach);

        var obfs4Index = IndexOf(reordered, "bridge:obfs4");
        var webtunnelIndex = IndexOf(reordered, "bridge:webtunnel");
        Assert.True(webtunnelIndex < obfs4Index, "dead obfs4 must sink below unprobed webtunnel");
    }

    [Fact]
    public void More_reachable_and_faster_ranks_higher()
    {
        var ladder = SevereLadder();
        var reach = new Dictionary<string, (int, int?)>
        {
            ["obfs4"] = (3, 400),    // few + slow
            ["webtunnel"] = (40, 60) // many + fast
        };

        var reordered = SmartConnectAdvisor.ReorderByReachability(ladder, reach);

        Assert.True(IndexOf(reordered, "bridge:webtunnel") < IndexOf(reordered, "bridge:obfs4"));
    }

    [Fact]
    public void Empty_reachability_is_a_noop()
    {
        var ladder = SevereLadder();
        var reordered = SmartConnectAdvisor.ReorderByReachability(ladder, new Dictionary<string, (int, int?)>());
        Assert.Same(ladder, reordered);
    }

    [Fact]
    public void Preserves_the_full_strategy_set()
    {
        var ladder = SevereLadder();
        var reach = new Dictionary<string, (int, int?)> { ["obfs4"] = (5, 100) };
        var reordered = SmartConnectAdvisor.ReorderByReachability(ladder, reach);

        Assert.Equal(
            ladder.Select(s => s.Name).OrderBy(n => n),
            reordered.Select(s => s.Name).OrderBy(n => n));
    }

    private static int IndexOf(IReadOnlyList<SmartConnectAdvisor.Strategy> strategies, string name)
    {
        for (var i = 0; i < strategies.Count; i++)
        {
            if (strategies[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
