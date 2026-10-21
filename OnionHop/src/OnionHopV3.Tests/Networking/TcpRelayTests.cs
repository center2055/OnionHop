using System.Net;
using System.Net.Sockets;
using System.Text;
using OnionHopV3.Core.Networking;
using Xunit;

namespace OnionHopV3.Tests.Networking;

/// <summary>
/// The relay that shares a loopback-only listener (Arti, ArtiHop, the HTTP proxy) on one chosen
/// interface (#89) must pass both directions through untouched, including a reply that comes after
/// the client finished sending.
/// </summary>
public sealed class TcpRelayTests
{
    // Reads everything until the client finishes sending, then answers and closes.
    private static async Task RunReplyAfterEofServerAsync(TcpListener server, string prefix)
    {
        using var socket = await server.AcceptSocketAsync();
        var received = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        await socket.SendAsync(Encoding.ASCII.GetBytes(prefix + Encoding.ASCII.GetString(received.ToArray())), SocketFlags.None);
        socket.Shutdown(SocketShutdown.Both);
    }

    private static async Task<string> SendAndReadAllAsync(IPEndPoint endpoint, string request)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(endpoint);
        await client.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);
        client.Shutdown(SocketShutdown.Send);

        var response = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await client.ReceiveAsync(buffer, SocketFlags.None)) > 0)
        {
            response.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(response.ToArray());
    }

    [Fact]
    public async Task Passes_a_request_through_and_the_reply_that_follows_a_half_close()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var target = (IPEndPoint)server.LocalEndpoint;
            var serving = RunReplyAfterEofServerAsync(server, "echo:");
            using var relay = TcpRelay.Start(new IPEndPoint(IPAddress.Loopback, 0), target);

            var response = await SendAndReadAllAsync(relay.ListenEndpoint, "GET / HTTP/1.0\r\n\r\n").WaitAsync(TimeSpan.FromSeconds(10));
            await serving.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("echo:GET / HTTP/1.0\r\n\r\n", response);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Serves_connections_side_by_side()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var target = (IPEndPoint)server.LocalEndpoint;
            var serving = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunReplyAfterEofServerAsync(server, "ok:")));
            using var relay = TcpRelay.Start(new IPEndPoint(IPAddress.Loopback, 0), target);

            var responses = await Task.WhenAll(
                SendAndReadAllAsync(relay.ListenEndpoint, "a"),
                SendAndReadAllAsync(relay.ListenEndpoint, "b"),
                SendAndReadAllAsync(relay.ListenEndpoint, "c")).WaitAsync(TimeSpan.FromSeconds(10));
            await serving.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(["ok:a", "ok:b", "ok:c"], responses.Order());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void Stops_listening_when_disposed()
    {
        var relay = TcpRelay.Start(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 9));
        var endpoint = relay.ListenEndpoint;
        relay.Dispose();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.ThrowsAny<SocketException>(() => client.Connect(endpoint));
    }
}
