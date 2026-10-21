using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Networking;

/// <summary>
/// Accepts connections on one address and pipes each one to a local port. Shares a listener that
/// only accepts loopback connections (an engine's SOCKS port, the HTTP proxy) on a single network
/// interface, without opening it on the others the way binding to 0.0.0.0 would.
/// </summary>
internal sealed class TcpRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();

    private TcpRelay(TcpListener listener, IPEndPoint target)
    {
        _listener = listener;
        _target = target;
    }

    public IPEndPoint ListenEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Starts listening right away; throws when the address cannot be bound.</summary>
    public static TcpRelay Start(IPEndPoint listen, IPEndPoint target)
    {
        var listener = new TcpListener(listen);
        listener.Start();
        var relay = new TcpRelay(listener, target);
        _ = relay.AcceptLoopAsync();
        return relay;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = PipeAsync(client);
        }
    }

    private async Task PipeAsync(Socket client)
    {
        using (client)
        using (var upstream = new Socket(_target.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                client.NoDelay = true;
                upstream.NoDelay = true;
                await upstream.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);

                var toTarget = PumpAsync(client, upstream, _stop.Token);
                var toClient = PumpAsync(upstream, client, _stop.Token);

                // A side that finished sending has been shut down for writing on the other; the
                // reply may still be on its way, so wait for the other direction too. A reset or an
                // error ends both: leaving this block closes the sockets.
                var first = await Task.WhenAny(toTarget, toClient).ConfigureAwait(false);
                if (first.Status == TaskStatus.RanToCompletion)
                {
                    await (first == toTarget ? toClient : toTarget).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task PumpAsync(Socket from, Socket to, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await from.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            if (read == 0)
            {
                to.Shutdown(SocketShutdown.Send);
                return;
            }

            var sent = 0;
            while (sent < read)
            {
                sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, token).ConfigureAwait(false);
            }
        }
    }

    // The token source is only canceled, never disposed: pipes still running may read its token.
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }
}
