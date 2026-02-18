using System;
using System.Diagnostics;

namespace OnionHopV2.Core.Platform.Windows;

internal sealed class WindowsOnionDnsProxyService : IDnsProxyService
{
    private const string RuleComment = "OnionHopV2-OnionDnsProxy";

    public bool Enable(string nameServerAddress, Action<string> log)
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
            ExecutePowerShell(
                "$ErrorActionPreference='Stop'; Add-DnsClientNrptRule -Namespace '.onion' -NameServers '" + safeNameServer + "' -Comment '" + RuleComment + "' | Out-Null");
            log($".onion DNS proxying enabled (NRPT rule added, nameserver={safeNameServer}).");
            return true;
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying enable failed: {ex.Message}");
            return false;
        }
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
