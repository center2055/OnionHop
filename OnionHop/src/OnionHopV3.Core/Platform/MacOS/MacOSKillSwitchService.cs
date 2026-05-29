using System;

namespace OnionHopV3.Core.Platform.MacOS;

internal sealed class MacOSKillSwitchService : IKillSwitchService
{
    private const string AnchorName = "onionhop_killswitch";
    private const string PfRulesFile = "/tmp/onionhop_killswitch.conf";

    public void EnableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            log("Kill switch requires root privileges on macOS.");
            return;
        }

        try
        {
            var rules = $"anchor \"{AnchorName}\" {{\n  block out all\n  pass out on lo0 all\n  pass out inet proto tcp from any to any port 53\n  pass out inet proto udp from any to any port 53\n}}\n";
            System.IO.File.WriteAllText(PfRulesFile, rules);
            PlatformHelper.RunCommandSuccess("pfctl", $"-a {AnchorName} -f {PfRulesFile}");
            PlatformHelper.RunCommandSuccess("pfctl", "-e");
            log("Kill switch engaged: outbound traffic blocked (pf).");
        }
        catch (Exception ex)
        {
            log($"Kill switch enable failed: {ex.Message}");
        }
    }

    public void DisableEmergencyBlock(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            return;
        }

        try
        {
            PlatformHelper.RunCommandSuccess("pfctl", $"-a {AnchorName} -F all");
            log("Kill switch cleared (pf).");

            try
            {
                if (System.IO.File.Exists(PfRulesFile))
                {
                    System.IO.File.Delete(PfRulesFile);
                }
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            log($"Kill switch disable failed: {ex.Message}");
        }
    }

    public bool IsEmergencyBlockActive()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var output = PlatformHelper.RunCommand("pfctl", $"-a {AnchorName} -sr");
            return !string.IsNullOrWhiteSpace(output) && output.Contains("block", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
