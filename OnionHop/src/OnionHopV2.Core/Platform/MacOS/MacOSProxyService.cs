using System;

namespace OnionHopV2.Core.Platform.MacOS;

internal sealed class MacOSProxyService : IProxyService
{
    private bool _applied;
    private string? _previousSocksHost;
    private string? _previousSocksPort;
    private string? _previousSocksEnabled;
    private string? _previousHttpHost;
    private string? _previousHttpPort;
    private string? _previousHttpEnabled;
    private string? _previousHttpsHost;
    private string? _previousHttpsPort;
    private string? _previousHttpsEnabled;
    private bool _savedPrevious;

    public bool IsApplied => _applied;

    public void ApplyTorProxy(int socksPort, int? httpPort, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!_savedPrevious)
        {
            SavePreviousProxy();
            _savedPrevious = true;
        }

        var networkService = GetActiveNetworkService();
        if (string.IsNullOrWhiteSpace(networkService))
        {
            log("Proxy update failed: could not determine active network service.");
            _applied = true;
            log($"Proxy enabled: socks=127.0.0.1:{socksPort} (manual configuration required — no active network service detected).");
            return;
        }

        try
        {
            RunNetworkSetup($"-setsocksfirewallproxy \"{networkService}\" 127.0.0.1 {socksPort}");
            RunNetworkSetup($"-setsocksfirewallproxystate \"{networkService}\" on");

            if (httpPort.HasValue)
            {
                RunNetworkSetup($"-setwebproxy \"{networkService}\" 127.0.0.1 {httpPort.Value}");
                RunNetworkSetup($"-setwebproxystate \"{networkService}\" on");
                RunNetworkSetup($"-setsecurewebproxy \"{networkService}\" 127.0.0.1 {httpPort.Value}");
                RunNetworkSetup($"-setsecurewebproxystate \"{networkService}\" on");
                log($"Proxy enabled (macOS networksetup): http/https=127.0.0.1:{httpPort.Value}, socks=127.0.0.1:{socksPort}");
            }
            else
            {
                log($"Proxy enabled (macOS networksetup): socks=127.0.0.1:{socksPort}");
            }

            _applied = true;
        }
        catch (Exception ex)
        {
            log($"macOS proxy setup failed: {ex.Message}");
            _applied = true;
        }
    }

    public void RestorePreviousProxy(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!_applied)
        {
            return;
        }

        var networkService = GetActiveNetworkService();
        if (string.IsNullOrWhiteSpace(networkService))
        {
            _applied = false;
            log("Proxy disabled (could not determine network service to restore).");
            return;
        }

        try
        {
            var socksEnabled = string.Equals(_previousSocksEnabled?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
            if (socksEnabled && !string.IsNullOrWhiteSpace(_previousSocksHost) && !string.IsNullOrWhiteSpace(_previousSocksPort))
            {
                RunNetworkSetup($"-setsocksfirewallproxy \"{networkService}\" {_previousSocksHost} {_previousSocksPort}");
                RunNetworkSetup($"-setsocksfirewallproxystate \"{networkService}\" on");
            }
            else
            {
                RunNetworkSetup($"-setsocksfirewallproxystate \"{networkService}\" off");
            }

            var httpEnabled = string.Equals(_previousHttpEnabled?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
            if (httpEnabled && !string.IsNullOrWhiteSpace(_previousHttpHost) && !string.IsNullOrWhiteSpace(_previousHttpPort))
            {
                RunNetworkSetup($"-setwebproxy \"{networkService}\" {_previousHttpHost} {_previousHttpPort}");
                RunNetworkSetup($"-setwebproxystate \"{networkService}\" on");
            }
            else
            {
                RunNetworkSetup($"-setwebproxystate \"{networkService}\" off");
            }

            var httpsEnabled = string.Equals(_previousHttpsEnabled?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
            if (httpsEnabled && !string.IsNullOrWhiteSpace(_previousHttpsHost) && !string.IsNullOrWhiteSpace(_previousHttpsPort))
            {
                RunNetworkSetup($"-setsecurewebproxy \"{networkService}\" {_previousHttpsHost} {_previousHttpsPort}");
                RunNetworkSetup($"-setsecurewebproxystate \"{networkService}\" on");
            }
            else
            {
                RunNetworkSetup($"-setsecurewebproxystate \"{networkService}\" off");
            }

            _applied = false;
            log("Proxy disabled (restored previous settings).");
        }
        catch (Exception ex)
        {
            _applied = false;
            log($"Failed to restore macOS proxy settings: {ex.Message}");
        }
    }

    private void SavePreviousProxy()
    {
        try
        {
            var networkService = GetActiveNetworkService();
            if (string.IsNullOrWhiteSpace(networkService))
            {
                return;
            }

            var socksInfo = PlatformHelper.RunCommand("networksetup", $"-getsocksfirewallproxy \"{networkService}\"");
            if (socksInfo != null)
            {
                _previousSocksEnabled = ExtractField(socksInfo, "Enabled");
                _previousSocksHost = ExtractField(socksInfo, "Server");
                _previousSocksPort = ExtractField(socksInfo, "Port");
            }

            var httpInfo = PlatformHelper.RunCommand("networksetup", $"-getwebproxy \"{networkService}\"");
            if (httpInfo != null)
            {
                _previousHttpEnabled = ExtractField(httpInfo, "Enabled");
                _previousHttpHost = ExtractField(httpInfo, "Server");
                _previousHttpPort = ExtractField(httpInfo, "Port");
            }

            var httpsInfo = PlatformHelper.RunCommand("networksetup", $"-getsecurewebproxy \"{networkService}\"");
            if (httpsInfo != null)
            {
                _previousHttpsEnabled = ExtractField(httpsInfo, "Enabled");
                _previousHttpsHost = ExtractField(httpsInfo, "Server");
                _previousHttpsPort = ExtractField(httpsInfo, "Port");
            }
        }
        catch
        {
        }
    }

    private static string? GetActiveNetworkService()
    {
        var routeOutput = PlatformHelper.RunCommand("route", "-n get default");
        if (string.IsNullOrWhiteSpace(routeOutput))
        {
            return GetFirstNetworkService();
        }

        foreach (var line in routeOutput.Split('\n'))
        {
            if (line.TrimStart().StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
            {
                var iface = line.Split(':')[1].Trim();
                var services = PlatformHelper.RunCommand("networksetup", "-listallhardwareports");
                if (services != null)
                {
                    var lines = services.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("Device:", StringComparison.OrdinalIgnoreCase) &&
                            lines[i].Split(':')[1].Trim().Equals(iface, StringComparison.Ordinal))
                        {
                            for (var j = i - 1; j >= 0; j--)
                            {
                                if (lines[j].TrimStart().StartsWith("Hardware Port:", StringComparison.OrdinalIgnoreCase))
                                {
                                    return lines[j].Split(':')[1].Trim();
                                }
                            }
                        }
                    }
                }

                break;
            }
        }

        return GetFirstNetworkService();
    }

    private static string? GetFirstNetworkService()
    {
        var output = PlatformHelper.RunCommand("networksetup", "-listallnetworkservices");
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) &&
                !trimmed.StartsWith("An asterisk", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string? ExtractField(string output, string fieldName)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith($"{fieldName}:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmed.IndexOf(':');
                return colonIndex >= 0 ? trimmed[(colonIndex + 1)..].Trim() : null;
            }
        }

        return null;
    }

    private static void RunNetworkSetup(string args) => PlatformHelper.RunCommand("networksetup", args);
}
