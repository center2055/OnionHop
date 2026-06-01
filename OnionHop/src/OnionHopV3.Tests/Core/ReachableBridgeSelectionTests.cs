using System.Linq;
using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Core;

public sealed class ReachableBridgeSelectionTests
{
    private static BridgeScanResult Result(string line, BridgeReachability reachability, int? pingMs)
        => new(line, "obfs4", "1.2.3.4", 443, pingMs, reachability, "test");

    [Fact]
    public void Keeps_only_working_bridges()
    {
        var results = new[]
        {
            Result("obfs4 A", BridgeReachability.Reachable, 120),
            Result("obfs4 B", BridgeReachability.Unreachable, null),
            Result("obfs4 C", BridgeReachability.Slow, 800),
            Result("obfs4 D", BridgeReachability.Unparsed, null),
        };

        var selected = OnionHopClient.SelectReachableBridges(results);

        Assert.Contains("obfs4 A", selected);
        Assert.Contains("obfs4 C", selected);
        Assert.DoesNotContain("obfs4 B", selected); // unreachable dropped
        Assert.DoesNotContain("obfs4 D", selected); // unparsed dropped
    }

    [Fact]
    public void Orders_fastest_first()
    {
        var results = new[]
        {
            Result("slow", BridgeReachability.Slow, 800),
            Result("fast", BridgeReachability.Reachable, 40),
            Result("medium", BridgeReachability.Reachable, 200),
        };

        var selected = OnionHopClient.SelectReachableBridges(results).ToList();

        Assert.Equal(new[] { "fast", "medium", "slow" }, selected);
    }

    [Fact]
    public void Fronted_bridges_count_as_working()
    {
        // snowflake/dnstt have no pingable endpoint; the scanner reports them Fronted and they must
        // survive the filter (ordered after timed bridges since their ping is null).
        var results = new[]
        {
            Result("snowflake X", BridgeReachability.Fronted, null),
            Result("obfs4 Y", BridgeReachability.Reachable, 90),
        };

        var selected = OnionHopClient.SelectReachableBridges(results).ToList();

        Assert.Equal(new[] { "obfs4 Y", "snowflake X" }, selected);
    }

    [Fact]
    public void All_blocked_yields_empty_so_caller_can_fall_back()
    {
        var results = new[]
        {
            Result("a", BridgeReachability.Unreachable, null),
            Result("b", BridgeReachability.Unreachable, null),
        };

        Assert.Empty(OnionHopClient.SelectReachableBridges(results));
    }
}
