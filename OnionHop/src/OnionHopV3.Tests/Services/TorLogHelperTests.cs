using System;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class TorLogHelperTests
{
    [Theory]
    [InlineData("Bootstrapped 25% (requesting_status): Asking for networkstatus consensus", 25)]
    [InlineData("Bootstrapped 100% (done): Done", 100)]
    [InlineData("Bootstrapped 0% (starting): Starting", 0)]
    [InlineData("Bootstrapped 75% (loading_descriptors): Loading relay descriptors", 75)]
    [InlineData("No percent sign here", 0)]
    [InlineData("", 0)]
    public void ExtractProgress_ParsesCorrectly(string line, int expected)
    {
        Assert.Equal(expected, TorLogHelper.ExtractProgress(line));
    }

    [Theory]
    [InlineData("Bootstrapped 25% (requesting_status): Asking for networkstatus consensus", "Asking for networkstatus consensus")]
    [InlineData("Bootstrapped 100% (done): Done", "Done")]
    [InlineData("No parenthesis colon here", null)]
    [InlineData("", null)]
    public void ExtractBootstrapSummary_ParsesCorrectly(string line, string? expected)
    {
        Assert.Equal(expected, TorLogHelper.ExtractBootstrapSummary(line));
    }

    [Theory]
    [InlineData("no configured transport called obfs4", true)]
    [InlineData("no such transport is supported", true)]
    [InlineData("failed to bind port 9050", true)]
    [InlineData("address already in use", true)]
    [InlineData("Normal log line", false)]
    [InlineData("Bootstrapped 50%", false)]
    public void IsFatalTorBootstrapLine_DetectsCorrectly(string line, bool expected)
    {
        Assert.Equal(expected, TorLogHelper.IsFatalTorBootstrapLine(line));
    }

    [Theory]
    [InlineData("handshaking (proxy) general SOCKS server failure", true)]
    [InlineData("normal connection message", false)]
    [InlineData("handshaking (proxy) succeeded", false)]
    public void IsTorProxyHandshakeFailureLine_DetectsCorrectly(string line, bool expected)
    {
        Assert.Equal(expected, TorLogHelper.IsTorProxyHandshakeFailureLine(line));
    }

    [Theory]
    [InlineData("[warn] Something happened", true)]
    [InlineData("[err] Fatal error", true)]
    [InlineData("Failed to connect", true)]
    [InlineData("Bootstrapped 100%", false)]
    [InlineData("SOCKS listener listening", false)]
    public void ShouldLogTorLine_DetectsCorrectly(string line, bool expected)
    {
        Assert.Equal(expected, TorLogHelper.ShouldLogTorLine(line));
    }

    [Fact]
    public void ParseAllowedPorts_DefaultWhenNull()
    {
        var ports = TorLogHelper.ParseAllowedPorts(null);
        Assert.Equal(new[] { 80, 443 }, ports);
    }

    [Fact]
    public void ParseAllowedPorts_DefaultWhenEmpty()
    {
        var ports = TorLogHelper.ParseAllowedPorts("  ");
        Assert.Equal(new[] { 80, 443 }, ports);
    }

    [Fact]
    public void ParseAllowedPorts_ParsesCommaDelimited()
    {
        var ports = TorLogHelper.ParseAllowedPorts("80, 443, 8080");
        Assert.Equal(new[] { 80, 443, 8080 }, ports);
    }

    [Fact]
    public void ParseAllowedPorts_IgnoresInvalidValues()
    {
        var ports = TorLogHelper.ParseAllowedPorts("80, abc, 443, -1, 99999");
        Assert.Equal(new[] { 80, 443 }, ports);
    }

    [Fact]
    public void ParseAllowedPorts_DeduplicatesPorts()
    {
        var ports = TorLogHelper.ParseAllowedPorts("80, 443, 80, 443");
        Assert.Equal(new[] { 80, 443 }, ports);
    }

    [Fact]
    public void ParseProcessNames_ReturnsEmptyForNull()
    {
        var names = TorLogHelper.ParseProcessNames(null);
        Assert.Empty(names);
    }

    [Fact]
    public void ParseProcessNames_ParsesNewlineDelimited()
    {
        var names = TorLogHelper.ParseProcessNames("firefox.exe\nchrome.exe\nmsedge.exe");
        Assert.Equal(new[] { "firefox.exe", "chrome.exe", "msedge.exe" }, names);
    }

    [Fact]
    public void ParseProcessNames_IgnoresComments()
    {
        var names = TorLogHelper.ParseProcessNames("# this is a comment\nfirefox.exe");
        Assert.Equal(new[] { "firefox.exe" }, names);
    }

    [Fact]
    public void ParseProcessNames_ExtractsFilenameFromPath()
    {
        // Paths without spaces are extracted correctly
        var names = TorLogHelper.ParseProcessNames(@"C:\Users\test\firefox.exe");
        Assert.Equal(new[] { "firefox.exe" }, names);
    }

    [Fact]
    public void ParseProcessNames_Deduplicates()
    {
        var names = TorLogHelper.ParseProcessNames("firefox.exe\nFirefox.exe\nFIREFOX.EXE");
        Assert.Single(names);
        Assert.Equal("firefox.exe", names[0]);
    }

    [Fact]
    public void ParseProcessNames_AppendsExeOnWindowsWhenMissing()
    {
        // Users copy friendly names like "FreeTube" / "browser" (Yandex) from Task
        // Manager; sing-box needs the .exe image name, so it is added automatically.
        var names = TorLogHelper.ParseProcessNames("FreeTube\nbrowser\nchrome.exe", isWindows: true);
        Assert.Equal(new[] { "FreeTube.exe", "browser.exe", "chrome.exe" }, names);
    }

    [Fact]
    public void ParseProcessNames_DoesNotAppendExeOffWindows()
    {
        var names = TorLogHelper.ParseProcessNames("freetube\nchrome", isWindows: false);
        Assert.Equal(new[] { "freetube", "chrome" }, names);
    }

    [Theory]
    [InlineData(9050, 9050)]
    [InlineData(0, 9050)]
    [InlineData(-1, 9050)]
    [InlineData(70000, 9050)]
    [InlineData(8080, 8080)]
    [InlineData(65535, 65535)]
    [InlineData(1, 1)]
    public void NormalizePreferredProxyPort_ReturnsCorrectly(int preferred, int expected)
    {
        Assert.Equal(expected, TorLogHelper.NormalizePreferredProxyPort(preferred, 9050));
    }

    [Theory]
    [InlineData(null, 60, 60)]
    [InlineData(0, 60, null)]
    [InlineData(-1, 60, null)]
    [InlineData(15, 60, 15)]
    [InlineData(5000, 60, 3600)]
    public void ResolveConnectTimeout_ReturnsExpectedSeconds(int? configured, int automaticSeconds, int? expectedSeconds)
    {
        var resolved = TorLogHelper.ResolveConnectTimeout(configured, TimeSpan.FromSeconds(automaticSeconds));
        if (expectedSeconds.HasValue)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds.Value), resolved);
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Default", null)]
    [InlineData("Enabled", true)]
    [InlineData("Disabled", false)]
    [InlineData("  ", null)]
    public void ParseToggleMode_ParsesCorrectly(string? mode, bool? expected)
    {
        Assert.Equal(expected, TorLogHelper.ParseToggleMode(mode));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Auto (recommended)", "auto")]
    [InlineData("Enabled", "1")]
    [InlineData("Disabled", "0")]
    [InlineData("  ", null)]
    public void ParseConnectionPaddingMode_ParsesCorrectly(string? mode, string? expected)
    {
        Assert.Equal(expected, TorLogHelper.ParseConnectionPaddingMode(mode));
    }

    [Theory]
    [InlineData(null, "mixed")]
    [InlineData("Mixed (recommended)", "mixed")]
    [InlineData("System", "system")]
    [InlineData("gVisor", "gvisor")]
    [InlineData("invalid", "mixed")]
    public void NormalizeTunStackModeForSingBox_ParsesCorrectly(string? mode, string expected)
    {
        Assert.Equal(expected, TorLogHelper.NormalizeTunStackModeForSingBox(mode));
    }

    [Fact]
    public void LimitBridgeLinesForLaunch_ReturnsAllWhenUnderLimit()
    {
        var lines = new[] { "bridge1", "bridge2", "bridge3" };
        var logMessages = new List<string>();
        var result = TorLogHelper.LimitBridgeLinesForLaunch(lines, 64, 12000, msg => logMessages.Add(msg));
        Assert.Equal(3, result.Count);
        Assert.Empty(logMessages);
    }

    [Fact]
    public void LimitBridgeLinesForLaunch_LimitsWhenOverMaxLines()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"bridge{i}").ToArray();
        var logMessages = new List<string>();
        var result = TorLogHelper.LimitBridgeLinesForLaunch(lines, 10, 50000, msg => logMessages.Add(msg));
        Assert.Equal(10, result.Count);
        Assert.Single(logMessages);
    }

    [Fact]
    public void LimitBridgeLinesForLaunch_ReturnsEmptyForEmptyInput()
    {
        var result = TorLogHelper.LimitBridgeLinesForLaunch(Array.Empty<string>(), 64, 12000, _ => { });
        Assert.Empty(result);
    }

    [Fact]
    public void BuildManualProxyHint_WithHttpPort()
    {
        var hint = TorLogHelper.BuildManualProxyHint("127.0.0.1", 9050, 9080);
        Assert.Contains("SOCKS 127.0.0.1:9050", hint);
        Assert.Contains("HTTP 127.0.0.1:9080", hint);
    }

    [Fact]
    public void BuildManualProxyHint_WithoutHttpPort()
    {
        var hint = TorLogHelper.BuildManualProxyHint("127.0.0.1", 9050, null);
        Assert.Contains("SOCKS 127.0.0.1:9050", hint);
        Assert.DoesNotContain("HTTP", hint);
    }

    [Fact]
    public void BuildManualProxyHint_UsesProvidedBindAddress()
    {
        var hint = TorLogHelper.BuildManualProxyHint("0.0.0.0", 9050, 9080);
        Assert.Contains("SOCKS 127.0.0.1:9050", hint);
        Assert.Contains("HTTP 127.0.0.1:9080", hint);
        Assert.Contains("LAN access is enabled", hint);
    }
}
