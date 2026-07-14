using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class Obfs4HandshakeVerifierTests
{
    [Theory]
    [InlineData("obfs4 1.2.3.4:443 FINGERPRINT cert=abcDEF123 iat-mode=0", true)]
    [InlineData("Bridge obfs4 5.6.7.8:9001 FP cert=x iat-mode=1", false)] // "Bridge " prefix -> first token not obfs4
    [InlineData("webtunnel [2001:db8::1]:443 FP url=https://x", false)]
    [InlineData("obfs4 [2001:db8::1]:443 FP cert=x iat-mode=0", true)]    // still an obfs4 line (IPv4-only split is in the parser)
    [InlineData("", false)]
    public void IsObfs4Line_detects_obfs4(string line, bool expected)
    {
        Assert.Equal(expected, Obfs4HandshakeVerifier.IsObfs4Line(line));
    }

    [Fact]
    public void TryParseObfs4Ipv4_extracts_endpoint_and_args()
    {
        var ok = Obfs4HandshakeVerifier.TryParseObfs4Ipv4(
            "obfs4 92.63.169.52:4444 6E51423BA6693C627F12CBA99951B05123CBFE76 cert=abc123DEF iat-mode=0",
            out var host, out var port, out var args);

        Assert.True(ok);
        Assert.Equal("92.63.169.52", host);
        Assert.Equal(4444, port);
        Assert.Equal("cert=abc123DEF;iat-mode=0", args);
    }

    [Fact]
    public void TryParseObfs4Ipv4_defaults_iat_mode_when_absent()
    {
        var ok = Obfs4HandshakeVerifier.TryParseObfs4Ipv4(
            "obfs4 1.2.3.4:443 FP cert=xyz", out _, out _, out var args);

        Assert.True(ok);
        Assert.Equal("cert=xyz;iat-mode=0", args);
    }

    [Theory]
    [InlineData("obfs4 [2001:db8::1]:443 FP cert=x iat-mode=0")] // IPv6 - not parsed by the v4 parser
    [InlineData("obfs4 1.2.3.4:443 FP iat-mode=0")]              // no cert
    [InlineData("vanilla 1.2.3.4:443 FINGERPRINT")]               // not obfs4
    [InlineData("obfs4 1.2.3.4:notaport FP cert=x")]              // bad port
    public void TryParseObfs4Ipv4_rejects_non_ipv4_obfs4(string line)
    {
        Assert.False(Obfs4HandshakeVerifier.TryParseObfs4Ipv4(line, out _, out _, out _));
    }
}
