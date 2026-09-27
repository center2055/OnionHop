using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Core;

/// <summary>
/// Users are asked to paste their logs into public GitHub issues, so anything written to them is
/// effectively published. Censors scrape those issues for bridges, and account paths carry real names.
/// </summary>
public sealed class LogPrivacyTests
{
    [Fact]
    public void A_vanilla_bridge_keeps_its_endpoint_but_not_its_fingerprint()
    {
        // Exactly the line #81's reporter posted in full: vanilla bridges have no transport name, so
        // the old "transport endpoint" rule printed the fingerprint.
        var logged = TorService.RedactBridgeLine("46.243.1.156:6889 5D9EE7AA730F39263D04C90AAF23C66AB013E1C0");

        Assert.DoesNotContain("5D9EE7AA", logged);
        Assert.Contains("46.243.1.156:6889", logged);
    }

    [Fact]
    public void An_ipv6_vanilla_bridge_is_redacted_too()
    {
        var logged = TorService.RedactBridgeLine("[2001:db8::7]:443 5D9EE7AA730F39263D04C90AAF23C66AB013E1C0");

        Assert.DoesNotContain("5D9EE7AA", logged);
    }

    [Fact]
    public void An_obfs4_bridge_keeps_transport_and_endpoint_but_drops_the_secrets()
    {
        var logged = TorService.RedactBridgeLine(
            "obfs4 5.199.162.203:4433 A143C79FF77B4354E98907944F8B1966C30DBC58 cert=SECRETCERT iat-mode=0");

        Assert.Contains("obfs4 5.199.162.203:4433", logged);
        Assert.DoesNotContain("SECRETCERT", logged);
        Assert.DoesNotContain("A143C79F", logged);
    }

    [Fact]
    public void A_webtunnel_bridge_does_not_leak_its_url()
    {
        var logged = TorService.RedactBridgeLine(
            "webtunnel [2001:db8::1]:443 A143C79FF77B4354E98907944F8B1966C30DBC58 url=https://secret.example/path");

        Assert.DoesNotContain("secret.example", logged);
    }

    [Theory]
    [InlineData(@"Paths: baseDir=C:\Users\Jane Doe\AppData\Local\OnionHop", @"Paths: baseDir=%USERPROFILE%\AppData\Local\OnionHop")]
    [InlineData(@"exec C:\\Users\\Jane Doe\\AppData\\lyrebird.exe", @"exec %USERPROFILE%\\AppData\\lyrebird.exe")]
    [InlineData("config at C:/Users/Jane Doe/AppData/x.json", "config at %USERPROFILE%/AppData/x.json")]
    public void Windows_profile_paths_lose_the_account_name(string line, string expected)
    {
        Assert.Equal(expected, OnionHopClient.ScrubPersonalPaths(line, @"C:\Users\Jane Doe"));
    }

    [Fact]
    public void Unix_home_paths_become_a_tilde()
    {
        Assert.Equal("data in ~/.local/share/OnionHop",
            OnionHopClient.ScrubPersonalPaths("data in /home/jane/.local/share/OnionHop", "/home/jane"));
    }

    [Fact]
    public void Matching_ignores_drive_letter_and_folder_case()
    {
        Assert.Equal(@"%USERPROFILE%\tor",
            OnionHopClient.ScrubPersonalPaths(@"c:\users\jane doe\tor", @"C:\Users\Jane Doe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:")]
    public void Without_a_usable_profile_path_nothing_changes(string? profile)
    {
        Assert.Equal(@"C:\Users\x\tor", OnionHopClient.ScrubPersonalPaths(@"C:\Users\x\tor", profile));
    }
}
