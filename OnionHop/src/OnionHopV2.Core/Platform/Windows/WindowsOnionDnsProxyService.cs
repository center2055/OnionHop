using System;
using System.Diagnostics;

namespace OnionHopV2.Core.Platform.Windows;

internal sealed class WindowsOnionDnsProxyService : IDnsProxyService
{
    private const string RuleComment = "OnionHopV3-OnionDnsProxy";

    public bool Enable(string nameServerAddress, bool routeAllDns, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!WindowsAdmin.IsAdministrator())
        {
            log(".onion DNS proxying requires Administrator.");
            return false;
        }

        try
        {
            var safeNameServer = string.IsNullOrWhiteSpace(nameServerAddress)
                ? "127.0.0.1"
                : nameServerAddress.Trim();
            RemoveRule(log);

            // Always keep .onion pointed at the Tor resolver.
            AddNrptRule(".onion", safeNameServer);

            if (routeAllDns)
            {
                // The single "." namespace is NRPT's catch-all: it matches every DNS query,
                // forcing all name resolution through Tor's DNSPort so normal lookups can no
                // longer leak to the system/ISP resolver. The more specific ".onion" rule above
                // still wins for onion addresses. Both rules share the same comment, so Disable()
                // removes them together. This is fail-closed: if Tor stops resolving, DNS stops
                // rather than silently falling back to a direct (leaking) resolver.
                //
                // The catch-all is added separately so that, if a particular Windows build rejects
                // the "." namespace, we keep the .onion protection and the connection rather than
                // failing outright — but we warn loudly because the user asked for full protection.
                try
                {
                    AddNrptRule(".", safeNameServer);
                    log($"Full DNS-over-Tor leak protection enabled (all DNS routed to {safeNameServer} via Tor).");
                }
                catch (Exception ex)
                {
                    log($"WARNING: full DNS-over-Tor rule could not be installed ({ex.Message}). " +
                        "Normal DNS may leak to your ISP. Use TUN/VPN Mode for guaranteed leak-free DNS.");
                }
            }
            else
            {
                log($".onion DNS proxying enabled (NRPT rule added, nameserver={safeNameServer}).");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"DNS-over-Tor enable failed: {ex.Message}");
            return false;
        }
    }

    private static void AddNrptRule(string namespacePattern, string nameServer)
    {
        ExecutePowerShell(
            "$ErrorActionPreference='Stop'; Add-DnsClientNrptRule -Namespace '" + namespacePattern +
            "' -NameServers '" + nameServer + "' -Comment '" + RuleComment + "' | Out-Null");
    }

    public void Disable(Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || !WindowsAdmin.IsAdministrator())
        {
            return;
        }

        try
        {
            RemoveRule(log);
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying cleanup failed: {ex.Message}");
        }
    }

    private static void RemoveRule(Action<string> log)
    {
        ExecutePowerShell(
            "$ErrorActionPreference='SilentlyContinue'; " +
            "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + RuleComment + "' } | Remove-DnsClientNrptRule -Force | Out-Null");
        log(".onion DNS proxying rule cleared.");
    }

    private static void ExecutePowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell.");
        process.WaitForExit(15000);
        var error = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"PowerShell exited with code {process.ExitCode}."
                : error);
        }
    }
}
