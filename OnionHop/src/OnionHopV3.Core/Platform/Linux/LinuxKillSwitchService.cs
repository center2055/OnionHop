using System;

namespace OnionHopV3.Core.Platform.Linux;

internal sealed class LinuxKillSwitchService : IKillSwitchService
{
    private const string ChainName = "ONIONHOP_KILLSWITCH";

    public void EnableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            log("Kill switch requires root privileges on Linux.");
            return;
        }

        try
        {
            var ipv4Enabled = ConfigureIptablesFamily("iptables");
            var ipv6Available = PlatformHelper.IsCommandAvailable("ip6tables");
            var ipv6Enabled = ipv6Available && ConfigureIptablesFamily("ip6tables");

            if (!ipv4Enabled)
            {
                throw new InvalidOperationException("iptables did not accept the kill switch rules.");
            }

            if (ipv6Available && !ipv6Enabled)
            {
                log("IPv6 kill switch setup failed; IPv4 traffic is blocked. Disable IPv6 or use TUN/VPN mode for full leak protection.");
            }

            log(ipv6Enabled
                ? "Kill switch engaged: outbound IPv4/IPv6 traffic blocked (iptables/ip6tables)."
                : "Kill switch engaged: outbound IPv4 traffic blocked (iptables).");
        }
        catch (Exception ex)
        {
            log($"Kill switch enable failed: {ex.Message}");
        }
    }

    public void DisableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            return;
        }

        try
        {
            ClearIptablesFamily("iptables");
            ClearIptablesFamily("ip6tables");
            log("Kill switch cleared (iptables/ip6tables).");
        }
        catch (Exception ex)
        {
            log($"Kill switch disable failed: {ex.Message}");
        }
    }

    public bool IsEmergencyBlockActive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return ChainReferencedInOutput("iptables") || ChainReferencedInOutput("ip6tables");
        }
        catch
        {
            return false;
        }
    }

    private static bool ConfigureIptablesFamily(string command)
    {
        if (!PlatformHelper.IsCommandAvailable(command))
        {
            return false;
        }

        PlatformHelper.RunCommandSuccess(command, $"-N {ChainName}");
        if (!PlatformHelper.RunCommandSuccess(command, $"-F {ChainName}"))
        {
            PlatformHelper.RunCommandSuccess(command, $"-X {ChainName}");
            if (!PlatformHelper.RunCommandSuccess(command, $"-N {ChainName}") ||
                !PlatformHelper.RunCommandSuccess(command, $"-F {ChainName}"))
            {
                return false;
            }
        }

        if (!PlatformHelper.RunCommandSuccess(command, $"-A {ChainName} -o lo -j ACCEPT") ||
            !PlatformHelper.RunCommandSuccess(command, $"-A {ChainName} -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT") ||
            !PlatformHelper.RunCommandSuccess(command, $"-A {ChainName} -j DROP"))
        {
            return false;
        }

        return ChainReferencedInOutput(command) ||
               PlatformHelper.RunCommandSuccess(command, $"-I OUTPUT 1 -j {ChainName}");
    }

    private static void ClearIptablesFamily(string command)
    {
        if (!PlatformHelper.IsCommandAvailable(command))
        {
            return;
        }

        PlatformHelper.RunCommandSuccess(command, $"-D OUTPUT -j {ChainName}");
        PlatformHelper.RunCommandSuccess(command, $"-F {ChainName}");
        PlatformHelper.RunCommandSuccess(command, $"-X {ChainName}");
    }

    private static bool ChainReferencedInOutput(string command)
    {
        var output = PlatformHelper.RunCommand(command, "-L OUTPUT -n");
        return output != null && output.Contains(ChainName, StringComparison.Ordinal);
    }
}
