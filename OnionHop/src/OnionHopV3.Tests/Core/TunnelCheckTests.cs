using OnionHopV3.Core;
using Xunit;
using static OnionHopV3.Core.OnionHopClient;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// The Home IP is fetched through Tor's SOCKS port, so it is a Tor exit whenever Tor runs even if the
/// tunnel carries nothing. The tunnel check compares a fresh connection's public IP against the one
/// seen before connecting (#83). A false "leaking" would alarm people for nothing and a false
/// "verified" would hide a real leak, so the verdict must be conservative in exactly one direction.
/// </summary>
public sealed class TunnelCheckTests
{
    [Fact]
    public void Same_address_as_before_connecting_is_a_leak()
    {
        Assert.Equal(TunnelCheckState.Leaking, TunnelCheckVerdict("91.236.142.16", "91.236.142.16"));
    }

    [Fact]
    public void A_different_address_means_the_tunnel_carried_it()
    {
        Assert.Equal(TunnelCheckState.Verified, TunnelCheckVerdict("91.236.142.16", "185.220.100.249"));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_hide_a_leak()
    {
        Assert.Equal(TunnelCheckState.Leaking, TunnelCheckVerdict(" 91.236.142.16\n", "91.236.142.16"));
    }

    [Theory]
    [InlineData(null, "185.220.100.249")]
    [InlineData("", "185.220.100.249")]
    [InlineData("91.236.142.16", null)]
    [InlineData("91.236.142.16", "not an ip")]
    public void Without_two_real_addresses_there_is_no_verdict(string? baseline, string? seen)
    {
        // Missing baseline (never saw the machine's own IP) or a failed lookup: say nothing rather
        // than guess, in either direction.
        Assert.Equal(TunnelCheckState.Unverifiable, TunnelCheckVerdict(baseline, seen));
    }

    [Fact]
    public void An_ipv4_baseline_is_not_compared_with_an_ipv6_answer()
    {
        // The machine's IPv6 address is not its IPv4 address, but that does not make it a Tor exit.
        // Calling this "verified" would hide a real leak on a dual-stack connection.
        Assert.Equal(TunnelCheckState.Unverifiable,
            TunnelCheckVerdict("91.236.142.16", "2a02:8108:1234:5678::1"));
    }

    [Fact]
    public void Ipv6_addresses_compare_by_value_not_by_spelling()
    {
        Assert.Equal(TunnelCheckState.Leaking,
            TunnelCheckVerdict("2a02:8108:0:0:0:0:0:1", "2a02:8108::1"));
    }

    [Fact]
    public void No_bypass_rules_means_a_direct_answer_cannot_be_explained_away()
    {
        Assert.False(HasDirectRoutingRules(new OnionHopConnectOptions()));
        Assert.False(HasDirectRoutingRules(new OnionHopConnectOptions { BypassCountries = "  ", BypassRoutingRules = "" }));
    }

    [Theory]
    [InlineData("example.com", null, null)]
    [InlineData(null, "us", null)]
    [InlineData(null, null, "banking")]
    public void Any_bypass_rule_can_explain_a_direct_answer(string? sites, string? countries, string? categories)
    {
        // A country rule for the US alone covers most IP lookup services, so a "real IP" answer is
        // expected there and must not raise the red "not protected" banner.
        Assert.True(HasDirectRoutingRules(new OnionHopConnectOptions
        {
            BypassRoutingRules = sites,
            BypassCountries = countries,
            BypassSiteCategories = categories
        }));
    }

    [Fact]
    public void Block_rules_do_not_count_as_bypass()
    {
        // Blocking sends nothing direct, so it cannot explain the real IP coming back.
        Assert.False(HasDirectRoutingRules(new OnionHopConnectOptions { BlockRoutingRules = "example.com" }));
    }
}

/// <summary>
/// Users are asked to paste their logs into public GitHub issues. Those logs used to carry the real
/// IP from the direct IP check in full.
/// </summary>
public sealed class LogIpMaskingTests
{
    [Fact]
    public void Masks_the_host_part_of_an_ipv4_address()
    {
        Assert.Equal("91.236.x.x", MaskIpForLog("91.236.142.16"));
    }

    [Fact]
    public void Masks_all_but_the_prefix_of_an_ipv6_address()
    {
        Assert.Equal("2a02:8108:x:x", MaskIpForLog("2a02:8108:1234:5678::1"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("--.--.--.--", "--.--.--.--")]
    public void Leaves_non_addresses_alone(string? input, string expected)
    {
        Assert.Equal(expected, MaskIpForLog(input));
    }
}
