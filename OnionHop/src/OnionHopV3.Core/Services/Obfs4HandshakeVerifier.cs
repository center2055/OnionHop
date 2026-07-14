using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Verifies obfs4 bridges with a REAL obfs4 handshake, not just a TCP connect. obfs4 is designed to
/// look like random bytes, so it cannot be probed without doing the handshake: a plain TCP connect
/// succeeds to a bridge that is dead (or blocked) at the obfs4 layer, which is why the TCP-only
/// reachability scan reports bridges as usable that Tor then fails with "general SOCKS server
/// failure". We drive the bundled obfs4 client (lyrebird) over its pluggable-transport SOCKS port: a
/// SOCKS5 CONNECT that succeeds means the obfs4 handshake to the bridge completed.
///
/// IPv4 only - the SOCKS request uses the bridge's IPv4 literal, and CI/desktop IPv6 reachability is
/// unreliable; IPv6 obfs4 lines (and anything unparseable) are passed through untouched, never dropped.
/// Validated against live bridges (parity with the collector's obfs4 verification).
/// </summary>
public static class Obfs4HandshakeVerifier
{
    private static readonly Regex Obfs4Ipv4Line =
        new(@"^obfs4\s+(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})\s+\S+\s+(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsObfs4Line(string line) =>
        !string.IsNullOrWhiteSpace(line) && line.TrimStart().StartsWith("obfs4 ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse an IPv4 obfs4 bridge line into (host, port, socksArgs), where socksArgs is the
    /// <c>cert=...;iat-mode=...</c> string Tor passes to the transport via the SOCKS auth fields.
    /// Returns false for non-obfs4, IPv6, or cert-less lines (those are kept, not verified).
    /// </summary>
    internal static bool TryParseObfs4Ipv4(string line, out string host, out int port, out string socksArgs)
    {
        host = string.Empty;
        port = 0;
        socksArgs = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = Obfs4Ipv4Line.Match(line.Trim());
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out port) || port is <= 0 or > 65535)
        {
            return false;
        }

        var rest = match.Groups[3].Value;
        var cert = Regex.Match(rest, @"(?:^|\s)cert=(\S+)", RegexOptions.IgnoreCase);
        if (!cert.Success)
        {
            return false;
        }

        var iat = Regex.Match(rest, @"(?:^|\s)iat-mode=(\S+)", RegexOptions.IgnoreCase);
        host = match.Groups[1].Value;
        socksArgs = $"cert={cert.Groups[1].Value};iat-mode={(iat.Success ? iat.Groups[1].Value : "0")}";
        return true;
    }

