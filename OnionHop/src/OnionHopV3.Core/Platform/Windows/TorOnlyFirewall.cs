using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnionHopV3.Core.Platform.Windows;

/// <summary>
/// Tor-only mode (#94): Windows Firewall blocks every outbound connection by default, and the only
/// way out is through rules in <see cref="RuleGroup"/>: OnionHop, Tor and its transports, DNS lookups
/// by the DNS Client service (the transports resolve their domain fronts through it), DHCP, IPv6
/// neighbor discovery, the local network, and while connected in TUN mode the tunnel adapter, whose
/// traffic goes through Tor. Outbound allow rules of other programs would still let those programs
/// out, so they are turned off while the mode is on and turned back on afterwards.
///
/// Unlike the kill switch it stays on while OnionHop is disconnected or closed and after a restart,
/// until it is turned off. Everything it changes is recorded in <see cref="StatePath"/> first, and it
/// all stays visible in Windows Firewall (wf.msc) under the rule group.
/// </summary>
internal static class TorOnlyFirewall
{
    public const string RuleGroup = "OnionHop Tor-only mode";
    private const string RulePrefix = "OnionHop Tor-only mode: ";
    private const string TunRuleName = RulePrefix + "TUN adapter";

    // Programs that may connect: the Tor engines and transports, and OnionHop itself, which downloads
    // bridges and checks which routes answer before Tor runs. In TUN mode Tor's own connections enter
    // the tunnel and leave through the tunnel core's direct route, so sing-box and Xray connect for
    // Tor; while the mode is on their config sends nothing else direct (OnionHopClient
    // .BuildVpnLaunchConfigAsync). Not the Snowflake volunteer proxy: its connections are not Tor's.
    internal static readonly string[] AllowedProgramNames =
    [
        "sing-box.exe",
        "xray.exe",
        "tor.exe",
        "arti.exe",
        "artihop.exe",
        "lyrebird.exe",
        "obfs4proxy.exe",
        "conjure-client.exe",
        "webtunnel-client.exe",
        "dnstt-client.exe",
        "snowflake-client.exe",
        "OnionHopV3.exe",
        "OnionHopV3.Cli.exe"
    ];

