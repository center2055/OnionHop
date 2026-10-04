using System;
using OnionHopV3.Core;
using Xunit;
using Decision = OnionHopV3.Core.OnionHopClient.AttemptDeadline;

namespace OnionHopV3.Tests.Core;

public sealed class AttemptDeadlineTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    [Fact]
    public void Within_the_budget_the_attempt_just_runs()
    {
        Assert.Equal(Decision.Continue, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromSeconds(30), Budget, 0, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_transport_that_never_got_through_ends_at_the_budget()
    {
        // 10%: the pluggable transport accepted the connection, but no handshake with a bridge yet.
        Assert.Equal(Decision.End, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromSeconds(45), Budget, 10, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void A_slow_transport_that_keeps_progressing_gets_more_time()
    {
        Assert.Equal(Decision.Extend, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromSeconds(50), Budget, 50, TimeSpan.FromSeconds(20)));
        Assert.Equal(Decision.Extend, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromMinutes(3), Budget, 80, TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Progress_that_stalls_or_runs_past_the_cap_ends_the_attempt()
    {
        Assert.Equal(Decision.End, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromSeconds(90), Budget, 50, TimeSpan.FromSeconds(61)));
        Assert.Equal(Decision.End, OnionHopClient.DecideAttemptDeadline(TimeSpan.FromMinutes(4), Budget, 95, TimeSpan.FromSeconds(1)));
    }
}
