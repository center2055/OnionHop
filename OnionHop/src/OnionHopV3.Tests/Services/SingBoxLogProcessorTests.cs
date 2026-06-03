using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public sealed class SingBoxLogProcessorTests
{
    [Fact]
    public void ProcessLine_ReturnsNull_ForNullInput()
    {
        var processor = new SingBoxLogProcessor();
        Assert.Null(processor.ProcessLine(null));
    }

    [Fact]
    public void ProcessLine_ReturnsNull_ForWhitespace()
    {
        var processor = new SingBoxLogProcessor();
        Assert.Null(processor.ProcessLine("   "));
    }

    [Fact]
    public void ProcessLine_StripsAnsiEscapes()
    {
        var processor = new SingBoxLogProcessor();
        var receivedLogs = new List<string>();
        processor.LogReceived += msg => receivedLogs.Add(msg);

        var result = processor.ProcessLine("\x1B[32msome log message\x1B[0m");

        Assert.Equal("some log message", result);
        Assert.Single(receivedLogs);
        Assert.Contains("some log message", receivedLogs[0]);
    }

    [Fact]
    public void ProcessLine_RaisesDnsLog_ForDnsLines()
    {
        var processor = new SingBoxLogProcessor();
        var dnsLogs = new List<string>();
        processor.DnsLogReceived += msg => dnsLogs.Add(msg);

        processor.ProcessLine("DNS query for example.com");

        Assert.Single(dnsLogs);
    }

    [Fact]
    public void ProcessLine_DoesNotRaiseDnsLog_ForNonDnsLines()
    {
        var processor = new SingBoxLogProcessor();
        var dnsLogs = new List<string>();
        processor.DnsLogReceived += msg => dnsLogs.Add(msg);

        processor.ProcessLine("connection to 10.0.0.1:443");

        Assert.Empty(dnsLogs);
    }

    [Fact]
    public void ProcessLine_LogsExitRejection_AsLogNote()
    {
        var processor = new SingBoxLogProcessor();
        var logs = new List<string>();
        processor.LogReceived += msg => logs.Add(msg);

        processor.ProcessLine("socks5: request rejected connection to example.com:22");

        // A transient per-connection exit rejection is surfaced as a log note (it no longer hijacks the
        // headline status, which looked alarming even though the tunnel was fine).
        Assert.Contains(logs, l => l.Contains("rejected a connection", StringComparison.OrdinalIgnoreCase)
                                   && l.Contains("retries on another circuit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetRecentLines_ReturnsEmptyInitially()
    {
        var processor = new SingBoxLogProcessor();
        Assert.Empty(processor.GetRecentLines());
    }

    [Fact]
    public void GetRecentLines_ReturnsProcessedLines()
    {
        var processor = new SingBoxLogProcessor();
        processor.ProcessLine("line 1");
        processor.ProcessLine("line 2");
        processor.ProcessLine("line 3");

        var lines = processor.GetRecentLines();
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void ClearRecentLines_Clears()
    {
        var processor = new SingBoxLogProcessor();
        processor.ProcessLine("line 1");
        processor.ClearRecentLines();

        Assert.Empty(processor.GetRecentLines());
    }

    [Theory]
    [InlineData("query doh server", true)]
    [InlineData("dns resolution failed", true)]
    [InlineData("hijack-dns rule matched", true)]
    [InlineData("[dns] cache hit", true)]
    [InlineData(" protocol=dns ", true)]
    [InlineData("connection to tcp://10.0.0.1:443", false)]
    public void LooksLikeDnsLogLine_DetectsCorrectly(string line, bool expected)
    {
        Assert.Equal(expected, SingBoxLogProcessor.LooksLikeDnsLogLine(line));
    }

    [Theory]
    [InlineData("[123 ", "123")]
    [InlineData("no brackets", null)]
    [InlineData("[abc ", null)]
    public void TryExtractConnectionId_ParsesCorrectly(string line, string? expected)
    {
        Assert.Equal(expected, SingBoxLogProcessor.TryExtractConnectionId(line));
    }
}
