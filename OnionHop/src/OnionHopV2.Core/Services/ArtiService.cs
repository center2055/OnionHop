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

namespace OnionHopV2.Core.Services;

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

        return $"""
            [proxy]
            socks_listen = "{EscapeTomlString(endpoint)}"
            dns_listen = 0

            [storage]
            state_dir = "{EscapeTomlString(stateDir)}"
            cache_dir = "{EscapeTomlString(cacheDir)}"

            [logging]
            console = "info"
            """;
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

    private static string EscapeTomlString(string value)
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
}
