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
        Assert.False(ArtiHopService.SupportsExitCountry(@"C:\does\not\exist\artihop.exe"));
    }

    // The exit country (--exit-country, EXITCOUNTRY) came with ArtiHop 0.3.0. An older build gets
    // neither: it would reject the flag and never open its SOCKS port.
    [Theory]
    [InlineData("0.1.0", false, false)]
    [InlineData("0.2.0", true, false)]
    [InlineData("0.2.9", true, false)]
    [InlineData("0.3.0", true, true)]
    [InlineData("1.0.0", true, true)]
    public void Options_go_only_to_builds_that_know_them(string version, bool control, bool exitCountry)
    {
        Assert.Equal(control, ArtiHopService.SupportsControlListener(Version.Parse(version)));
        Assert.Equal(exitCountry, ArtiHopService.SupportsExitCountry(Version.Parse(version)));
        Assert.False(ArtiHopService.SupportsControlListener((Version?)null));
        Assert.False(ArtiHopService.SupportsExitCountry((Version?)null));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData(" US ", "us")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("Automatic", "auto")]
    [InlineData("d3", "auto")]
    [InlineData("de; NEWNYM", "auto")]
    public void The_exit_country_sent_to_ArtiHop_is_two_letters_or_auto(string? input, string expected)
    {
        Assert.Equal(expected, ArtiHopService.NormalizeExitCountry(input));
    }

    // What ArtiHop cannot apply is named, so a setting is never dropped without a word. The exit
    // location is not in the list any more: ArtiHop 0.3 applies it.
    [Fact]
    public void Settings_ArtiHop_cannot_apply_are_named()
    {
        var none = OnionHopV3.Core.OnionHopClient.DescribeSettingsArtiHopIgnores(new OnionHopV3.Core.OnionHopConnectOptions
        {
            SelectedLocation = "Germany"
        });
        Assert.Empty(none);

        var both = OnionHopV3.Core.OnionHopClient.DescribeSettingsArtiHopIgnores(new OnionHopV3.Core.OnionHopConnectOptions
        {
            SelectedEntryLocation = "France",
            ExitNodeFingerprint = "5450CC0E3D08BB001E8229B8990323D11BC63332"
        });
        Assert.Equal(["an entry location", "pinned relays"], both);
    }
}
