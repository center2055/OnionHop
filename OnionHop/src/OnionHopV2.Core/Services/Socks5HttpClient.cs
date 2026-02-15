using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Services;

internal static class Socks5HttpClient
{
    public static HttpClient Create(string proxyHost, int proxyPort, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(proxyHost))
        {
            throw new ArgumentException("Proxy host is required.", nameof(proxyHost));
        }

        if (proxyPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyPort), "Proxy port must be between 1 and 65535.");
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, token) =>
            {
                // Open TCP to SOCKS5 proxy
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(proxyHost, proxyPort, token).ConfigureAwait(false);
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    socket = null;

                    await ConnectThroughSocks5Async(stream, context.DnsEndPoint, token).ConfigureAwait(false);
                    return stream;
                }
                catch
                {
                    try
                    {
                        socket?.Dispose();
                    }
                    catch
                    {
                    }

                    throw;
                }
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");
        return client;
    }

    private static async Task ConnectThroughSocks5Async(Stream stream, DnsEndPoint destination, CancellationToken token)
    {
        // Greeting: VER, NMETHODS, METHODS...
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var greetingResponse = new byte[2];
        await ReadExactAsync(stream, greetingResponse, token).ConfigureAwait(false);
        if (greetingResponse[0] != 0x05 || greetingResponse[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 proxy rejected unauthenticated negotiation.");
        }

        // CONNECT request
        var host = NormalizeHost(destination.Host);
        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length is 0 or > 255)
        {
            throw new InvalidOperationException("SOCKS5 destination host is invalid.");
        }

        var portBytes = BitConverter.GetBytes((ushort)destination.Port);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(portBytes);
        }

        var request = new byte[4 + 1 + hostBytes.Length + 2];
        request[0] = 0x05; // VER
        request[1] = 0x01; // CMD=CONNECT
        request[2] = 0x00; // RSV
        request[3] = 0x03; // ATYP=DOMAIN
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        Buffer.BlockCopy(portBytes, 0, request, 5 + hostBytes.Length, 2);

        await stream.WriteAsync(request, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        // Response: VER, REP, RSV, ATYP, BND.ADDR, BND.PORT
        var header = new byte[4];
        await ReadExactAsync(stream, header, token).ConfigureAwait(false);

        if (header[0] != 0x05)
        {
            throw new InvalidOperationException("SOCKS5 proxy returned an invalid response.");
        }

        if (header[1] != 0x00)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"SOCKS5 connect failed: {DescribeReplyCode(header[1])}."));
        }

        var addressType = header[3];
        var addressLength = addressType switch
        {
            0x01 => 4,  // IPv4
            0x04 => 16, // IPv6
            0x03 => await ReadDomainLengthAsync(stream, token).ConfigureAwait(false),
            _ => throw new InvalidOperationException("SOCKS5 proxy returned an unknown address type.")
        };

        var skip = new byte[addressLength + 2];
        await ReadExactAsync(stream, skip, token).ConfigureAwait(false);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await ReadExactAsync(stream, length, token).ConfigureAwait(false);
        return length[0];
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("SOCKS5 proxy connection closed unexpectedly.");
            }

            offset += read;
        }
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0)
        {
            return host;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        // Ensure IDN hostnames are converted to ASCII/punycode.
        // SOCKS5 domain lengths are byte-limited (<= 255).
        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch
        {
            return host;
        }
    }

    private static string DescribeReplyCode(byte code)
    {
        return code switch
        {
            0x01 => "general SOCKS server failure",
            0x02 => "connection not allowed by ruleset",
            0x03 => "network unreachable",
            0x04 => "host unreachable",
            0x05 => "connection refused",
            0x06 => "TTL expired",
            0x07 => "command not supported",
            0x08 => "address type not supported",
            _ => $"unknown error ({code.ToString(CultureInfo.InvariantCulture)})"
        };
    }
}

