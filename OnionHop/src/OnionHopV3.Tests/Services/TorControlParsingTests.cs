using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public class TorControlParsingTests
{
    [Fact]
    public void ParseConnectedOrFingerprints_multiline_reply_returns_connected_only()
    {
        const string response = """
            250+orconn-status=
            $74FAD13168806246602538555B5521A0383A1875~mybridge CONNECTED
            $DA1ECF055635C1A6ED7F5B5F36296A5E3015CE57~other LAUNCHED
            $10A6CD36A537FCE513A322361547444B393989F0~third CONNECTED
            .
            250 OK
            """;

        var fingerprints = TorService.ParseConnectedOrFingerprints(response);

        Assert.Equal(2, fingerprints.Count);
        Assert.Contains("74FAD13168806246602538555B5521A0383A1875", fingerprints);
        Assert.Contains("10A6CD36A537FCE513A322361547444B393989F0", fingerprints);
        Assert.DoesNotContain("DA1ECF055635C1A6ED7F5B5F36296A5E3015CE57", fingerprints);
    }

    [Fact]
    public void ParseConnectedOrFingerprints_inline_single_connection_reply()
    {
        const string response = "250-orconn-status=$74fad13168806246602538555b5521a0383a1875~bridge CONNECTED\r\n250 OK";

        var fingerprints = TorService.ParseConnectedOrFingerprints(response);

        Assert.Single(fingerprints);
        // Normalized to uppercase so callers can match case-insensitively stored bridge fingerprints.
        Assert.Equal("74FAD13168806246602538555B5521A0383A1875", fingerprints[0]);
    }

    [Fact]
    public void ParseConnectedOrFingerprints_empty_or_garbage_returns_nothing()
    {
        Assert.Empty(TorService.ParseConnectedOrFingerprints("250 OK"));
        Assert.Empty(TorService.ParseConnectedOrFingerprints(""));
        Assert.Empty(TorService.ParseConnectedOrFingerprints("$notahex~x CONNECTED"));
    }
}
