using System.Diagnostics;
using OnionHopV3.Core.Platform;
using Xunit;

namespace OnionHopV3.Tests.Platform;

/// <summary>
/// snowflake-proxy reports its traffic as "↓ 7 KB, ↑ 10 KB" in UTF-8. Decoded in the Windows code
/// page, the arrows showed as "鈫?" on Chinese Windows (#91).
/// </summary>
public sealed class ProcessOutputEncodingTests
{
    // Writes the raw UTF-8 bytes of "↓ 7 KB" to standard output, the way a Go program does.
    // printf gets its format as one argument: .NET does not split on single quotes.
    private static ProcessStartInfo WriteDownArrow() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("powershell", "-NoProfile -Command \"$o=[Console]::OpenStandardOutput(); $b=[byte[]](0xE2,0x86,0x93,0x20,0x37,0x20,0x4B,0x42); $o.Write($b,0,$b.Length); $o.Flush()\"")
        : new ProcessStartInfo("printf") { ArgumentList = { "\\342\\206\\223 7 KB" } };

    [Fact]
    public async Task A_helpers_utf8_output_reads_back_as_written()
    {
        var psi = WriteDownArrow();
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WithUtf8Output();

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal("↓ 7 KB", output.Trim());
    }

    [Fact]
    public void Only_redirected_streams_get_an_encoding()
    {
        // Setting an encoding on a stream that is not redirected makes Process.Start throw.
        var psi = new ProcessStartInfo("tool") { RedirectStandardOutput = true }.WithUtf8Output();

        Assert.NotNull(psi.StandardOutputEncoding);
        Assert.Null(psi.StandardErrorEncoding);
    }
}
