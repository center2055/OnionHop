using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OnionHopV3.Core.Services;

/// <summary>
/// A parsed dnstt bridge line:
/// <c>dnstt &lt;ip:port&gt; &lt;fingerprint&gt; doh=&lt;url&gt; pubkey=&lt;hex&gt; domain=&lt;domain&gt;</c>
/// (or <c>dot=&lt;host:port&gt;</c> instead of <c>doh=</c>). The ip:port is a placeholder; the real
/// path is the DNS tunnel to <see cref="Domain"/>. <see cref="Fingerprint"/> authenticates the Tor
/// relay reached through the tunnel.
/// </summary>
internal sealed record DnsttBridge(string Fingerprint, string? Doh, string? Dot, string Pubkey, string Domain);

/// <summary>
/// dnstt is a DNS tunnel, not a Tor pluggable transport. <c>dnstt-client</c> runs as a standalone
/// local forwarder: it listens on 127.0.0.1:port and tunnels Tor's traffic over DoH/DoT to a dnstt
/// server that forwards to a real Tor bridge's ORPort. We launch one forwarder per dnstt bridge and
/// hand Tor a plain vanilla <c>Bridge 127.0.0.1:port &lt;fingerprint&gt;</c> line (no
/// ClientTransportPlugin). Useful where everything but DNS is blocked.
/// </summary>
internal sealed class DnsttForwarderService : IDisposable
{
    private readonly Action<string> _log;
    private readonly List<Process> _processes = new();
    private readonly object _lock = new();
    private bool _disposed;

    public DnsttForwarderService(Action<string> log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    public bool HasActiveForwarders
    {
        get { lock (_lock) { return _processes.Count > 0; } }
    }

    /// <summary>Parse a "dnstt ..." bridge line; returns null if it is not a usable dnstt line.</summary>
    public static DnsttBridge? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var text = line.Trim();
        if (text.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
        {
            text = text["Bridge ".Length..].Trim();
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3 || !string.Equals(tokens[0], "dnstt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fingerprint = tokens[2];
        string? doh = null, dot = null, pubkey = null, domain = null;
        for (var i = 3; i < tokens.Length; i++)
        {
            var eq = tokens[i].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = tokens[i][..eq].ToLowerInvariant();
            var value = tokens[i][(eq + 1)..];
            switch (key)
            {
                case "doh": doh = value; break;
                case "dot": dot = value; break;
                case "pubkey": pubkey = value; break;
                case "domain": domain = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(pubkey))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(doh) && string.IsNullOrWhiteSpace(dot))
        {
            return null;
        }

        if (fingerprint.Length != 40 || !fingerprint.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new DnsttBridge(fingerprint, doh, dot, pubkey, domain);
    }

    /// <summary>Launch a dnstt-client forwarder for <paramref name="bridge"/> listening on 127.0.0.1:<paramref name="localPort"/>.</summary>
    public bool Start(string? exePath, DnsttBridge bridge, int localPort)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DnsttForwarderService));
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            _log("dnstt-client binary was not found. Build it with download-deps.ps1 (needs the Go toolchain), bundle it under tor/pluggable_transports, or set ONIONHOP_DNSTT_PATH.");
            return false;
        }

        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(bridge.Doh))
        {
            arguments.Add("-doh");
            arguments.Add(bridge.Doh!);
        }
        else
        {
            arguments.Add("-dot");
            arguments.Add(bridge.Dot!);
        }

        arguments.Add("-pubkey");
        arguments.Add(bridge.Pubkey);
        arguments.Add(bridge.Domain);
        arguments.Add(string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{localPort}"));

        var psi = new ProcessStartInfo(exePath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath!) ?? AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log($"dnstt: {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log($"dnstt: {e.Data}"); };

            if (!process.Start())
            {
                _log("Unable to launch dnstt-client.");
                process.Dispose();
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_lock)
            {
                _processes.Add(process);
            }

            var via = !string.IsNullOrWhiteSpace(bridge.Doh) ? $"DoH {bridge.Doh}" : $"DoT {bridge.Dot}";
            _log($"dnstt-client started: tunnelling via {via} (domain {bridge.Domain}) -> 127.0.0.1:{localPort}.");
            return true;
        }
        catch (Exception ex)
        {
            _log($"Failed to start dnstt-client: {ex.Message}");
            return false;
        }
    }

    public void StopAll()
    {
        List<Process> processes;
        lock (_lock)
        {
            processes = new List<Process>(_processes);
            _processes.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // best effort
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

        StopAll();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
