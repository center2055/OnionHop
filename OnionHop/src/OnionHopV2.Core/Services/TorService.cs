using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Services;

internal sealed class TorService : IDisposable
{
    private const string ControlPortFileName = "control_port.txt";
    private const string ControlAuthCookieFileName = "control_auth_cookie";
    private readonly Action<string> _log;
    private Process? _process;
    private string? _dataDirectory;
    private int? _controlPort;
    private bool _disposed;

    public TorService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public Task StartAsync(TorLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TorService));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(config.TorPath))
        {
            throw new ArgumentException("Tor path is required.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.GeoIpPath) || string.IsNullOrWhiteSpace(config.GeoIp6Path))
        {
            throw new ArgumentException("GeoIP paths are required.", nameof(config));
        }

        _dataDirectory = string.IsNullOrWhiteSpace(config.DataDirectory)
            ? Path.Combine(Path.GetTempPath(), "OnionHop", "tor-data")
            : config.DataDirectory;
        Directory.CreateDirectory(_dataDirectory);

        _controlPort = null;
        TryDeleteFile(Path.Combine(_dataDirectory, ControlPortFileName));
        TryDeleteFile(Path.Combine(_dataDirectory, ControlAuthCookieFileName));

        var args = BuildArguments(config, _dataDirectory);
        var psi = new ProcessStartInfo(config.TorPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory
                ?? Path.GetDirectoryName(config.TorPath)
                ?? AppContext.BaseDirectory
        };

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.Exited += HandleExited;
        _process.OutputDataReceived += HandleOutput;
        _process.ErrorDataReceived += HandleOutput;

        if (!_process.Start())
        {
            throw new InvalidOperationException("Unable to launch tor.exe");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    public async Task<bool> SendControlSignalAsync(string command, CancellationToken token)
    {
        var port = await GetControlPortAsync(token);
        if (!port.HasValue)
        {
            _log("Tor control port not available.");
            return false;
        }

        var cookie = await GetControlCookieHexAsync(token);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            _log("Tor control cookie not available.");
            return false;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port.Value, token);

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        await writer.WriteLineAsync($"AUTHENTICATE {cookie}");
        var authResponse = await ReadControlResponseAsync(reader);
        if (!authResponse.StartsWith("250", StringComparison.Ordinal))
        {
            _log($"Tor control auth failed: {authResponse}");
            return false;
        }

        await writer.WriteLineAsync(command);
        var response = await ReadControlResponseAsync(reader);
        if (!response.StartsWith("250", StringComparison.Ordinal))
        {
            _log($"Tor control command failed: {response}");
            return false;
        }

        await writer.WriteLineAsync("QUIT");
        return true;
    }

    public async Task<(long BytesRead, long BytesWritten)?> TryGetTrafficBytesAsync(CancellationToken token)
    {
        var port = await GetControlPortAsync(token);
        if (!port.HasValue)
        {
            return null;
        }

        var cookie = await GetControlCookieHexAsync(token);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port.Value, token);

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        await writer.WriteLineAsync($"AUTHENTICATE {cookie}");
        var authResponse = await ReadControlResponseAsync(reader);
        if (!authResponse.StartsWith("250", StringComparison.Ordinal))
        {
            return null;
        }

        await writer.WriteLineAsync("GETINFO traffic/read traffic/written");
        var response = await ReadControlResponseAsync(reader);

        long? read = null;
        long? written = null;

        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("250", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || eq + 1 >= line.Length)
            {
                continue;
            }

            var key = line.Substring(3, eq - 3).TrimStart('-', ' ');
            var value = line[(eq + 1)..].Trim();
            if (!long.TryParse(value, out var parsed))
            {
                continue;
            }

            if (string.Equals(key, "traffic/read", StringComparison.OrdinalIgnoreCase))
            {
                read = parsed;
            }
            else if (string.Equals(key, "traffic/written", StringComparison.OrdinalIgnoreCase))
            {
                written = parsed;
            }
        }

        await writer.WriteLineAsync("QUIT");

        if (!read.HasValue || !written.HasValue)
        {
            return null;
        }

        return (read.Value, written.Value);
    }

    public void Stop()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.CloseMainWindow();
                    _process.WaitForExit(1500);
                }
                catch
                {
                }

                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to stop Tor: {ex.Message}");
        }
        finally
        {
            _process.OutputDataReceived -= HandleOutput;
            _process.ErrorDataReceived -= HandleOutput;
            _process.Exited -= HandleExited;
            _process.Dispose();
            _process = null;
            _controlPort = null;
            _dataDirectory = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task<int?> GetControlPortAsync(CancellationToken token)
    {
        if (_controlPort.HasValue)
        {
            return _controlPort.Value;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return null;
        }

        var portFile = Path.Combine(_dataDirectory, ControlPortFileName);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (File.Exists(portFile))
            {
                var content = await File.ReadAllTextAsync(portFile, token);
                var parsed = ParsePortFromFile(content);
                if (parsed.HasValue)
                {
                    _controlPort = parsed.Value;
                    return _controlPort;
                }
            }

            await Task.Delay(200, token);
        }

        return null;
    }

    private async Task<string?> GetControlCookieHexAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return null;
        }

        var cookiePath = Path.Combine(_dataDirectory, ControlAuthCookieFileName);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (File.Exists(cookiePath))
            {
                var bytes = await File.ReadAllBytesAsync(cookiePath, token);
                if (bytes.Length > 0)
                {
                    return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                }
            }

            await Task.Delay(200, token);
        }

        return null;
    }

    private static async Task<string> ReadControlResponseAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            sb.AppendLine(line);

            // End marker is a 250 line with a space (not dash)
            if (line.StartsWith("250 ", StringComparison.Ordinal) || line.StartsWith("5", StringComparison.Ordinal))
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string BuildArguments(TorLaunchConfig config, string dataDirectory)
    {
        var sb = new StringBuilder();

        sb.Append($"--SocksPort {FormatPortEndpoint(config.SocksListenAddress, config.SocksPort, "127.0.0.1")} ");
        if (config.HttpTunnelPort.HasValue)
        {
            sb.Append($"--HTTPTunnelPort {FormatPortEndpoint(config.HttpTunnelListenAddress, config.HttpTunnelPort.Value, "127.0.0.1")} ");
        }
        if (config.DnsPort.HasValue)
        {
            sb.Append($"--DNSPort {FormatPortEndpoint(config.DnsListenAddress, config.DnsPort.Value, "127.0.0.1")} ");
        }
        sb.Append($"--DataDirectory \"{dataDirectory}\" ");
        sb.Append($"--GeoIPFile \"{config.GeoIpPath}\" ");
        sb.Append($"--GeoIPv6File \"{config.GeoIp6Path}\" ");
        sb.Append("--Log \"notice stdout\" ");

        // Control port for NEWNYM etc
        sb.Append("--ControlPort auto ");
        sb.Append($"--ControlPortWriteToFile \"{Path.Combine(dataDirectory, ControlPortFileName)}\" ");
        sb.Append("--CookieAuthentication 1 ");

        if (config.ClientUseIpv6.HasValue)
        {
            sb.Append($"--ClientUseIPv6 {(config.ClientUseIpv6.Value ? "1" : "0")} ");
        }

        if (config.HardwareAccel.HasValue)
        {
            sb.Append($"--HardwareAccel {(config.HardwareAccel.Value ? "1" : "0")} ");
        }

        if (!string.IsNullOrWhiteSpace(config.ConnectionPadding))
        {
            sb.Append($"--ConnectionPadding {config.ConnectionPadding} ");
        }

        if (config.AllowedPorts is { Count: > 0 })
        {
            var reachable = string.Join(",", config.AllowedPorts
                .Where(port => port is > 0 and <= 65535)
                .Distinct()
                .Select(port => $"*:{port}"));
            if (!string.IsNullOrWhiteSpace(reachable))
            {
                sb.Append($"--ReachableAddresses \"{reachable}\" ");
                sb.Append($"--ReachableDirAddresses \"{reachable}\" ");
                sb.Append($"--ReachableORAddresses \"{reachable}\" ");
            }
        }

        if (config.MaxCircuitDirtinessSeconds.HasValue && config.MaxCircuitDirtinessSeconds.Value > 0)
        {
            sb.Append($"--MaxCircuitDirtiness {config.MaxCircuitDirtinessSeconds.Value} ");
        }

        if (config.BridgeLines is { Count: > 0 })
        {
            sb.Append("--UseBridges 1 ");
            foreach (var plugin in config.ClientTransportPlugins ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(plugin))
                {
                    continue;
                }

                // Accept either:
                // - "ClientTransportPlugin snowflake exec C:\...\snowflake-client.exe"
                // - "snowflake exec C:\...\snowflake-client.exe"
                const string prefix = "ClientTransportPlugin ";
                var value = plugin.Trim();
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(prefix.Length).Trim();
                }

                sb.Append("--ClientTransportPlugin ");
                sb.Append($"\"{value}\" ");
            }

            foreach (var bridge in config.BridgeLines)
            {
                if (string.IsNullOrWhiteSpace(bridge))
                {
                    continue;
                }

                var value = bridge.Trim();
                const string bridgePrefix = "Bridge ";
                if (value.StartsWith(bridgePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(bridgePrefix.Length).Trim();
                }

                sb.Append("--Bridge ");
                sb.Append($"\"{value}\" ");
            }
        }

        var hasEntry = !string.IsNullOrWhiteSpace(config.EntryCountryCode);
        var hasExit = !string.IsNullOrWhiteSpace(config.ExitCountryCode);
        var hasExitFingerprint = !string.IsNullOrWhiteSpace(config.ExitNodeFingerprint);

        if (hasEntry)
        {
            sb.Append($"--EntryNodes \"{{{config.EntryCountryCode}}}\" ");
        }

        if (hasExitFingerprint)
        {
            sb.Append($"--ExitNodes \"{config.ExitNodeFingerprint}\" ");
        }
        else if (hasExit)
        {
            sb.Append($"--ExitNodes \"{{{config.ExitCountryCode}}}\" ");
        }

        var strictNodes = hasEntry || hasExit || (hasExitFingerprint && config.StrictManualExitNodeFingerprint);
        if (strictNodes)
        {
            sb.Append("--StrictNodes 1 ");
        }

        sb.Append("--ClientOnly 1 ");

        return sb.ToString().Trim();
    }

    private static string FormatPortEndpoint(string? listenAddress, int port, string defaultAddress)
    {
        var host = string.IsNullOrWhiteSpace(listenAddress)
            ? defaultAddress
            : listenAddress.Trim();

        if (IPAddress.TryParse(host, out var ipAddress) &&
            ipAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
            !host.StartsWith("[", StringComparison.Ordinal))
        {
            host = $"[{host}]";
        }

        return $"{host}:{port}";
    }

    private static int? ParsePortFromFile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var parts = content.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
        {
            return port;
        }

        return null;
    }

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        OutputReceived?.Invoke(sender, e);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class TorLaunchConfig
{
    public string TorPath { get; init; } = string.Empty;
    public int SocksPort { get; init; }
    public string? SocksListenAddress { get; init; }
    public int? HttpTunnelPort { get; init; }
    public string? HttpTunnelListenAddress { get; init; }
    public int? DnsPort { get; init; }
    public string? DnsListenAddress { get; init; }
    public string? DataDirectory { get; init; }
    public string GeoIpPath { get; init; } = string.Empty;
    public string GeoIp6Path { get; init; } = string.Empty;
    public IReadOnlyList<string>? BridgeLines { get; init; }
    public IReadOnlyList<string>? ClientTransportPlugins { get; init; }
    public IReadOnlyList<int>? AllowedPorts { get; init; }
    public int? MaxCircuitDirtinessSeconds { get; init; }
    public string? EntryCountryCode { get; init; }
    public string? ExitCountryCode { get; init; }
    public string? ExitNodeFingerprint { get; init; }
    public bool StrictManualExitNodeFingerprint { get; init; } = true;
    public bool? ClientUseIpv6 { get; init; }
    public bool? HardwareAccel { get; init; }
    public string? ConnectionPadding { get; init; }
    public string? WorkingDirectory { get; init; }
    public Action<Process>? ProcessStarted { get; init; }
}
