using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Launches the ArtiHop binary (github.com/center2055/ArtiHop) — a standalone Arti-based SOCKS5
/// proxy that supports shortened 2-hop (Guard -> Exit) circuits via "--mode short-2". Unlike the
/// upstream <see cref="ArtiService"/> (which speaks arti's own `proxy -c file.toml` CLI), ArtiHop
/// takes flags: `artihop --mode short-2 --socks 127.0.0.1:PORT --log FILTER`.
/// </summary>
internal sealed class ArtiHopService : IDisposable
{
    private const int RecentOutputCapacity = 24;
    private readonly Action<string> _log;
    private readonly Queue<string> _recentOutputLines = new();
    private Process? _process;
    private IPEndPoint? _controlEndpoint;
    private bool _disposed;

    public ArtiHopService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>True when this session's ArtiHop was started with a control listener (New Identity).</summary>
    public bool HasControlEndpoint => _controlEndpoint != null && IsRunning;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ControlSupportCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ArtiHop 0.2.0 added a loopback control listener (--control, accepting NEWNYM). Older builds
    /// reject the flag and exit before opening their SOCKS port, so it is only passed to builds that
    /// report 0.2.0 or newer. Cached per binary and modification time; a failed probe means no.
    /// </summary>
    internal static bool SupportsControlListener(string artiHopPath)
    {
        string key;
        try
        {
            key = $"{Path.GetFullPath(artiHopPath)}|{File.GetLastWriteTimeUtc(artiHopPath).Ticks}";
        }
        catch
        {
            return false;
        }

        return ControlSupportCache.GetOrAdd(key, _ =>
        {
            try
            {
                var psi = new ProcessStartInfo(artiHopPath, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null)
                {
                    return false;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                return ParseArtiHopVersion(output.Result) is { } version && version >= new Version(0, 2, 0);
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>"artihop 0.2.0" (clap's --version output) to 0.2.0; null for anything else.</summary>
    internal static Version? ParseArtiHopVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "artihop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Drop any pre-release or build suffix ("0.3.0-dev") before parsing.
        var numeric = parts[1].Split('-', '+')[0];
        return Version.TryParse(numeric, out var version) ? version : null;
    }
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

    public async Task StartAsync(ArtiHopLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ArtiHopService));
        }

        Stop();
        // Start each launch with a clean diagnostic buffer so a failure only reports THIS attempt's
        // output (otherwise lines from prior retries pile up and bloat the error).
        lock (_recentOutputLines)
        {
            _recentOutputLines.Clear();
        }
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(config.ArtiHopPath))
        {
            throw new ArgumentException("ArtiHop path is required.", nameof(config));
        }

        // A crash or force-close can leave an orphaned artihop holding Arti's shared state-dir lock;
        // the next launch then dies with "Configuration requires exclusive access to shared state, but
        // another instance of Arti has the lock". Stop() only clears OUR tracked process, so kill any
        // orphan of the same binary (and its PT children) before starting.
        KillStaleArtiHopProcesses(config.ArtiHopPath);

        var endpoint = FormatPortEndpoint(config.SocksListenAddress, config.SocksPort, "127.0.0.1");
        var mode = string.IsNullOrWhiteSpace(config.Mode) ? "short-2" : config.Mode.Trim();
        var logFilter = string.IsNullOrWhiteSpace(config.LogFilter)
            // arti_client at info surfaces bootstrap progress, which is what we need to diagnose a
            // "SOCKS port did not become ready" failure (stuck at directory fetch vs. circuit build).
            ? "artihop=info,arti_client=info,tor_proto=warn,tor_circmgr=info"
            : config.LogFilter.Trim();

        var arguments = new List<string> { "--mode", mode, "--socks", endpoint, "--log", logFilter };
        // Only builds that support it get --control (see SupportsControlListener): older ones reject
        // the unknown argument and exit before opening their SOCKS port.
        _controlEndpoint = null;
        if (config.ControlPort is int controlPort)
        {
            arguments.Add("--control");
            arguments.Add($"127.0.0.1:{controlPort}");
            _controlEndpoint = new IPEndPoint(IPAddress.Loopback, controlPort);
        }

        if (!string.IsNullOrWhiteSpace(config.BridgesConfigPath))
        {
            arguments.Add("--bridges-config");
            arguments.Add(config.BridgesConfigPath);
        }

        _log($"ArtiHop arguments: {FormatArgumentsForLog(arguments)}");

        var psi = new ProcessStartInfo(config.ArtiHopPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory
                ?? Path.GetDirectoryName(config.ArtiHopPath)
                ?? AppContext.BaseDirectory
        };

        // ArtiHop logs via tracing-subscriber, which emits ANSI color codes by default. Disable them
        // so the in-app log shows clean text instead of escape sequences.
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
            throw new InvalidOperationException("Unable to launch ArtiHop.");
        }

        config.ProcessStarted?.Invoke(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var readinessTimeout = TimeSpan.FromSeconds(45);
        if (!await WaitForSocksPortReadyAsync(
                config.SocksPort,
                token,
                readinessTimeout,
                () => _process?.HasExited == true).ConfigureAwait(false))
        {
            // Keep the user-facing exception short; the full recent output goes to the log so the
            // Home screen shows a one-line reason instead of a wall of engine text.
            var details = RecentOutput;
            if (!string.IsNullOrWhiteSpace(details))
            {
                _log($"ArtiHop did not open its SOCKS port in time. Recent output:{Environment.NewLine}{details}");
            }

            var exitedEarly = _process?.HasExited == true;
            Stop();
            throw new InvalidOperationException(exitedEarly
                ? $"ArtiHop exited before its SOCKS port {config.SocksPort} became ready. See the Logs tab for details."
                : $"ArtiHop started but its SOCKS port {config.SocksPort} was not ready within {(int)readinessTimeout.TotalSeconds}s. See the Logs tab for details.");
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
            _log($"Failed to stop ArtiHop: {ex.Message}");
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

    /// <summary>
    /// Ask the running ArtiHop to rotate to a new identity (fresh isolated circuits) via its control
    /// listener. Returns false if no control endpoint is available or the request fails.
    /// </summary>
    public async Task<bool> SendNewIdentityAsync(CancellationToken token)
    {
        var endpoint = _controlEndpoint;
        if (endpoint == null || !IsRunning)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            var payload = System.Text.Encoding.ASCII.GetBytes("NEWNYM\n");
            await stream.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            // ArtiHop answers "OK" once later streams will use fresh circuits.
            var buffer = new byte[32];
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var reply = System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim();
            if (!reply.StartsWith("OK", StringComparison.Ordinal))
            {
                _log($"ArtiHop refused the new-identity request: {reply}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"ArtiHop new-identity request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Kills any orphaned copy of OUR artihop binary (matched by executable path) left running from a
    /// crashed/force-closed session. An orphan keeps Arti's shared state-dir lock, so a fresh launch
    /// fails with "another instance of Arti has the lock". Tree-kill also takes down its PT children
    /// (webtunnel-client, snowflake-client, ...). Best effort - never throws.
    /// </summary>
    private void KillStaleArtiHopProcesses(string artiHopPath)
    {
        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(artiHopPath);
        }
        catch
        {
            return;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(artiHopPath));
        }
        catch
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                // Only our binary - never touch an unrelated process that happens to share the name.
                string? exePath = null;
                try { exePath = process.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exePath) ||
                    !string.Equals(Path.GetFullPath(exePath!), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
                _log($"Released Arti state lock: stopped orphaned {Path.GetFileName(artiHopPath)} (pid {process.Id}).");
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

internal sealed class ArtiHopLaunchConfig
{
    public string ArtiHopPath { get; init; } = string.Empty;
    public int SocksPort { get; init; }
    public string? SocksListenAddress { get; init; }
    public int? ControlPort { get; init; }
    public string? Mode { get; init; }
    public string? LogFilter { get; init; }
    public string? WorkingDirectory { get; init; }
    public Action<Process>? ProcessStarted { get; init; }

    /// <summary>Path to an Arti-format TOML file with a [bridges] section. When set, ArtiHop is launched
    /// with --bridges-config so it connects through those bridges + pluggable transports.</summary>
    public string? BridgesConfigPath { get; init; }
}
