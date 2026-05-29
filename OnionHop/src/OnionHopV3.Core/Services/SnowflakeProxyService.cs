using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Snapshot of the standalone Snowflake proxy's volunteer status.
/// </summary>
public readonly record struct SnowflakeProxyStatus(
    bool IsRunning,
    string NatType,
    long ConnectionsServed,
    string TrafficSummary,
    string Message);

/// <summary>
/// Runs the standalone Snowflake proxy (snowflake-proxy) so the user can volunteer as a Snowflake
/// bridge, relaying censored users' traffic into Tor over WebRTC. This is donor-side and entirely
/// independent of the user's own Tor connection. Status (NAT type, connections served, traffic
/// relayed) is parsed from the proxy's periodic summary output.
/// </summary>
internal sealed class SnowflakeProxyService : IDisposable
{
    private const int RecentOutputCapacity = 40;

    private static readonly Regex ConnectionsRegex =
        new(@"there were (\d+)\s+(?:completed\s+)?connections", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrafficRegex =
        new(@"Traffic Relayed\s+(.+?)\.\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NatTypeRegex =
        new(@"NAT type[:=]?\s*(unrestricted|restricted|unknown)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Action<string> _log;
    private readonly Queue<string> _recentOutputLines = new();
    private readonly object _stateLock = new();
    private Process? _process;
    private bool _disposed;

    private string _natType = "unknown";
    private long _connectionsServed;
    private string _trafficSummary = string.Empty;

    public SnowflakeProxyService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Raised whenever the parsed status changes (start, NAT type, summary, exit).</summary>
    public event EventHandler<SnowflakeProxyStatus>? StatusChanged;
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

    public SnowflakeProxyStatus CurrentStatus()
    {
        lock (_stateLock)
        {
            return new SnowflakeProxyStatus(IsRunning, _natType, _connectionsServed, _trafficSummary, string.Empty);
        }
    }

    public Task StartAsync(SnowflakeProxyConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SnowflakeProxyService));
        }

        if (string.IsNullOrWhiteSpace(config.ProxyPath))
        {
            throw new ArgumentException("Snowflake proxy path is required.", nameof(config));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            _natType = "unknown";
            _connectionsServed = 0;
            _trafficSummary = string.Empty;
        }

        var summarySeconds = config.SummaryIntervalSeconds is >= 5 and <= 3600 ? config.SummaryIntervalSeconds : 60;
        var arguments = new List<string>
        {
            "-summary-interval", $"{summarySeconds}s"
        };
        // capacity 0 = unlimited; only pass when the user set a positive cap.
        if (config.Capacity > 0)
        {
            arguments.Add("-capacity");
            arguments.Add(config.Capacity.ToString(CultureInfo.InvariantCulture));
        }

        _log($"Snowflake proxy arguments: {string.Join(" ", arguments)}");

        var psi = new ProcessStartInfo(config.ProxyPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory
                ?? System.IO.Path.GetDirectoryName(config.ProxyPath)
                ?? AppContext.BaseDirectory
        };

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
            throw new InvalidOperationException("Unable to launch the Snowflake proxy.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _log("Snowflake proxy started. You are now volunteering as a Snowflake bridge; this helps censored users reach Tor and is independent of your own connection.");
        RaiseStatus("Snowflake proxy started.");
        return Task.CompletedTask;
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
            _log($"Failed to stop the Snowflake proxy: {ex.Message}");
        }
        finally
        {
            _process.OutputDataReceived -= HandleOutput;
            _process.ErrorDataReceived -= HandleOutput;
            _process.Exited -= HandleExited;
            _process.Dispose();
            _process = null;
            RaiseStatus("Snowflake proxy stopped.");
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

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
        RaiseStatus("Snowflake proxy exited.");
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

        var changed = false;

        var natMatch = NatTypeRegex.Match(e.Data);
        if (natMatch.Success)
        {
            lock (_stateLock)
            {
                _natType = natMatch.Groups[1].Value.ToLowerInvariant();
            }
            changed = true;
        }

        var connMatch = ConnectionsRegex.Match(e.Data);
        if (connMatch.Success && long.TryParse(connMatch.Groups[1].Value, out var inInterval))
        {
            lock (_stateLock)
            {
                // The proxy reports connections within each summary interval; accumulate a session total.
                _connectionsServed += inInterval;
            }
            changed = true;
        }

        var trafficMatch = TrafficRegex.Match(e.Data);
        if (trafficMatch.Success)
        {
            lock (_stateLock)
            {
                _trafficSummary = trafficMatch.Groups[1].Value.Trim();
            }
            changed = true;
        }

        if (changed)
        {
            RaiseStatus(e.Data.Trim());
        }
    }

    private void RaiseStatus(string message)
    {
        SnowflakeProxyStatus status;
        lock (_stateLock)
        {
            status = new SnowflakeProxyStatus(IsRunning, _natType, _connectionsServed, _trafficSummary, message);
        }

        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class SnowflakeProxyConfig
{
    public string ProxyPath { get; init; } = string.Empty;
    public uint Capacity { get; init; }
    public int SummaryIntervalSeconds { get; init; } = 60;
    public string? WorkingDirectory { get; init; }
}