    /// <summary>
    /// Handshake-verify the IPv4 obfs4 lines in <paramref name="bridgeLines"/> and return the full list
    /// with dead IPv4 obfs4 bridges removed (non-obfs4 and IPv6 obfs4 lines are preserved in place).
    /// <paramref name="ran"/> is false when the obfs4 client could not be started or there was nothing
    /// to verify, so the caller keeps the unfiltered list rather than trusting an empty result.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Lines, bool Ran)> VerifyAsync(
        IReadOnlyList<string> bridgeLines,
        string lyrebirdPath,
        int workers,
        int maxToVerify,
        TimeSpan perProbeTimeout,
        CancellationToken token)
    {
        if (bridgeLines == null || bridgeLines.Count == 0 || string.IsNullOrWhiteSpace(lyrebirdPath) || !File.Exists(lyrebirdPath))
        {
            return (bridgeLines ?? Array.Empty<string>(), false);
        }

        var testable = new List<(string Line, string Host, int Port, string Args)>();
        foreach (var line in bridgeLines)
        {
            if (TryParseObfs4Ipv4(line, out var host, out var port, out var args))
            {
                testable.Add((line, host, port, args));
            }
        }

        if (testable.Count == 0)
        {
            return (bridgeLines, false); // no IPv4 obfs4 to verify - leave the list as-is
        }

        // Verify at most the fastest-first `maxToVerify` bridges (Tor only uses ~64 anyway); any obfs4
        // beyond the cap are kept unverified rather than dropped, to bound connect latency.
        if (maxToVerify > 0 && testable.Count > maxToVerify)
        {
            testable = testable.Take(maxToVerify).ToList();
        }

        Process? proc = null;
        try
        {
            var (process, socksPort) = await StartObfs4ClientAsync(lyrebirdPath, token).ConfigureAwait(false);
            proc = process;
            if (proc == null || socksPort is null)
            {
                return (bridgeLines, false); // client would not start - keep the unfiltered list
            }

            var clamped = Math.Clamp(workers, 1, 32);
            using var throttle = new SemaphoreSlim(clamped, clamped);
            var alive = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            var tasks = testable.Select(async b =>
            {
                await throttle.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (await Obfs4SocksHandshakeAsync(socksPort.Value, b.Host, b.Port, b.Args, perProbeTimeout, token).ConfigureAwait(false))
                    {
                        alive.TryAdd(b.Line, 0);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A single failed probe just means "not alive"; never fail the batch.
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Rebuild the list in original order: drop only the IPv4 obfs4 lines that failed the handshake.
            var testableLines = new HashSet<string>(testable.Select(t => t.Line), StringComparer.Ordinal);
            var kept = bridgeLines.Where(line => !testableLines.Contains(line) || alive.ContainsKey(line)).ToList();
            return (kept, true);
        }
        finally
        {
            StopObfs4Client(proc);
        }
    }

    private static async Task<(Process? Process, int? SocksPort)> StartObfs4ClientAsync(string lyrebirdPath, CancellationToken token)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "onionhop-obfs4-verify");
        try
        {
            Directory.CreateDirectory(stateDir);
        }
        catch
        {
            // A missing state dir is fine; lyrebird will report and we fall back.
        }

        var psi = new ProcessStartInfo(lyrebirdPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["TOR_PT_MANAGED_TRANSPORT_VER"] = "1";
        psi.Environment["TOR_PT_STATE_LOCATION"] = stateDir;
        psi.Environment["TOR_PT_EXIT_ON_STDIN_CLOSE"] = "1";
        psi.Environment["TOR_PT_CLIENT_TRANSPORTS"] = "obfs4";

        Process process;
        try
        {
            var started = Process.Start(psi);
            if (started == null)
            {
                return (null, null);
            }

            process = started;
        }
        catch
        {
            return (null, null);
        }

        // Read the PT handshake until "CMETHOD obfs4 socks5 127.0.0.1:PORT" (or CMETHODS DONE), bounded.
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        startupCts.CancelAfter(TimeSpan.FromSeconds(8));
        int? socksPort = null;
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
                if (line == null)
                {
                    break; // process closed stdout before advertising a method
                }

                var m = Regex.Match(line, @"^CMETHOD\s+obfs4\s+socks5\s+127\.0\.0\.1:(\d{1,5})", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p))
                {
                    socksPort = p;
                }

                if (line.StartsWith("CMETHODS DONE", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for CMETHOD; treat as unavailable.
        }

        if (socksPort is null)
        {
            StopObfs4Client(process);
            return (null, null);
        }

        return (process, socksPort);
    }

    private static void StopObfs4Client(Process? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { /* triggers EXIT_ON_STDIN_CLOSE */ }
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            try { process.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// SOCKS5 CONNECT through the obfs4 client to the bridge, passing the obfs4 args in the
    /// username/password auth fields (Tor's PT convention). A 0x00 reply means the obfs4 handshake
    /// completed - the bridge is alive at the obfs4 layer.
    /// </summary>
    private static async Task<bool> Obfs4SocksHandshakeAsync(
        int socksPort, string host, int port, string args, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, socksPort), timeoutCts.Token).ConfigureAwait(false);

            // Greeting: offer username/password auth (method 0x02).
            await SendAllAsync(socket, new byte[] { 0x05, 0x01, 0x02 }, timeoutCts.Token).ConfigureAwait(false);
            var greeting = await ReadExactAsync(socket, 2, timeoutCts.Token).ConfigureAwait(false);
            if (greeting is not [0x05, 0x02])
            {
                return false;
            }

            // Username/password auth: the transport args go in username (<=255), password is one NUL.
            var argBytes = Encoding.ASCII.GetBytes(args);
            byte[] uname;
            byte[] passwd;
            if (argBytes.Length <= 255)
            {
                uname = argBytes;
                passwd = new byte[] { 0x00 };
            }
            else
            {
                uname = argBytes[..255];
                passwd = argBytes[255..];
            }

            var auth = new byte[1 + 1 + uname.Length + 1 + passwd.Length];
            auth[0] = 0x01;
            auth[1] = (byte)uname.Length;
            Array.Copy(uname, 0, auth, 2, uname.Length);
            auth[2 + uname.Length] = (byte)passwd.Length;
            Array.Copy(passwd, 0, auth, 3 + uname.Length, passwd.Length);
            await SendAllAsync(socket, auth, timeoutCts.Token).ConfigureAwait(false);

            var authReply = await ReadExactAsync(socket, 2, timeoutCts.Token).ConfigureAwait(false);
            if (authReply is not [0x01, 0x00])
            {
                return false;
            }

            // CONNECT to the bridge IPv4:port.
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var ipBytes = address.GetAddressBytes();
            var request = new byte[10];
            request[0] = 0x05; // version
            request[1] = 0x01; // CONNECT
            request[2] = 0x00; // reserved
            request[3] = 0x01; // IPv4
            Array.Copy(ipBytes, 0, request, 4, 4);
            request[8] = (byte)(port >> 8);
            request[9] = (byte)(port & 0xFF);
            await SendAllAsync(socket, request, timeoutCts.Token).ConfigureAwait(false);

            var reply = await ReadExactAsync(socket, 2, timeoutCts.Token).ConfigureAwait(false);
            return reply is [0x05, 0x00]; // 0x00 = succeeded (obfs4 handshake completed)
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SendAllAsync(Socket socket, byte[] data, CancellationToken token)
    {
        var sent = 0;
        while (sent < data.Length)
        {
            sent += await socket.SendAsync(data.AsMemory(sent), SocketFlags.None, token).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadExactAsync(Socket socket, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, token).ConfigureAwait(false);
            if (n <= 0)
            {
                return Array.Empty<byte>(); // connection closed early
            }

            read += n;
        }

        return buffer;
    }
}
