using OnionHopV3.Core;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// "Connection canceled or timed out." did not say which part was blocked (#89). A timeout now
/// names the bootstrap step it stopped at and what that means.
/// </summary>
public sealed class AttemptTimeoutMessageTests
{
    [Theory]
    [InlineData(0, "Starting", "Timed out at 0% (Starting): nothing answered.")]
    [InlineData(2, "Connected to pluggable transport", "Timed out at 2% (Connected to pluggable transport): the transport program did not get going.")]
    [InlineData(5, "Connecting to a relay", "Timed out at 5% (Connecting to a relay): no connection to a bridge or relay got through.")]
    [InlineData(14, "Handshaking with a relay", "Timed out at 14% (Handshaking with a relay): a connection was made, but the handshake through it did not finish.")]
    [InlineData(50, "Loading relay descriptors", "Timed out at 50% (Loading relay descriptors): the route works, but the download through it stalled.")]
    public void Names_the_step_and_what_it_means(int percent, string summary, string expected)
    {
        Assert.Equal(expected, OnionHopClient.DescribeAttemptTimeout(percent, summary, transportFailures: 0, torPhases: true));
    }

    [Fact]
    public void Counts_the_transports_failed_connections()
    {
        Assert.Equal(
            "Timed out at 5% (Connecting to a relay): no connection to a bridge or relay got through. The transport reported 12 failed connections.",
            OnionHopClient.DescribeAttemptTimeout(5, "Connecting to a relay.", transportFailures: 12, torPhases: true));
    }

    [Fact]
    public void Without_a_step_only_the_percentage_is_given()
    {
        Assert.Equal("Timed out at 0%: nothing answered.", OnionHopClient.DescribeAttemptTimeout(0, null, 0, torPhases: true));
    }

    [Fact]
    public void Arti_engines_count_their_own_percentages_so_only_the_step_is_named()
    {
        Assert.Equal(
            "Timed out at 45% (fetching directory).",
            OnionHopClient.DescribeAttemptTimeout(45, "fetching directory", 0, torPhases: false));
    }
}
