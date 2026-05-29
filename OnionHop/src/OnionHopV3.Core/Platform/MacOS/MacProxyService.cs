using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using OnionHopV3.Core.Platform;

namespace OnionHopV3.Core.Platform.MacOS;

internal sealed class MacProxyService : IProxyService
{
    private readonly List<ServiceSnapshot> _snapshots = [];
    private bool _applied;

    public bool IsApplied => _applied;

    public void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (MacSwiftHelper.TryRun(BuildSwiftProxyArgs("proxy apply", socksPort, httpPort), log))
        {
            _applied = true;
            return;
        }

        var services = GetNetworkServices();
        if (services.Count == 0)
        {
            log("Proxy setup: no active macOS network services found.");
            return;
        }

        _snapshots.Clear();
        foreach (var service in services)
        {
            _snapshots.Add(new ServiceSnapshot(
                service,
                ReadProxyState("-getsocksfirewallproxy", service),
                ReadProxyState("-getwebproxy", service),
                ReadProxyState("-getsecurewebproxy", service)));
        }

        var hadErrors = false;
        foreach (var service in services)
        {
            hadErrors |= !TryRunNetworkSetup($"-setsocksfirewallproxy \"{EscapeArg(service)}\" 127.0.0.1 {socksPort}");
            hadErrors |= !TryRunNetworkSetup($"-setsocksfirewallproxystate \"{EscapeArg(service)}\" on");

            if (httpPort.HasValue)
            {
                hadErrors |= !TryRunNetworkSetup($"-setwebproxy \"{EscapeArg(service)}\" 127.0.0.1 {httpPort.Value}");
                hadErrors |= !TryRunNetworkSetup($"-setsecurewebproxy \"{EscapeArg(service)}\" 127.0.0.1 {httpPort.Value}");
                hadErrors |= !TryRunNetworkSetup($"-setwebproxystate \"{EscapeArg(service)}\" on");
                hadErrors |= !TryRunNetworkSetup($"-setsecurewebproxystate \"{EscapeArg(service)}\" on");
            }
            else
            {
                hadErrors |= !TryRunNetworkSetup($"-setwebproxystate \"{EscapeArg(service)}\" off");
                hadErrors |= !TryRunNetworkSetup($"-setsecurewebproxystate \"{EscapeArg(service)}\" off");
            }
        }

        _applied = true;
        if (hadErrors)
        {
            log("Proxy setup on macOS reported command errors. Check permissions or install/use the macOS helper.");
        }
        else if (httpPort.HasValue)
        {
            log($"Proxy enabled (macOS): http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort}");
        }
        else
        {
            log($"Proxy enabled (macOS): socks=127.0.0.1:{socksPort}");
        }
    }

    public void RestorePreviousProxy(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS() || !_applied)
        {
            return;
        }

        if (MacSwiftHelper.TryRun("proxy restore", log))
        {
            _applied = false;
            return;
        }

        var hadErrors = false;
        foreach (var snapshot in _snapshots)
        {
            hadErrors |= !RestoreProxyState(snapshot.ServiceName, snapshot.Socks, "socks");
            hadErrors |= !RestoreProxyState(snapshot.ServiceName, snapshot.Web, "web");
            hadErrors |= !RestoreProxyState(snapshot.ServiceName, snapshot.SecureWeb, "secure");
        }

        _applied = false;
        if (hadErrors)
        {
            log("Proxy restore on macOS reported command errors.");
        }
        else
        {
            log("Proxy disabled (restored previous settings).");
        }
    }

    private static string BuildSwiftProxyArgs(string command, int socksPort, int? httpPort)
    {
        if (httpPort.HasValue)
        {
            return $"{command} --socks-port {socksPort} --http-port {httpPort.Value}";
        }

        return $"{command} --socks-port {socksPort}";
    }

    private static bool RestoreProxyState(string serviceName, ProxyState state, string kind)
    {
        var ok = true;
        var escapedService = EscapeArg(serviceName);
        if (!state.Enabled)
        {
            switch (kind)
            {
                case "socks":
                    ok &= TryRunNetworkSetup($"-setsocksfirewallproxystate \"{escapedService}\" off");
                    break;
                case "web":
                    ok &= TryRunNetworkSetup($"-setwebproxystate \"{escapedService}\" off");
                    break;
                case "secure":
                    ok &= TryRunNetworkSetup($"-setsecurewebproxystate \"{escapedService}\" off");
                    break;
            }

            return ok;
        }

        var host = string.IsNullOrWhiteSpace(state.Server) ? "127.0.0.1" : state.Server!;
        var port = state.Port is > 0 and <= 65535 ? state.Port.Value : 0;
        if (port <= 0)
        {
            return ok;
        }

        switch (kind)
        {
            case "socks":
                ok &= TryRunNetworkSetup($"-setsocksfirewallproxy \"{escapedService}\" {host} {port}");
                ok &= TryRunNetworkSetup($"-setsocksfirewallproxystate \"{escapedService}\" on");
                break;
            case "web":
                ok &= TryRunNetworkSetup($"-setwebproxy \"{escapedService}\" {host} {port}");
                ok &= TryRunNetworkSetup($"-setwebproxystate \"{escapedService}\" on");
                break;
            case "secure":
                ok &= TryRunNetworkSetup($"-setsecurewebproxy \"{escapedService}\" {host} {port}");
                ok &= TryRunNetworkSetup($"-setsecurewebproxystate \"{escapedService}\" on");
                break;
        }

        return ok;
    }

    private static List<string> GetNetworkServices()
    {
        var output = RunNetworkSetup("-listallnetworkservices");
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("An asterisk", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("*", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProxyState ReadProxyState(string command, string serviceName)
    {
        var output = RunNetworkSetup($"{command} \"{EscapeArg(serviceName)}\"");
        if (string.IsNullOrWhiteSpace(output))
        {
            return new ProxyState(false, null, null);
        }

        var enabled = output.Contains("Enabled: Yes", StringComparison.OrdinalIgnoreCase);
        var server = ParseLabelValue(output, "Server:");
        var portRaw = ParseLabelValue(output, "Port:");
        var port = int.TryParse(portRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int?)null;

        return new ProxyState(enabled, server, port);
    }

    private static string? ParseLabelValue(string output, string label)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var idx = trimmed.IndexOf(':');
            if (idx < 0 || idx + 1 >= trimmed.Length)
            {
                return null;
            }

            return trimmed[(idx + 1)..].Trim();
        }

        return null;
    }

    private static bool TryRunNetworkSetup(string args)
    {
        return RunNetworkSetupCommand(args).Success;
    }

    private static string RunNetworkSetup(string args)
    {
        return RunNetworkSetupCommand(args).Output;
    }

    private static (bool Success, string Output) RunNetworkSetupCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("networksetup", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, string.Empty);
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            var output = string.Join(Environment.NewLine, new[] { stdout, stderr }
                .Where(text => !string.IsNullOrWhiteSpace(text)))
                .Trim();
            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private readonly record struct ProxyState(bool Enabled, string? Server, int? Port);

    private readonly record struct ServiceSnapshot(
        string ServiceName,
        ProxyState Socks,
        ProxyState Web,
        ProxyState SecureWeb);
}
