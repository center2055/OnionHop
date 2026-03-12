using System;

namespace OnionHopV2.Core.Platform.Linux;

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
            RunIptables($"-N {ChainName}");
            RunIptables($"-F {ChainName}");
            RunIptables($"-A {ChainName} -o lo -j ACCEPT");
            RunIptables($"-A {ChainName} -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT");
            RunIptables($"-A {ChainName} -j DROP");

            if (!ChainReferencedInOutput())
            {
                RunIptables($"-I OUTPUT 1 -j {ChainName}");
            }

            log("Kill switch engaged: outbound traffic blocked (iptables).");
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
            RunIptables($"-D OUTPUT -j {ChainName}");
            RunIptables($"-F {ChainName}");
            RunIptables($"-X {ChainName}");
            log("Kill switch cleared (iptables).");
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
            return ChainReferencedInOutput();
        }
        catch
        {
            return false;
        }
    }

    private static bool ChainReferencedInOutput()
    {
        var output = PlatformHelper.RunCommand("iptables", "-L OUTPUT -n");
        return output != null && output.Contains(ChainName, StringComparison.Ordinal);
    }

    private static void RunIptables(string args)
    {
        PlatformHelper.RunCommand("iptables", args);
    }
}
