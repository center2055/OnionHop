using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core;

namespace OnionHopV2.Core.Services;

internal sealed class VpnService : IDisposable
{
    private readonly Action<string> _log;
    private Process? _process;
    private bool _disposed;

    public VpnService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public async Task StartAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VpnService));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        var vpnCoreMode = NormalizeTunCoreMode(config.VpnCoreMode);
        var vpnCorePath = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? config.XrayPath
            : config.SingBoxPath;
        var vpnCoreLabel = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? "xray"
            : "sing-box";

        if (!File.Exists(vpnCorePath))
        {
            throw new FileNotFoundException($"VPN component missing: vpn\\{vpnCoreLabel}.exe", vpnCorePath);
        }

        if (!File.Exists(config.WintunPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\wintun.dll", config.WintunPath);
        }

        var workDir = Path.GetDirectoryName(vpnCorePath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", vpnCoreLabel);
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, $"{vpnCoreLabel}.json");

        var configJson = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? XrayConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunMtu)
            : VpnConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunStack,
                config.TunMtu,
                config.TunStrictRoute);
        await File.WriteAllTextAsync(configPath, configJson, token);

        _log($"Starting {vpnCoreLabel} with config: {configPath}");

        var psi = new ProcessStartInfo(vpnCorePath, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
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
            throw new InvalidOperationException($"Unable to launch {vpnCoreLabel}.exe");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(750, token);
        if (_process.HasExited)
        {
            throw new InvalidOperationException($"{vpnCoreLabel} exited unexpectedly during startup.");
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
            _log($"Failed to stop VPN core: {ex.Message}");
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

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        OutputReceived?.Invoke(sender, e);
    }

    private static string NormalizeTunCoreMode(string? value)
    {
        return string.Equals(value, OnionHopConnectOptions.TunCoreXray, StringComparison.OrdinalIgnoreCase)
            ? OnionHopConnectOptions.TunCoreXray
            : OnionHopConnectOptions.TunCoreSingBox;
    }
}

internal sealed class VpnLaunchConfig
{
    public string SingBoxPath { get; init; } = string.Empty;
    public string XrayPath { get; init; } = string.Empty;
    public string WintunPath { get; init; } = string.Empty;
    public string VpnCoreMode { get; init; } = OnionHopConnectOptions.TunCoreSingBox;
    public bool HybridRouting { get; init; }
    public bool SecureDns { get; init; }
    public int SocksPort { get; init; }
    public string DohServer { get; init; } = "cloudflare-dns.com";
    public int DohServerPort { get; init; } = 443;
    public string DohPath { get; init; } = "/dns-query";
    public bool RouteAllWebTrafficThroughTor { get; init; } = true;
    public bool BlockQuicForTorApps { get; init; } = true;
    public string TunStack { get; init; } = "mixed";
    public int? TunMtu { get; init; }
    public bool TunStrictRoute { get; init; } = true;
    public IReadOnlyList<string> TorAppProcessNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassAppProcessNames { get; init; } = Array.Empty<string>();
    [JsonIgnore]
    public Action<Process>? ProcessStarted { get; init; }
}