    internal static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OnionHop",
        "tor-only-mode.json");

    /// <summary>Whether this machine is in Tor-only mode, from the record written when it was turned on.</summary>
    public static bool IsOn => OperatingSystem.IsWindows() && File.Exists(StatePath);

    /// <summary>
    /// The OnionHop programs that may connect in Tor-only mode: the given files that exist, have one of
    /// <see cref="AllowedProgramNames"/> and lie under one of <paramref name="roots"/>. The elevated
    /// helper takes requests from unelevated processes, so it only ever allows OnionHop's own files,
    /// never a path a caller made up.
    /// </summary>
    internal static IReadOnlyList<string> FilterAllowedPrograms(IEnumerable<string> paths, IEnumerable<string> roots)
    {
        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar)
            .ToList();

        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            var name = Path.GetFileName(full);
            if (!AllowedProgramNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || !normalizedRoots.Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                || !File.Exists(full)
                || result.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(full);
        }

        return result;
    }

    /// <summary>Turns Tor-only mode on, or applies it again (rules refreshed, new exceptions turned off). Needs administrator rights.</summary>
    public static (bool Success, string Message) Enable(IReadOnlyList<string> programs, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Tor-only mode is only available on Windows.");
        }

        if (programs.Count == 0)
        {
            return (false, "No Tor programs were found to allow.");
        }

        var query = RunPowerShell(BuildQueryScript());
        if (!TryParseQuery(query.Output, out var snapshot))
        {
            return (false, $"Could not read the Windows Firewall settings: {Describe(query)}");
        }

        var off = snapshot.Profiles.Where(profile => !profile.Enabled).Select(profile => profile.Name).ToList();
        if (off.Count > 0)
        {
            return (false, $"Windows Firewall is off for the {string.Join(", ", off)} profile(s). Tor-only mode works through Windows Firewall, so turn it on first.");
        }

        // Record what is changed before changing anything. Applying again keeps the original outbound
        // actions from the first time and adds the exceptions turned off since.
        var state = LoadState() ?? new TorOnlyState
        {
            OutboundActions = snapshot.Profiles.ToDictionary(profile => profile.Name, profile => profile.DefaultOutboundAction, StringComparer.OrdinalIgnoreCase)
        };
        state.DisabledRules = state.DisabledRules
            .Concat(snapshot.OtherAllowRules)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        state.EnabledUtc ??= DateTimeOffset.UtcNow;
        SaveState(state);

        var rules = RunPowerShell(BuildRulesScript(WithShortPaths(programs)));
        if (rules.ExitCode != 0 || !rules.Output.Contains("RULES_ADDED", StringComparison.Ordinal))
        {
            Disable(log);
            return (false, $"Windows Firewall did not accept the rules: {Describe(rules)}");
        }

        foreach (var batch in snapshot.OtherAllowRules.Chunk(RuleBatchSize))
        {
            var turnedOff = RunPowerShell(BuildSetRulesEnabledScript(batch, enabled: false));
            if (turnedOff.ExitCode != 0 || !turnedOff.Output.Contains("DONE", StringComparison.Ordinal))
            {
                Disable(log);
                return (false, $"Windows Firewall did not turn off the other programs' exceptions: {Describe(turnedOff)}");
            }
        }

        var block = RunPowerShell(BuildBlockScript());
        if (block.ExitCode == 4 || block.Output.Contains("POLICY_OVERRIDDEN", StringComparison.Ordinal))
        {
            Disable(log);
            return (false, "Group Policy sets this computer's firewall, so the outbound default could not be changed to block.");
        }

        if (block.ExitCode != 0 || !block.Output.Contains("BLOCKED", StringComparison.Ordinal))
        {
            Disable(log);
            return (false, $"Windows Firewall did not change the outbound default to block: {Describe(block)}");
        }

        log($"Tor-only mode on: Windows Firewall blocks outbound connections except Tor's ({programs.Count} programs allowed, {snapshot.OtherAllowRules.Count} other outbound exceptions turned off).");
        return (true, "Tor-only mode is on.");
    }

    /// <summary>Turns Tor-only mode off and restores what it changed. Needs administrator rights.</summary>
    public static (bool Success, string Message) Disable(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Tor-only mode is only available on Windows.");
        }

        // Nothing to undo when it was never on: an uninstall calls this either way, and the outbound
        // default of someone who set it to block on purpose must stay as it is.
        var state = LoadState();
        if (state == null && !GroupRulesExist())
        {
            return (true, "Tor-only mode is off.");
        }

        // The outbound default first: once it is back, nothing depends on the rest any more.
        var result = RunPowerShell(BuildRestoreScript(state));
        foreach (var batch in (state?.DisabledRules ?? []).Chunk(RuleBatchSize))
        {
            RunPowerShell(BuildSetRulesEnabledScript(batch, enabled: true));
        }

        if (result.ExitCode != 0 || !result.Output.Contains("RESTORED", StringComparison.Ordinal))
        {
            return (false, $"Windows Firewall did not take the changes back: {Describe(result)}");
        }

        try
        {
            File.Delete(StatePath);
        }
        catch (Exception ex)
        {
            return (false, $"Tor-only mode is off, but its record could not be removed: {ex.Message}");
        }

        log("Tor-only mode off: Windows Firewall's outbound settings and the other programs' exceptions are back as they were.");
        return (true, "Tor-only mode is off.");
    }

    // Only a clear answer counts: when it cannot be told, the firewall is left alone.
    private static bool GroupRulesExist()
    {
        var result = RunPowerShell($"Write-Output ('COUNT ' + @(Get-NetFirewallRule -Group '{Quote(RuleGroup)}' -ErrorAction SilentlyContinue).Count)");
        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"COUNT (\d+)");
        return match.Success && int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// In TUN mode apps connect through the tunnel adapter, which takes them to Tor, so let traffic
    /// into it. The adapter gets a new name on every connect (VpnService.ResolveTunInterfaceName).
    /// </summary>
    public static void AllowTunAdapterIfOn(string interfaceAlias, Action<string> log)
    {
        if (!IsOn || string.IsNullOrWhiteSpace(interfaceAlias))
        {
            return;
        }

        var result = RunPowerShell(BuildTunScript(interfaceAlias));
        log(result.ExitCode == 0 && result.Output.Contains("TUN_ALLOWED", StringComparison.Ordinal)
            ? $"Tor-only mode: traffic into the {interfaceAlias} tunnel adapter is allowed."
            : $"Tor-only mode: could not allow the {interfaceAlias} tunnel adapter, so apps cannot reach Tor through it: {Describe(result)}");
    }

    // ----- PowerShell ------------------------------------------------------------------------------

    internal static string BuildQueryScript() => """
        $ErrorActionPreference = 'Stop'
        $group = '__GROUP__'
        $active = @{}
        Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object { $active[[string]$_.Name] = ([string]$_.Enabled -eq 'True') }
        $profiles = @(Get-NetFirewallProfile -PolicyStore PersistentStore | ForEach-Object {
            [pscustomobject]@{ Name = [string]$_.Name; Enabled = [bool]$active[[string]$_.Name]; DefaultOutboundAction = [string]$_.DefaultOutboundAction }
        })
        $others = @(Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
            Where-Object { $_.Group -ne $group } | ForEach-Object { [string]$_.Name })
        Write-Output ('QUERY ' + (ConvertTo-Json -Compress -Depth 4 @{ Profiles = $profiles; OtherAllowRules = $others }))
        """.Replace("__GROUP__", Quote(RuleGroup), StringComparison.Ordinal);

    // Rule names go into the script, and an encoded PowerShell command has to fit Windows' 32767
    // character command line, so other programs' rules are handled this many at a time.
    private const int RuleBatchSize = 100;

    internal static string BuildRulesScript(IReadOnlyList<string> programs)
    {
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$group = '{Quote(RuleGroup)}'");
        script.AppendLine("$svchost = Join-Path $env:SystemRoot 'System32\\svchost.exe'");
        // A failing rule names itself in the error.
        script.AppendLine("function Allow([hashtable]$rule) { try { New-NetFirewallRule -Group $group -Direction Outbound -Action Allow -Profile Any @rule -ErrorAction Stop | Out-Null } catch { throw ('rule ''' + $rule.DisplayName + ''': ' + $_.Exception.Message) } }");
        script.AppendLine("Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule");

        foreach (var program in programs)
        {
            script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix + Path.GetFileName(program))}'; Program = '{Quote(program)}' }}");
        }

        // DNS lookups go out through the DNS Client service, including the transports' lookups of their
        // domain fronts; 853 and 443 are DNS over TLS and HTTPS. DHCP keeps the address lease, ICMPv6
        // neighbor discovery keeps IPv6 working, and the local network (subnet, link-local, multicast)
        // stays reachable: none of this leaves the local network except DNS.
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}DNS lookups (UDP)'; Program = $svchost; Service = 'Dnscache'; Protocol = 'UDP'; RemotePort = '53' }}");
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}DNS lookups (TCP)'; Program = $svchost; Service = 'Dnscache'; Protocol = 'TCP'; RemotePort = @('53', '853', '443') }}");
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}DHCP'; Program = $svchost; Service = 'Dhcp'; Protocol = 'UDP'; LocalPort = '68'; RemotePort = '67' }}");
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}DHCPv6'; Program = $svchost; Service = 'Dhcp'; Protocol = 'UDP'; LocalPort = '546'; RemotePort = '547' }}");
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}IPv6 neighbor discovery'; Protocol = 'ICMPv6'; IcmpType = @('133', '135', '136', '143') }}");
        script.AppendLine($"Allow @{{ DisplayName = '{Quote(RulePrefix)}local network'; RemoteAddress = @('LocalSubnet', 'fe80::/10', '224.0.0.0/4', 'ff00::/8') }}");
        // No rule for loopback, where apps reach Tor's proxy: Windows Firewall does not filter it, and
        // refuses rules that name it ("an unspecified, multicast, broadcast, or loopback IPv6 address").

        // An adapter of an earlier TUN connect is still up when the mode is turned on while connected.
        script.AppendLine("$tun = @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'OnionHop*' -and $_.Status -eq 'Up' })");
        script.AppendLine($"foreach ($a in $tun) {{ Allow @{{ DisplayName = '{Quote(TunRuleName)}'; InterfaceAlias = $a.Name }} }}");
        script.AppendLine("Write-Output 'RULES_ADDED'");
        return script.ToString();
    }

    internal static string BuildSetRulesEnabledScript(IEnumerable<string> ruleNames, bool enabled)
    {
        var cmdlet = enabled ? "Enable-NetFirewallRule" : "Disable-NetFirewallRule";
        var script = new StringBuilder();
        script.AppendLine("$names = @(" + string.Join(", ", ruleNames.Select(name => $"'{Quote(name)}'")) + ")");
        // One at a time: a rule removed in the meantime must not stop the others.
        script.AppendLine($"foreach ($name in $names) {{ {cmdlet} -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue }}");
        script.AppendLine("Write-Output 'DONE'");
        return script.ToString();
    }

    internal static string BuildBlockScript() => """
        $ErrorActionPreference = 'Stop'
        Set-NetFirewallProfile -PolicyStore PersistentStore -Profile Domain, Private, Public -DefaultOutboundAction Block
        $open = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object { [string]$_.DefaultOutboundAction -ne 'Block' } | ForEach-Object { $_.Name })
        if ($open.Count -gt 0) { Write-Output ('POLICY_OVERRIDDEN ' + ($open -join ',')); exit 4 }
        Write-Output 'BLOCKED'
        """;

    internal static string BuildRestoreScript(TorOnlyState? state)
    {
        var script = new StringBuilder();
        // Each step on its own: a profile Group Policy holds must not stop the rest.
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.AppendLine($"$group = '{Quote(RuleGroup)}'");

        var actions = state?.OutboundActions ?? new Dictionary<string, string>();
        foreach (var profile in new[] { "Domain", "Private", "Public" })
        {
            var action = actions.TryGetValue(profile, out var saved) && saved is "Allow" or "Block" or "NotConfigured"
                ? saved
                : "Allow";
            script.AppendLine($"Set-NetFirewallProfile -PolicyStore PersistentStore -Profile {profile} -DefaultOutboundAction {action}");
        }

        script.AppendLine("Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        script.AppendLine("if (@(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue).Count -gt 0) { Write-Output 'RULES_LEFT'; exit 5 }");
        script.AppendLine("Write-Output 'RESTORED'");
        return script.ToString();
    }

    internal static string BuildTunScript(string interfaceAlias) => $$"""
        $ErrorActionPreference = 'Stop'
        $alias = '{{Quote(interfaceAlias)}}'
        Get-NetFirewallRule -DisplayName '{{Quote(TunRuleName)}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        for ($i = 0; $i -lt 40 -and -not (Get-NetAdapter -IncludeHidden -Name $alias -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
        New-NetFirewallRule -Group '{{Quote(RuleGroup)}}' -DisplayName '{{Quote(TunRuleName)}}' -Direction Outbound -Action Allow -Profile Any -InterfaceAlias $alias | Out-Null
        Write-Output 'TUN_ALLOWED'
        """;

    internal static bool TryParseQuery(string output, out FirewallSnapshot snapshot)
    {
        snapshot = new FirewallSnapshot();
        var line = output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.StartsWith("QUERY ", StringComparison.Ordinal));
        if (line == null)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FirewallSnapshot>(line["QUERY ".Length..]);
            if (parsed?.Profiles is not { Count: > 0 })
            {
                return false;
            }

            parsed.OtherAllowRules ??= [];
            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    internal static string Describe((int ExitCode, string Output) result)
    {
        // The scripts report a failure on a line of its own (ScriptPreamble). PowerShell writes what
        // goes to a redirected error stream as CLIXML, so take the error text out of that if needed.
        var error = result.Output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("ERROR ", StringComparison.Ordinal));
        var text = error?["ERROR ".Length..] ?? result.Output.Trim();
        if (error == null && text.Contains("#< CLIXML", StringComparison.Ordinal))
        {
            var errors = System.Text.RegularExpressions.Regex.Matches(text, "<S S=\"Error\">(.*?)</S>")
                .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Replace("_x000D__x000A_", " ", StringComparison.Ordinal)).Trim());
            text = string.Join(" ", errors).Trim();
        }

        if (text.Length > 600)
        {
            text = text[..600] + "...";
        }

        return string.IsNullOrEmpty(text) ? $"PowerShell exit code {result.ExitCode}" : text;
    }

    // Before every script: no progress records (they come out as CLIXML noise when the output is
    // redirected), and a failure as one plain line on standard output.
    internal const string ScriptPreamble = """
        $ProgressPreference = 'SilentlyContinue'
        trap { Write-Output ('ERROR ' + ($_.Exception.Message -replace '\s+', ' ')); exit 1 }

        """;

    private static (int ExitCode, string Output) RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ScriptPreamble + script));
        var psi = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return (-1, "PowerShell did not start.");
            }

            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(180_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, "PowerShell did not finish within 3 minutes.");
            }

            var output = stdout + (string.IsNullOrWhiteSpace(stderr.Result) ? string.Empty : "\n" + stderr.Result);
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // Tor starts transports by their 8.3 short path when the long one has a space (TorBridgeManager),
    // and a firewall program rule matches the path a process was started with, so allow both.
    private static IReadOnlyList<string> WithShortPaths(IReadOnlyList<string> programs)
    {
        var all = new List<string>(programs);
        foreach (var program in programs.Where(program => program.Contains(' ', StringComparison.Ordinal)))
        {
            var shortPath = GetShortPath(program);
            if (!string.IsNullOrEmpty(shortPath) && !all.Contains(shortPath, StringComparer.OrdinalIgnoreCase))
            {
                all.Add(shortPath);
            }
        }

        return all;
    }

    private static string? GetShortPath(string longPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    // ----- State -----------------------------------------------------------------------------------

    internal static TorOnlyState? LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<TorOnlyState>(File.ReadAllText(StatePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(TorOnlyState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed class TorOnlyState
    {
        public DateTimeOffset? EnabledUtc { get; set; }

        /// <summary>Each profile's outbound default before Tor-only mode: Allow, Block or NotConfigured.</summary>
        public Dictionary<string, string> OutboundActions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The other programs' outbound allow rules (rule names) that were turned off.</summary>
        public List<string> DisabledRules { get; set; } = [];
    }

    internal sealed class FirewallSnapshot
    {
        public List<FirewallProfile> Profiles { get; set; } = [];
        public List<string>? OtherAllowRules { get; set; } = [];
    }

    internal sealed class FirewallProfile
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string DefaultOutboundAction { get; set; } = string.Empty;
    }
}
