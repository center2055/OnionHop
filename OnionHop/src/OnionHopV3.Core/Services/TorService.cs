using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

internal sealed class TorService : IDisposable
{
    private const string ControlPortFileName = "control_port.txt";
    private const string ControlAuthCookieFileName = "control_auth_cookie";
    private const int RecentOutputCapacity = 20;
    private readonly Action<string> _log;
    private readonly Queue<string> _recentOutputLines = new();
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

    public string RecentOutput
    {
        get
        {
            lock (_recentOutputLines)
            {
                return string.Join(Environment.NewLine, _recentOutputLines);
            }
        }
    }

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
        _dataDirectory = EnsureWritableDataDirectory(_dataDirectory);

        _controlPort = null;
        TryDeleteFile(Path.Combine(_dataDirectory, ControlPortFileName));
        TryDeleteFile(Path.Combine(_dataDirectory, ControlAuthCookieFileName));

        // Stop() only kills the process THIS instance tracked. A previous session can leave an
        // orphaned tor.exe behind (crash, force-close, a failed connect whose cleanup didn't reach
        // Tor) that keeps holding the data directory lock and the SOCKS/DNS ports - so the new Tor
        // exits with "another Tor process is running with the same data directory." Kill any orphaned
        // copy of OUR tor binary (scoped by path, so a separate Tor Browser install is untouched) and
        // clear the stale lock before launching.
        KillStaleOnionHopTor(config.TorPath);
        // Pluggable transports (lyrebird/snowflake/webtunnel/conjure/dnstt) are launched by Tor as
        // managed proxies. If a previous Tor was force-killed its transport can be orphaned and keep
        // holding its SOCKS/state, so the new Tor's managed proxy dies on launch ("Managed proxy ...
        // terminated with status code 2"), killing obfs4/snowflake bridges. Clear those too.
        KillStaleOnionHopPluggableTransports(config.TorPath);
        TryDeleteFile(Path.Combine(_dataDirectory, "lock"));

        var arguments = BuildArguments(config, _dataDirectory);
        _log($"Tor arguments: {FormatArgumentsForLog(arguments)}");
        var torDirectory = config.WorkingDirectory
            ?? Path.GetDirectoryName(config.TorPath)
            ?? AppContext.BaseDirectory;
        var psi = new ProcessStartInfo(config.TorPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = torDirectory
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // On Linux the Tor expert bundle ships its own libssl/libcrypto/libevent next to the tor
        // binary; point the loader at that directory so tor uses the matching libraries instead of
        // an incompatible system libevent (which surfaces as e.g. "undefined symbol:
        // evutil_secure_rng_add_bytes"). Prepend so the bundled libs win, then the system path.
        if (OperatingSystem.IsLinux())
        {
            var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing)
                ? torDirectory
                : $"{torDirectory}:{existing}";
        }

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
            throw new InvalidOperationException("Unable to launch Tor.");
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

