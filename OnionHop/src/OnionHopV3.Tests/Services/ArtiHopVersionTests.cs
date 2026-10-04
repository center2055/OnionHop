using System;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// OnionHop only passes --control to ArtiHop builds that report 0.2.0 or newer: older ones reject
/// the flag and exit before opening their SOCKS port, which would break connecting outright.
/// </summary>
public sealed class ArtiHopVersionTests
{
    [Theory]
    [InlineData("artihop 0.2.0", 0, 2, 0)]
    [InlineData("artihop 0.2.0\n", 0, 2, 0)]
    [InlineData("artihop 0.10.3", 0, 10, 3)]
    [InlineData("artihop 1.0.0-dev", 1, 0, 0)]
    [InlineData("ArtiHop 0.3.1+build.5", 0, 3, 1)]
    public void Reads_the_clap_version_line(string output, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), ArtiHopService.ParseArtiHopVersion(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("arti 2.7.0")]
    [InlineData("artihop")]
    [InlineData("error: unexpected argument '--version' found")]
    public void Anything_else_is_no_version(string? output)
    {
        Assert.Null(ArtiHopService.ParseArtiHopVersion(output));
    }

    [Fact]
    public void A_missing_binary_does_not_support_the_control_listener()
    {
        Assert.False(ArtiHopService.SupportsControlListener(@"C:\does\not\exist\artihop.exe"));
    }
}
