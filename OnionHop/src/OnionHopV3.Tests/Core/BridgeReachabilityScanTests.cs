using System.Collections.Generic;
using System.Linq;
using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// The pre-connect reachability scan has a fixed time budget, so a huge pasted bridge list (hundreds of
/// lines, e.g. a Shared Camouflage set) can't all be probed in time. Rather than time out and fall back
/// to the whole unfiltered, mostly-dead list, the scan shuffles large lists and samples a representative
/// slice within the budget. <see cref="OnionHopClient.ShuffleBridgeLines"/> must produce a faithful
/// permutation so no bridge is dropped or duplicated by the sampling step.
/// </summary>
public sealed class BridgeReachabilityScanTests
{
    private static IReadOnlyList<string> MakeBridges(int count) =>
        Enumerable.Range(0, count).Select(i => $"obfs4 10.0.0.{i}:443 FINGERPRINT{i} cert=abc iat-mode=0").ToList();

    [Fact]
    public void Shuffle_PreservesEveryBridge_NoLossOrDuplication()
    {
        var input = MakeBridges(500);

        var shuffled = OnionHopClient.ShuffleBridgeLines(input);

        // Same count and exactly the same multiset of lines - just reordered.
        Assert.Equal(input.Count, shuffled.Count);
        Assert.Equal(input.OrderBy(x => x), shuffled.OrderBy(x => x));
        Assert.Equal(input.Distinct().Count(), shuffled.Distinct().Count());
    }

    [Fact]
    public void Shuffle_DoesNotMutateInput()
    {
        var input = MakeBridges(50);
        var snapshot = input.ToList();

        OnionHopClient.ShuffleBridgeLines(input);

        Assert.Equal(snapshot, input);
    }

    [Fact]
    public void Shuffle_ActuallyReorders_ForLargeList()
    {
        // With 500 distinct items the odds of a Fisher-Yates shuffle reproducing the exact input order
        // are 1/500! - effectively zero - so an unchanged order would mean the shuffle isn't running.
        var input = MakeBridges(500);

        var shuffled = OnionHopClient.ShuffleBridgeLines(input);

        Assert.NotEqual(input, shuffled);
    }

    [Fact]
    public void Shuffle_HandlesEmptyAndSingle()
    {
        Assert.Empty(OnionHopClient.ShuffleBridgeLines(new List<string>()));

        var single = new List<string> { "snowflake 192.0.2.1:443" };
        Assert.Equal(single, OnionHopClient.ShuffleBridgeLines(single));
    }
}