    /// <summary>
    /// Kills any orphaned copy of OUR tor binary left running from a previous session (matched by
    /// executable path, so a separate Tor Browser install is never affected). Such an orphan keeps
    /// holding the data directory lock and the SOCKS/DNS ports, which makes a fresh Tor refuse to
    /// start with "another Tor process is running with the same data directory." Best effort.
    /// </summary>
    private void KillStaleOnionHopTor(string torPath)
    {
        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(torPath);
        }
        catch
        {
            return;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(torPath));
        }
        catch
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                // Only touch the orphan if it's the same executable we're about to launch.
                string? exePath = null;
                try { exePath = process.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exePath) ||
                    !string.Equals(Path.GetFullPath(exePath!), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
                _log($"Released Tor data directory: stopped orphaned {Path.GetFileName(torPath)} (pid {process.Id}).");
            }
            catch
            {
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Kills any orphaned pluggable-transport helper (lyrebird, snowflake-client, webtunnel-client,
    /// conjure-client, dnstt-client) left running from a previous session, matched strictly by the
    /// binary living under OUR tor\pluggable_transports directory - so an unrelated process that
    /// happens to share a name (or the Snowflake volunteer proxy, which lives elsewhere) is never
    /// touched. An orphan keeps holding its managed-proxy SOCKS/state, which makes the new Tor's
    /// managed proxy exit immediately with "terminated with status code 2". Best effort.
    /// </summary>
    private void KillStaleOnionHopPluggableTransports(string torPath)
    {
        string ptDirectory;
        try
        {
            var torDirectory = Path.GetDirectoryName(Path.GetFullPath(torPath));
            if (string.IsNullOrEmpty(torDirectory))
            {
                return;
            }

            ptDirectory = Path.GetFullPath(Path.Combine(torDirectory, "pluggable_transports"));
        }
        catch
        {
            return;
        }

        foreach (var transportName in PluggableTransportProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(transportName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    string? exePath = null;
                    try { exePath = process.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(exePath))
                    {
                        continue;
                    }

                    // Only ours: the binary must sit directly inside our pluggable_transports folder.
                    var exeDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath!));
                    if (!string.Equals(exeDirectory, ptDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                    _log($"Stopped orphaned pluggable transport {Path.GetFileName(exePath)} (pid {process.Id}).");
                }
                catch
                {
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
    }

    // Process names (no extension) of the managed proxies we ship under tor\pluggable_transports.
    private static readonly string[] PluggableTransportProcessNames =
    {
        "lyrebird", "snowflake-client", "webtunnel-client", "conjure-client", "dnstt-client"
    };

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

    private static IReadOnlyList<string> BuildArguments(TorLaunchConfig config, string dataDirectory)
    {
        var arguments = new List<string>();

        AddArgument(arguments, "--SocksPort", FormatPortEndpoint(config.SocksListenAddress, config.SocksPort, "127.0.0.1"));
        if (config.HttpTunnelPort.HasValue)
        {
            AddArgument(arguments, "--HTTPTunnelPort", FormatPortEndpoint(config.HttpTunnelListenAddress, config.HttpTunnelPort.Value, "127.0.0.1"));
        }
        if (config.DnsPort.HasValue)
        {
            AddArgument(arguments, "--DNSPort", FormatPortEndpoint(config.DnsListenAddress, config.DnsPort.Value, "127.0.0.1"));
        }
        AddArgument(arguments, "--DataDirectory", dataDirectory);
        AddArgument(arguments, "--GeoIPFile", config.GeoIpPath);
        AddArgument(arguments, "--GeoIPv6File", config.GeoIp6Path);
        AddArgument(arguments, "--Log", "notice stdout");

        // Upstream proxy: make Tor dial all its outbound connections (including to bridges) through an
        // external SOCKS5/HTTPS proxy. Lets OnionHop run behind another proxy instead of fighting it in
        // the network stack.
        foreach (var token in BuildUpstreamProxyArguments(
                     config.UpstreamProxyKind,
                     config.UpstreamProxyHost,
                     config.UpstreamProxyPort,
                     config.UpstreamProxyUsername,
                     config.UpstreamProxyPassword))
        {
            arguments.Add(token);
        }

        // Control port for NEWNYM etc
        AddArgument(arguments, "--ControlPort", "auto");
        AddArgument(arguments, "--ControlPortWriteToFile", Path.Combine(dataDirectory, ControlPortFileName));
        AddArgument(arguments, "--CookieAuthentication", "1");

        if (config.ClientUseIpv6.HasValue)
        {
            AddArgument(arguments, "--ClientUseIPv6", config.ClientUseIpv6.Value ? "1" : "0");
        }

        if (config.HardwareAccel.HasValue)
        {
            AddArgument(arguments, "--HardwareAccel", config.HardwareAccel.Value ? "1" : "0");
        }

        if (!string.IsNullOrWhiteSpace(config.ConnectionPadding))
        {
            AddArgument(arguments, "--ConnectionPadding", config.ConnectionPadding);
        }

        if (config.AllowedPorts is { Count: > 0 })
        {
            var reachable = string.Join(",", config.AllowedPorts
                .Where(port => port is > 0 and <= 65535)
                .Distinct()
                .Select(port => $"*:{port}"));
            if (!string.IsNullOrWhiteSpace(reachable))
            {
                AddArgument(arguments, "--ReachableAddresses", reachable);
                AddArgument(arguments, "--ReachableDirAddresses", reachable);
                AddArgument(arguments, "--ReachableORAddresses", reachable);
            }
        }

        if (config.MaxCircuitDirtinessSeconds.HasValue && config.MaxCircuitDirtinessSeconds.Value > 0)
        {
            AddArgument(arguments, "--MaxCircuitDirtiness", config.MaxCircuitDirtinessSeconds.Value.ToString());
        }

        if (config.BridgeLines is { Count: > 0 })
        {
            AddArgument(arguments, "--UseBridges", "1");
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

                AddArgument(arguments, "--ClientTransportPlugin", value);
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

                AddArgument(arguments, "--Bridge", value);
            }
        }

        var hasEntry = !string.IsNullOrWhiteSpace(config.EntryCountryCode);
        var hasEntryFingerprint = !string.IsNullOrWhiteSpace(config.EntryNodeFingerprint);
        var hasMiddleFingerprint = !string.IsNullOrWhiteSpace(config.MiddleNodeFingerprint);
        var hasExit = !string.IsNullOrWhiteSpace(config.ExitCountryCode);
        var hasExitFingerprint = !string.IsNullOrWhiteSpace(config.ExitNodeFingerprint);

        if (hasEntryFingerprint)
        {
            AddArgument(arguments, "--EntryNodes", FormatRelayFingerprint(config.EntryNodeFingerprint!));
        }
        else if (hasEntry)
        {
            AddArgument(arguments, "--EntryNodes", $"{{{config.EntryCountryCode}}}");
        }

        if (hasMiddleFingerprint)
        {
            AddArgument(arguments, "--MiddleNodes", FormatRelayFingerprint(config.MiddleNodeFingerprint!));
        }

        if (hasExitFingerprint)
        {
            AddArgument(arguments, "--ExitNodes", FormatRelayFingerprint(config.ExitNodeFingerprint!));
        }
        else if (hasExit)
        {
            AddArgument(arguments, "--ExitNodes", $"{{{config.ExitCountryCode}}}");
        }

        var strictNodes = hasEntry || hasExit ||
                          (hasEntryFingerprint && config.StrictManualEntryNodeFingerprint) ||
                          (hasMiddleFingerprint && config.StrictManualMiddleNodeFingerprint) ||
                          (hasExitFingerprint && config.StrictManualExitNodeFingerprint);
        if (strictNodes)
        {
            AddArgument(arguments, "--StrictNodes", "1");
        }

        AddArgument(arguments, "--ClientOnly", "1");

        return arguments;
    }

    private static void AddArgument(ICollection<string> arguments, string name, string value)
    {
        arguments.Add(name);
        arguments.Add(value);
    }

    /// <summary>
    /// Builds the Tor command-line tokens that route Tor's outbound traffic through an upstream proxy.
    /// SOCKS5 maps to --Socks5Proxy (+ optional username/password); "https"/"http" maps to --HTTPSProxy
    /// (an HTTP CONNECT proxy, + optional --HTTPSProxyAuthenticator). Returns an empty list when no host
    /// or a valid port is given, so the caller can always splice the result in unconditionally.
    /// </summary>
    internal static IReadOnlyList<string> BuildUpstreamProxyArguments(
        string? kind, string? host, int? port, string? username, string? password)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(host) || port is not (> 0 and <= 65535))
        {
            return tokens;
        }

        var endpoint = $"{host!.Trim()}:{port!.Value}";
        var normalized = (kind ?? OnionHopConnectOptions.UpstreamProxyKindSocks5).Trim().ToLowerInvariant();

        if (normalized is "https" or "http")
        {
            tokens.Add("--HTTPSProxy");
            tokens.Add(endpoint);
            if (!string.IsNullOrEmpty(username))
            {
                tokens.Add("--HTTPSProxyAuthenticator");
                tokens.Add($"{username}:{password ?? string.Empty}");
            }
        }
        else
        {
            tokens.Add("--Socks5Proxy");
            tokens.Add(endpoint);
            if (!string.IsNullOrEmpty(username))
            {
                tokens.Add("--Socks5ProxyUsername");
                tokens.Add(username!);
                tokens.Add("--Socks5ProxyPassword");
                tokens.Add(password ?? string.Empty);
            }
        }

        return tokens;
    }

    private static string FormatRelayFingerprint(string fingerprint)
    {
        var normalized = fingerprint.Trim().TrimStart('$');
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"${normalized}";
    }

    // Flags whose following value is a secret and must never be written to the (exportable) log.
    private static readonly HashSet<string> RedactNextValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--Socks5ProxyPassword",
        "--Socks5ProxyUsername",
        "--HTTPSProxyAuthenticator",
    };

    // internal for testing. Bridge lines are anti-censorship credentials and the upstream-proxy
    // password is a secret; neither may appear in a log the user can export and share for support.
    internal static string FormatArgumentsForLog(IReadOnlyList<string> arguments)
    {
        var rendered = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (i > 0 && RedactNextValueFlags.Contains(arguments[i - 1]))
            {
                rendered.Add("***");
                continue;
            }

            if (i > 0 && string.Equals(arguments[i - 1], "--Bridge", StringComparison.OrdinalIgnoreCase))
            {
                rendered.Add(RedactBridgeLine(arg));
                continue;
            }

            rendered.Add(QuoteForLog(arg));
        }

        return string.Join(" ", rendered);
    }

    // Keep just the transport + endpoint for log correlation; redact the fingerprint, cert= and any
    // other parameters (the obfs4 cert / webtunnel url / snowflake broker are the real secrets).
    internal static string RedactBridgeLine(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return QuoteForLog(bridgeLine);
        }

        var parts = bridgeLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Length switch
        {
            >= 3 => $"{parts[0]} {parts[1]} ***",
            2 => $"{parts[0]} {parts[1]}",
            _ => $"{parts[0]} ***"
        };
        return QuoteForLog(kept);
    }

    private static string QuoteForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
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
        if (!string.IsNullOrEmpty(e.Data))
        {
            lock (_recentOutputLines)
            {
                if (_recentOutputLines.Count >= RecentOutputCapacity)
                {
                    _recentOutputLines.Dequeue();
                }
                _recentOutputLines.Enqueue(e.Data);
            }
        }
        OutputReceived?.Invoke(sender, e);
    }

    private string EnsureWritableDataDirectory(string path)
    {
        // On macOS/Linux, Tor strictly requires the data directory to be owned by the
        // current user. When switching between proxy mode (normal user) and TUN/VPN mode
        // (root), ownership mismatches in both directions.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            path = EnsureWritableDataDirectoryUnix(path);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    private string EnsureWritableDataDirectoryUnix(string path)
    {
        var uid = geteuid();
        var gid = getegid();

        // Try the requested path first, then a temp-based fallback.
        var candidates = new[]
        {
            path,
            Path.Combine(Path.GetTempPath(), "OnionHop", $"tor-data-{uid}")
        };

        foreach (var candidate in candidates)
        {
            if (TryPrepareDataDirectory(candidate, uid, gid))
            {
                return candidate;
            }
        }

        // Last resort: fresh unique temp directory (always writable by current user).
        var fallback = Path.Combine(Path.GetTempPath(), $"OnionHop-tor-{uid}-{Environment.ProcessId}");
        _log($"All data directory candidates failed. Using unique fallback: {fallback}");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private bool TryPrepareDataDirectory(string path, uint uid, uint gid)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _log($"Cannot create directory '{path}': {ex.Message}");
            return false;
        }

        // Test actual write access — Directory.CreateDirectory succeeds on existing dirs
        // even without write permission.
        var testFile = Path.Combine(path, ".write-test");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch
        {
            _log($"Directory '{path}' is not writable by current user (uid={uid}). Attempting fix...");

            // Try to chown the directory.
            try
            {
                RecursiveChown(path, uid, gid);
            }
            catch
            {
                // chown failed — try deleting and recreating.
                _log($"chown failed on '{path}'. Attempting delete and recreate...");
                try
                {
                    Directory.Delete(path, recursive: true);
                    Directory.CreateDirectory(path);
                }
                catch (Exception delEx)
                {
                    _log($"Cannot reclaim '{path}': {delEx.Message}");
                    return false;
                }
            }

            // Verify write access after fix attempt.
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                _log($"Directory '{path}' still not writable after fix attempt.");
                return false;
            }
        }

        // Ensure ownership is correct (Tor checks this).
        try
        {
            RecursiveChown(path, uid, gid);
        }
        catch
        {
            // If we can write but can't chown, it's likely already owned by us.
        }

        return true;
    }

    private static void RecursiveChown(string path, uint uid, uint gid)
    {
        if (chown(path, uid, gid) != 0)
            throw new IOException($"chown failed on '{path}'");

        foreach (var dir in Directory.GetDirectories(path))
            RecursiveChown(dir, uid, gid);

        foreach (var file in Directory.GetFiles(path))
        {
            if (chown(file, uid, gid) != 0)
                throw new IOException($"chown failed on '{file}'");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string pathname, uint owner, uint group);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();

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
    public string? EntryNodeFingerprint { get; init; }
    public string? MiddleNodeFingerprint { get; init; }
    public string? ExitCountryCode { get; init; }
    public string? ExitNodeFingerprint { get; init; }
    public bool StrictManualEntryNodeFingerprint { get; init; } = true;
    public bool StrictManualMiddleNodeFingerprint { get; init; } = true;
    public bool StrictManualExitNodeFingerprint { get; init; } = true;
    public bool? ClientUseIpv6 { get; init; }
    public bool? HardwareAccel { get; init; }
    public string? ConnectionPadding { get; init; }
    public string? UpstreamProxyKind { get; init; }
    public string? UpstreamProxyHost { get; init; }
    public int? UpstreamProxyPort { get; init; }
    public string? UpstreamProxyUsername { get; init; }
    public string? UpstreamProxyPassword { get; init; }
    public string? WorkingDirectory { get; init; }
    public Action<Process>? ProcessStarted { get; init; }
}
