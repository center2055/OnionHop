using System;
using Microsoft.Win32;

namespace OnionHopV3.Core.Platform.Windows;

/// <summary>
/// Explains the Windows-side cause behind "set ipv6 address: Element not found", which stops the TUN
/// core from starting at all (#81).
///
/// A reporter traced their failure to the DisabledComponents value under the IPv6 service key. That
/// value is absent on a stock Windows install; once some tweak or "debloat" script writes it, Windows
/// can leave IPv6 half-broken even when it is set to 0, the value that nominally means "everything
/// enabled". That is why no API check catches this: IPv6 reports as available right up to the point
/// where assigning an address to a freshly created adapter fails. Deleting the value fixed it for them.
///
/// We only ever read it. Silently rewriting a machine-wide network setting is the user's call, not
/// ours, so this produces a hint for the log and nothing else.
/// </summary>
public static class WindowsIpv6Diagnostics
{
    private const string Tcpip6ParametersKey = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private const string DisabledComponentsValue = "DisabledComponents";

    /// <summary>
    /// A hint to log when the tunnel is refused an IPv6 address, or null when this machine looks
    /// normal and there is nothing useful to say.
    /// </summary>
    public static string? DescribeSuspectIpv6Configuration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Tcpip6ParametersKey, writable: false);
            var raw = key?.GetValue(DisabledComponentsValue);
            if (raw == null)
            {
                // The healthy state: stock Windows does not define this value at all.
                return null;
            }

            var isDword = key!.GetValueKind(DisabledComponentsValue) == RegistryValueKind.DWord;
            return BuildHint(FormatDisabledComponents(raw, isDword));
        }
        catch
        {
            // A diagnostic is never worth failing a connection over.
            return null;
        }
    }

    internal static string FormatDisabledComponents(object raw, bool isDword) =>
        isDword && raw is int dword ? $"0x{dword:X}" : $"'{raw}' (unexpected type)";

    internal static string BuildHint(string shownValue) =>
        $"Heads-up: Windows has an IPv6 \"{DisabledComponentsValue}\" override set to {shownValue} "
        + $"under HKLM\\{Tcpip6ParametersKey}. A stock Windows install does not have that value at all, "
        + "and it can leave IPv6 half-working even when set to 0, which is what stops the tunnel from "
        + "taking an IPv6 address. Deleting that value and rebooting has fixed this for other users. "
        + "OnionHop will not touch it for you: it is a system-wide network setting.";
}
