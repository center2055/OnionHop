using System.Collections.Generic;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// The Tor argument log is exportable by the user, so it must never contain the upstream-proxy
/// password or full bridge lines (which are anti-censorship credentials).
/// </summary>
public sealed class TorArgumentRedactionTests
{
    [Fact]
    public void Redacts_socks5_username_and_password()
    {
        var args = new List<string>
        {
            "--SocksPort", "127.0.0.1:9050",
            "--Socks5ProxyUsername", "alice",
            "--Socks5ProxyPassword", "s3cret"
        };

        var rendered = TorService.FormatArgumentsForLog(args);

        Assert.DoesNotContain("s3cret", rendered);
        Assert.DoesNotContain("alice", rendered);
        Assert.Contains("--Socks5ProxyPassword ***", rendered);
        Assert.Contains("--Socks5ProxyUsername ***", rendered);
    }

    [Fact]
    public void Redacts_https_authenticator()
    {
        var args = new List<string> { "--HTTPSProxy", "proxy:8080", "--HTTPSProxyAuthenticator", "user:pass" };

        var rendered = TorService.FormatArgumentsForLog(args);

        Assert.DoesNotContain("user:pass", rendered);
        Assert.Contains("--HTTPSProxyAuthenticator ***", rendered);
    }

    [Fact]
    public void Masks_bridge_fingerprint_and_cert_but_keeps_transport_and_endpoint()
    {
        var args = new List<string> { "--Bridge", "obfs4 1.2.3.4:443 ABCDEF0123456789 cert=SECRETCERT iat-mode=0" };

        var rendered = TorService.FormatArgumentsForLog(args);

        Assert.DoesNotContain("SECRETCERT", rendered);
        Assert.DoesNotContain("ABCDEF0123456789", rendered);
        Assert.Contains("obfs4 1.2.3.4:443 ***", rendered);
    }

    [Fact]
    public void Non_secret_arguments_pass_through()
    {
        var args = new List<string> { "--SocksPort", "127.0.0.1:9050", "--ClientOnly", "1" };

        var rendered = TorService.FormatArgumentsForLog(args);

        Assert.Contains("--SocksPort 127.0.0.1:9050", rendered);
        Assert.Contains("--ClientOnly 1", rendered);
    }
}
