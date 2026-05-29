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
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(config.ArtiHopPath))
        {
            throw new ArgumentException("ArtiHop path is required.", nameof(config));
        }

        var endpoint = FormatPortEndpoint(config.SocksListenAddress, config.SocksPort, "127.0.0.1");
        var mode = string.IsNullOrWhiteSpace(config.Mode) ? "short-2" : config.Mode.Trim();
        var logFilter = string.IsNullOrWhiteSpace(config.LogFilter)
            ? "artihop=info,arti_client=warn,tor_proto=warn,tor_circmgr=info"
            : config.LogFilter.Trim();

        var arguments = new List<string> { "--mode", mode, "--socks", endpoint, "--log", logFilter };
        _controlEndpoint = null;
        if (config.ControlPort is > 0 and <= 65535)
        {
            // Control listener stays on loopback even when SOCKS is exposed to the LAN.
            var controlEndpoint = $"127.0.0.1:{config.ControlPort.Value}";
            arguments.Add("--control");
            arguments.Add(controlEndpoint);
            _controlEndpoint = new IPEndPoint(IPAddress.Loopback, config.ControlPort.Value);
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

        if (!await WaitForSocksPortReadyAsync(
                config.SocksPort,
                token,
                TimeSpan.FromSeconds(45),
                () => _process?.HasExited == true).ConfigureAwait(false))
        {
            var details = RecentOutput;
            Stop();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"ArtiHop started but SOCKS port {config.SocksPort} did not become ready."
                : $"ArtiHop started but SOCKS port {config.SocksPort} did not become ready. Recent output: {details}");
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

            // Best-effort read of the "OK" acknowledgement.
            var buffer = new byte[16];
            try
            {
                await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch
            {
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"ArtiHop new-identity request failed: {ex.Message}");
            return false;
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
}
