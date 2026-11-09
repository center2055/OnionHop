using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OnionHopV3.Core.Platform.Windows;
using Xunit;

namespace OnionHopV3.Tests.Platform.Windows;

public sealed class TorOnlyFirewallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onionhop-toronly-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private string MakeFile(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    // The elevated helper takes the list from an unelevated process: only OnionHop's own programs, in
    // OnionHop's folders, may come through, whatever else is asked for.
    [Fact]
    public void Only_OnionHops_programs_in_OnionHops_folders_are_allowed()
    {
        var runtime = Path.Combine(_root, "OnionHop");
        var tor = MakeFile("OnionHop", "tor", "tor.exe");
        var lyrebird = MakeFile("OnionHop", "tor", "pluggable_transports", "lyrebird.exe");
        var notOurs = MakeFile("OnionHop", "tor", "curl.exe");
        var lookalikeFolder = MakeFile("OnionHopEvil", "tor.exe");
        var outside = MakeFile("Elsewhere", "tor.exe");
        var missing = Path.Combine(runtime, "artihop", "artihop.exe");
        var traversal = Path.Combine(runtime, "..", "Elsewhere", "tor.exe");

        var allowed = TorOnlyFirewall.FilterAllowedPrograms(
            [tor, lyrebird, notOurs, lookalikeFolder, outside, missing, traversal, tor.ToUpperInvariant(), "", "C:\\Windows\\System32\\curl.exe"],
            [runtime]);

        Assert.Equal([tor, lyrebird], allowed);
    }

    [Fact]
    public void The_volunteer_proxy_is_not_let_out_but_the_tunnel_cores_are()
    {
        // The Snowflake proxy's connections are not Tor's. The tunnel cores carry Tor's own connections
        // in TUN mode, and get nothing else to send direct while the mode is on.
        Assert.DoesNotContain("snowflake-proxy.exe", TorOnlyFirewall.AllowedProgramNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sing-box.exe", TorOnlyFirewall.AllowedProgramNames);
        Assert.Contains("xray.exe", TorOnlyFirewall.AllowedProgramNames);
        Assert.Contains("tor.exe", TorOnlyFirewall.AllowedProgramNames);
        Assert.Contains("lyrebird.exe", TorOnlyFirewall.AllowedProgramNames);
    }

    [Fact]
    public void The_rules_script_quotes_paths_and_lets_out_only_what_Tor_needs()
    {
        var script = TorOnlyFirewall.BuildRulesScript([@"C:\Users\O'Brien\AppData\Local\OnionHop\tor\tor.exe"]);

        Assert.Contains(@"Program = 'C:\Users\O''Brien\AppData\Local\OnionHop\tor\tor.exe'", script, StringComparison.Ordinal);
        Assert.Contains("DisplayName = 'OnionHop Tor-only mode: tor.exe'", script, StringComparison.Ordinal);
        Assert.Contains("Service = 'Dnscache'; Protocol = 'UDP'; RemotePort = '53'", script, StringComparison.Ordinal);
        Assert.Contains("Service = 'Dhcp'; Protocol = 'UDP'; LocalPort = '68'; RemotePort = '67'", script, StringComparison.Ordinal);
        Assert.Contains("RemoteAddress = @('LocalSubnet', 'fe80::/10', '224.0.0.0/4', 'ff00::/8')", script, StringComparison.Ordinal);
        // Windows Firewall refuses rules that name loopback addresses, and never filters loopback.
        Assert.DoesNotContain("::1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.", script, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $env:SystemRoot 'System32\svchost.exe'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-NetFirewallRule", script, StringComparison.Ordinal);
        Assert.EndsWith("Write-Output 'RULES_ADDED'" + Environment.NewLine, script, StringComparison.Ordinal);
        // The outbound default changes only after every rule is in place.
        Assert.DoesNotContain("DefaultOutboundAction", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Restoring_puts_back_each_profiles_own_outbound_default()
    {
        var state = new TorOnlyFirewall.TorOnlyState
        {
            OutboundActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Domain"] = "Block",
                ["Private"] = "NotConfigured",
                ["Public"] = "Allow; Remove-Item C:\\ -Recurse"
            }
        };

        var script = TorOnlyFirewall.BuildRestoreScript(state);

        Assert.Contains("-Profile Domain -DefaultOutboundAction Block", script, StringComparison.Ordinal);
        Assert.Contains("-Profile Private -DefaultOutboundAction NotConfigured", script, StringComparison.Ordinal);
        // Anything but the three known values falls back to Windows' default instead of being run.
        Assert.Contains("-Profile Public -DefaultOutboundAction Allow" + Environment.NewLine, script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", script, StringComparison.Ordinal);

        var noRecord = TorOnlyFirewall.BuildRestoreScript(null);
        Assert.Equal(3, noRecord.Split("-DefaultOutboundAction Allow").Length - 1);
    }

    // An encoded PowerShell command has to fit Windows' 32767-character command line.
    [Fact]
    public void A_batch_of_other_programs_rules_fits_on_a_command_line()
    {
        var names = Enumerable.Range(0, 100)
            .Select(i => "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}-SomeVeryLongWindowsAppContainerRuleName-" + i)
            .ToList();

        var script = TorOnlyFirewall.BuildSetRulesEnabledScript(names, enabled: false);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Assert.True(encoded.Length < 32_000, $"encoded command is {encoded.Length} characters");
        Assert.Contains("Disable-NetFirewallRule -PolicyStore PersistentStore -Name $name", script, StringComparison.Ordinal);
        Assert.Contains("Enable-NetFirewallRule", TorOnlyFirewall.BuildSetRulesEnabledScript(["a'b"], enabled: true), StringComparison.Ordinal);
        Assert.Contains("'a''b'", TorOnlyFirewall.BuildSetRulesEnabledScript(["a'b"], enabled: true), StringComparison.Ordinal);
    }

    [Fact]
    public void The_firewall_query_is_read_from_powershells_output()
    {
        const string output = """
            WARNING: something unrelated
            QUERY {"Profiles":[{"Name":"Domain","Enabled":true,"DefaultOutboundAction":"NotConfigured"},{"Name":"Private","Enabled":true,"DefaultOutboundAction":"Allow"},{"Name":"Public","Enabled":false,"DefaultOutboundAction":"Allow"}],"OtherAllowRules":["CoreNet-DNS-Out-UDP","{1E2C}"]}
            """;

        Assert.True(TorOnlyFirewall.TryParseQuery(output, out var snapshot));
        Assert.Equal(["Domain", "Private", "Public"], snapshot.Profiles.Select(p => p.Name));
        Assert.False(snapshot.Profiles[2].Enabled);
        Assert.Equal("NotConfigured", snapshot.Profiles[0].DefaultOutboundAction);
        Assert.Equal(["CoreNet-DNS-Out-UDP", "{1E2C}"], snapshot.OtherAllowRules);

        Assert.True(TorOnlyFirewall.TryParseQuery("""QUERY {"Profiles":[{"Name":"Public","Enabled":true,"DefaultOutboundAction":"Allow"}],"OtherAllowRules":null}""", out var noOthers));
        Assert.Empty(noOthers.OtherAllowRules!);
        Assert.False(TorOnlyFirewall.TryParseQuery("Get-NetFirewallProfile : Access is denied.", out _));
        Assert.False(TorOnlyFirewall.TryParseQuery("""QUERY {"Profiles":[]}""", out _));
    }

    // A failure has to say what failed, not show PowerShell's serialized progress records.
    [Fact]
    public void Failures_are_reported_as_plain_text()
    {
        Assert.Equal(
            "rule 'OnionHop Tor-only mode: DHCP': The parameter is incorrect.",
            TorOnlyFirewall.Describe((1, "noise\r\nERROR rule 'OnionHop Tor-only mode: DHCP': The parameter is incorrect.\r\n")));

        const string clixml = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>x</T></TN></Obj>"
            + "<S S=\"Error\">New-NetFirewallRule : Access is denied._x000D__x000A_</S><S S=\"Error\">At line:1 char:1_x000D__x000A_</S></Objs>";
        Assert.Equal("New-NetFirewallRule : Access is denied. At line:1 char:1", TorOnlyFirewall.Describe((1, clixml)));
        Assert.Equal("PowerShell exit code 5", TorOnlyFirewall.Describe((5, "  ")));
        Assert.StartsWith("$ProgressPreference = 'SilentlyContinue'", TorOnlyFirewall.ScriptPreamble, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tunnel_rule_follows_the_adapter_name_of_each_connect()
    {
        var script = TorOnlyFirewall.BuildTunScript("OnionHop-3f2a");

        Assert.Contains("$alias = 'OnionHop-3f2a'", script, StringComparison.Ordinal);
        Assert.Contains("-InterfaceAlias $alias", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetFirewallRule -DisplayName 'OnionHop Tor-only mode: TUN adapter'", script, StringComparison.Ordinal);
    }
}
