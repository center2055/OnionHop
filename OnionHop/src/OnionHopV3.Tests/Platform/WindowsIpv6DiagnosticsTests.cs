using OnionHopV3.Core.Platform.Windows;
using Xunit;

namespace OnionHopV3.Tests.Platform;

/// <summary>
/// The hint shown when Windows refuses the tunnel an IPv6 address. A reporter traced that failure to
/// a DisabledComponents override that a stock Windows install does not have at all, so the hint has
/// to name the value, show what it is set to, and stay honest about the fact that 0 is not safe (#81).
/// </summary>
public sealed class WindowsIpv6DiagnosticsTests
{
    [Fact]
    public void Formats_a_dword_as_hex()
    {
        Assert.Equal("0x0", WindowsIpv6Diagnostics.FormatDisabledComponents(0, isDword: true));
        Assert.Equal("0xFF", WindowsIpv6Diagnostics.FormatDisabledComponents(255, isDword: true));
        Assert.Equal("0x20", WindowsIpv6Diagnostics.FormatDisabledComponents(32, isDword: true));
    }

    [Fact]
    public void Calls_out_a_value_written_with_the_wrong_type()
    {
        // A tweak script writing "0" as text is itself a plausible cause, so do not hide the type.
        var shown = WindowsIpv6Diagnostics.FormatDisabledComponents("0", isDword: false);

        Assert.Contains("unexpected type", shown);
        Assert.Contains("0", shown);
    }

    [Fact]
    public void Hint_names_the_value_the_registry_path_and_the_fix()
    {
        var hint = WindowsIpv6Diagnostics.BuildHint("0x0");

        Assert.Contains("DisabledComponents", hint);
        Assert.Contains(@"Tcpip6\Parameters", hint);
        Assert.Contains("0x0", hint);
        Assert.Contains("Deleting", hint);
    }

    [Fact]
    public void Hint_says_zero_is_not_a_safe_value()
    {
        // The whole point: the reporter's value was 0 and IPv6 was still broken, so a hint that
        // implied "0 means fine" would send them straight back to square one.
        Assert.Contains("even when set to 0", WindowsIpv6Diagnostics.BuildHint("0x0"));
    }

    [Fact]
    public void Reading_the_machine_configuration_never_throws()
    {
        // Runs on whatever machine builds this; it must degrade to null rather than break a connect.
        var hint = WindowsIpv6Diagnostics.DescribeSuspectIpv6Configuration();

        if (hint != null)
        {
            Assert.Contains("DisabledComponents", hint);
        }
    }
}
