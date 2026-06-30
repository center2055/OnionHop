using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

internal sealed class ArtiService : IDisposable
{
    private const int RecentOutputCapacity = 24;
    private readonly Action<string> _log;
    private readonly Queue<string> _recentOutputLines = new();
    private Process? _process;
    private bool _disposed;

    public ArtiService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event EventHandler<string>? OutputReceived;
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

    public async Task StartAsync(ArtiLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ArtiService));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(config.ArtiPath))
        {
            throw new ArgumentException("Arti path is required.", nameof(config));
        }

        // An orphaned arti from a crash/force-close keeps Arti's shared state-dir lock, so a fresh
        // launch dies with "another instance of Arti has the lock". Stop() only clears OUR tracked
        // process, so kill any orphan of the same binary (and its PT children) first.
        KillStaleArtiProcesses(config.ArtiPath);

        var dataDirectory = string.IsNullOrWhiteSpace(config.DataDirectory)
            ? Path.Combine(Path.GetTempPath(), "OnionHop", "arti-data")
            : config.DataDirectory;
        Directory.CreateDirectory(dataDirectory);

        var configPath = Path.Combine(dataDirectory, "onionhop-arti.toml");
        await File.WriteAllTextAsync(configPath, BuildConfigText(config, dataDirectory), Encoding.UTF8, token)
            .ConfigureAwait(false);

        var arguments = new[] { "proxy", "-c", configPath };
        _log($"Arti arguments: {FormatArgumentsForLog(arguments)}");

        var psi = new ProcessStartInfo(config.ArtiPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory
                ?? Path.GetDirectoryName(config.ArtiPath)
                ?? AppContext.BaseDirectory
        };

        // Suppress ANSI color codes in Arti's log output so the in-app log stays clean.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CLICOLOR"] = "0";

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
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
            throw new InvalidOperationException("Unable to launch Arti.");
        }

        config.ProcessStarted?.Invoke(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        if (!await WaitForSocksPortReadyAsync(
                config.SocksPort,
                token,
                TimeSpan.FromSeconds(45),
                () => _process?.HasExited == true).ConfigureAwait(false))
        {
            var details = RecentOutput;
            Stop();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"Arti started but SOCKS port {config.SocksPort} did not become ready."
                : $"Arti started but SOCKS port {config.SocksPort} did not become ready. Recent output: {details}");
        }
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
                    _process.WaitForExit(1200);
                }
                catch
                {
                }

                if (!_process.HasExited)
                {
                    _process.Kill(true);
                    _process.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to stop Arti: {ex.Message}");
        }
        finally
        {
            _process.OutputDataReceived -= HandleOutput;
            _process.ErrorDataReceived -= HandleOutput;
            _process.Exited -= HandleExited;
            _process.Dispose();
            _process = null;
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

    internal static string BuildConfigText(ArtiLaunchConfig config, string dataDirectory)
    {
        var endpoint = FormatPortEndpoint(config.SocksListenAddress, config.SocksPort, "127.0.0.1");
        var stateDir = Path.Combine(dataDirectory, "state");
        var cacheDir = Path.Combine(dataDirectory, "cache");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(cacheDir);

        var baseConfig = $"""
            [proxy]
            socks_listen = "{EscapeTomlString(endpoint)}"
            dns_listen = 0

            [storage]
            state_dir = "{EscapeTomlString(stateDir)}"
            cache_dir = "{EscapeTomlString(cacheDir)}"

            [logging]
            console = "info"
            """;

        return baseConfig + BuildBridgesSection(config);
    }

    // Emit Arti's native [bridges] + [[bridges.transports]] config so the upstream Arti engine connects
    // through OnionHop's bridges + pluggable transports (Arti has supported this since 1.1.0). Unlike
    // Tor's whitespace-split ClientTransportPlugin args, TOML strings handle paths with spaces fine.
    internal static string BuildBridgesSection(ArtiLaunchConfig config)
    {
        var bridges = config.BridgeLines;
        if (bridges == null || bridges.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("\n\n[bridges]\nenabled = true\nbridges = [\n");
        foreach (var line in bridges)
        {
            var spec = line.Trim();
            if (spec.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
            {
                spec = spec[7..].Trim();
            }
            if (spec.Length == 0)
            {
                continue;
            }
            sb.Append($"    \"{EscapeTomlString(spec)}\",\n");
        }
        sb.Append("]\n");

        // One [[bridges.transports]] per distinct transport, pointing at its PT binary.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in config.TransportPlugins ?? Array.Empty<string>())
        {
            var parsed = ParseTransportPlugin(plugin);
            if (parsed == null)
            {
                continue;
            }

            var (transport, path, arguments) = parsed.Value;
            if (!seen.Add(transport))
            {
                continue;
            }
            // A single Tor PT line can register several methods ("obfs4,meek_lite"); Arti's
            // `protocols` is a list of individual transport names, one quoted entry each.
            var protocols = string.Join(", ", transport
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => $"\"{EscapeTomlString(p)}\""));
            if (protocols.Length == 0)
            {
                continue;
            }
            sb.Append($"\n[[bridges.transports]]\nprotocols = [{protocols}]\npath = \"{EscapeTomlString(path)}\"\n");
            if (arguments.Count > 0)
            {
                var args = string.Join(", ", arguments.Select(argument => $"\"{EscapeTomlString(argument)}\""));
                sb.Append($"arguments = [{args}]\n");
            }
            sb.Append("run_on_startup = false\n");
        }

        return sb.ToString();
    }

    // Parse a Tor-format "transport exec C:\path\to\client.exe [args]" line into Arti fields.
    internal static (string Transport, string Path, IReadOnlyList<string> Arguments)? ParseTransportPlugin(string? plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin))
        {
            return null;
        }

        var execIdx = plugin.IndexOf(" exec ", StringComparison.OrdinalIgnoreCase);
        if (execIdx <= 0)
        {
            return null;
        }

        var transport = plugin[..execIdx].Trim();
        // Tor lines are "ClientTransportPlugin <methods> exec <path>"; drop the leading keyword so the
        // transport is just the method name(s) (e.g. "webtunnel"). Without this, Arti rejects the
        // config: "ClientTransportPlugin webtunnel" is not a valid pluggable transport ID.
        if (transport.StartsWith("ClientTransportPlugin", StringComparison.OrdinalIgnoreCase))
        {
            transport = transport["ClientTransportPlugin".Length..].Trim();
        }
        var rest = plugin[(execIdx + 6)..].Trim();
        if (transport.Length == 0 || rest.Length == 0)
        {
            return null;
        }

        string path;
        IReadOnlyList<string> arguments;
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            path = end > 1 ? rest[1..end] : rest.Trim('"');
            arguments = SplitCommandLineArguments(end > 1 ? rest[(end + 1)..] : string.Empty);
        }
        else
        {
            var parts = SplitCommandLineArguments(rest);
            var firstArgument = parts.FindIndex(argument => argument.StartsWith("-", StringComparison.Ordinal));
            if (firstArgument > 0)
            {
                path = string.Join(' ', parts.Take(firstArgument));
                arguments = parts.Skip(firstArgument).ToList();
            }
            else
            {
                path = rest;
                arguments = Array.Empty<string>();
            }
        }

        return path.Length == 0 ? null : (transport, path, arguments);
    }

    private static List<string> SplitCommandLineArguments(string value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in value.Trim())
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static async Task<bool> WaitForSocksPortReadyAsync(
        int port,
        CancellationToken token,
        TimeSpan maxWait,
        Func<bool>? hasExited = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            token.ThrowIfCancellationRequested();
            if (hasExited?.Invoke() == true)
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Kills any orphaned copy of OUR arti binary (matched by executable path) left from a crashed or
    /// force-closed session. An orphan keeps Arti's shared state-dir lock, so a fresh launch fails with
    /// "another instance of Arti has the lock". Tree-kill also stops its PT children. Best effort.
    /// </summary>
    private void KillStaleArtiProcesses(string artiPath)
    {
        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(artiPath);
        }
        catch
        {
            return;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(artiPath));
        }
        catch
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                string? exePath = null;
                try { exePath = process.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exePath) ||
                    !string.Equals(Path.GetFullPath(exePath!), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
                _log($"Released Arti state lock: stopped orphaned {Path.GetFileName(artiPath)} (pid {process.Id}).");
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

    internal static string EscapeTomlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string FormatArgumentsForLog(IReadOnlyList<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteForLog));
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

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        lock (_recentOutputLines)
        {
            if (_recentOutputLines.Count >= RecentOutputCapacity)
            {
                _recentOutputLines.Dequeue();
            }

            _recentOutputLines.Enqueue(e.Data);
        }

        OutputReceived?.Invoke(sender, e.Data);
    }
}

internal sealed class ArtiLaunchConfig
{
    public string ArtiPath { get; init; } = string.Empty;
    public int SocksPort { get; init; }
    public string? SocksListenAddress { get; init; }
    public string? DataDirectory { get; init; }
    public string? WorkingDirectory { get; init; }
    public Action<Process>? ProcessStarted { get; init; }

    /// <summary>Bridge spec lines (e.g. "obfs4 1.2.3.4:443 FINGERPRINT cert=... iat-mode=0"), without
    /// the leading "Bridge ". When non-empty, Arti is configured to connect through these bridges.</summary>
    public IReadOnlyList<string>? BridgeLines { get; init; }

    /// <summary>Tor-format ClientTransportPlugin lines ("transport exec path"). Each becomes an Arti
    /// [[bridges.transports]] entry so Arti launches the pluggable transport for its bridges.</summary>
    public IReadOnlyList<string>? TransportPlugins { get; init; }
}
