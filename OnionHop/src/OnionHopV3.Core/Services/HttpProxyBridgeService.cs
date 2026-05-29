using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

internal sealed class HttpProxyBridgeService : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly byte[] HeaderTerminator = [13, 10, 13, 10];
    private readonly Action<string> _log;
    private readonly object _taskLock = new();
    private readonly List<Task> _clientTasks = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    public HttpProxyBridgeService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsRunning => _listener != null;

    public Task StartAsync(HttpProxyBridgeConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HttpProxyBridgeService));
        }

        if (config.ListenPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(config.ListenPort), "HTTP proxy listen port must be between 1 and 65535.");
        }

        if (config.SocksProxyPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(config.SocksProxyPort), "SOCKS proxy port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(config.SocksProxyHost))
        {
            throw new ArgumentException("SOCKS proxy host is required.", nameof(config.SocksProxyHost));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        var listenAddress = ResolveListenAddress(config.ListenAddress);
        _listener = new TcpListener(listenAddress, config.ListenPort);
        _listener.Server.NoDelay = true;
        _listener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _acceptTask = Task.Run(() => AcceptLoopAsync(config, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        try
        {
            cts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            cts?.Dispose();
        }

        var listener = _listener;
        _listener = null;
        try
        {
            listener?.Stop();
        }
        catch
        {
        }

        var acceptTask = _acceptTask;
        _acceptTask = null;
        if (acceptTask != null)
        {
            try
            {
                acceptTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        Task[] pending;
        lock (_taskLock)
        {
            pending = _clientTasks.Where(task => !task.IsCompleted).ToArray();
            _clientTasks.Clear();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(1));
        }
        catch
        {
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

    private async Task AcceptLoopAsync(HttpProxyBridgeConfig config, CancellationToken token)
    {
        if (_listener == null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _log($"HTTP proxy bridge accept failed: {ex.Message}");
                }

                await Task.Delay(75, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (client == null)
            {
                continue;
            }

            var task = HandleClientAsync(client, config, token);
            lock (_taskLock)
            {
                _clientTasks.Add(task);
            }

            _ = task.ContinueWith(_ =>
            {
                lock (_taskLock)
                {
                    _clientTasks.Remove(task);
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, HttpProxyBridgeConfig config, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var clientStream = client.GetStream();
                var request = await ReadRequestHeaderAsync(clientStream, token).ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }

                if (!TryParseRequestLine(request.Value.RequestLine, out var method, out var target, out var version))
                {
                    await SendSimpleResponseAsync(clientStream, 400, "Bad Request", "Invalid HTTP proxy request line.", token).ConfigureAwait(false);
                    return;
                }

                var headers = ParseHeaders(request.Value.HeaderLines);
                if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseHostAndPort(target, 443, out var destinationHost, out var destinationPort))
                    {
                        await SendSimpleResponseAsync(clientStream, 400, "Bad Request", "Invalid CONNECT destination.", token).ConfigureAwait(false);
                        return;
                    }

                    await HandleConnectTunnelAsync(
                        clientStream,
                        request.Value.Remainder,
                        config,
                        destinationHost,
                        destinationPort,
                        token).ConfigureAwait(false);
                    return;
                }

                if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                    transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    await SendSimpleResponseAsync(clientStream, 501, "Not Implemented", "Chunked request bodies are not supported by this proxy bridge.", token).ConfigureAwait(false);
                    return;
                }

                if (!TryResolveForwardTarget(target, headers, out var destination, out var pathAndQuery))
                {
                    await SendSimpleResponseAsync(clientStream, 400, "Bad Request", "Unable to resolve HTTP destination.", token).ConfigureAwait(false);
                    return;
                }

                await HandleForwardRequestAsync(
                    clientStream,
                    request.Value.Remainder,
                    config,
                    method,
                    version,
                    headers,
                    destination,
                    pathAndQuery,
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"HTTP proxy bridge client handling failed: {ex.Message}");
        }
    }

    private async Task HandleConnectTunnelAsync(
        Stream clientStream,
        byte[] bufferedClientBytes,
        HttpProxyBridgeConfig config,
        string destinationHost,
        int destinationPort,
        CancellationToken token)
    {
        using var remoteClient = await ConnectViaSocksAsync(
            config.SocksProxyHost,
            config.SocksProxyPort,
            destinationHost,
            destinationPort,
            token).ConfigureAwait(false);

        await using var remoteStream = remoteClient.GetStream();
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: OnionHop\r\n\r\n", token).ConfigureAwait(false);
        if (bufferedClientBytes.Length > 0)
        {
            await remoteStream.WriteAsync(bufferedClientBytes.AsMemory(0, bufferedClientBytes.Length), token).ConfigureAwait(false);
            await remoteStream.FlushAsync(token).ConfigureAwait(false);
        }

        await TunnelBidirectionalAsync(clientStream, remoteStream, token).ConfigureAwait(false);
    }

    private async Task HandleForwardRequestAsync(
        Stream clientStream,
        byte[] bufferedClientBytes,
        HttpProxyBridgeConfig config,
        string method,
        string version,
        IReadOnlyDictionary<string, string> headers,
        ProxyDestination destination,
        string pathAndQuery,
        CancellationToken token)
    {
        using var remoteClient = await ConnectViaSocksAsync(
            config.SocksProxyHost,
            config.SocksProxyPort,
            destination.Host,
            destination.Port,
            token).ConfigureAwait(false);

        await using var remoteStream = remoteClient.GetStream();
        var outboundHeader = BuildForwardRequestHeader(
            method,
            version,
            headers,
            destination,
            pathAndQuery);
        await remoteStream.WriteAsync(outboundHeader.AsMemory(0, outboundHeader.Length), token).ConfigureAwait(false);

        var contentLength = ParseContentLength(headers);
        if (contentLength.HasValue)
        {
            var available = Math.Min(bufferedClientBytes.Length, contentLength.Value);
            if (available > 0)
            {
                await remoteStream.WriteAsync(bufferedClientBytes.AsMemory(0, available), token).ConfigureAwait(false);
            }

            var remaining = contentLength.Value - available;
            if (remaining > 0)
            {
                await RelayFixedLengthAsync(clientStream, remoteStream, remaining, token).ConfigureAwait(false);
            }
        }
        else if (bufferedClientBytes.Length > 0)
        {
            await remoteStream.WriteAsync(bufferedClientBytes.AsMemory(0, bufferedClientBytes.Length), token).ConfigureAwait(false);
        }

        await remoteStream.FlushAsync(token).ConfigureAwait(false);
        await remoteStream.CopyToAsync(clientStream, 81920, token).ConfigureAwait(false);
    }

    private static async Task RelayFixedLengthAsync(Stream source, Stream destination, int bytesToRelay, CancellationToken token)
    {
        var remaining = bytesToRelay;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (remaining > 0)
            {
                var readSize = Math.Min(remaining, buffer.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, readSize), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException("Client closed connection before sending full HTTP request body.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task TunnelBidirectionalAsync(Stream clientStream, Stream remoteStream, CancellationToken token)
    {
        var clientToRemote = PumpStreamAsync(clientStream, remoteStream, token);
        var remoteToClient = PumpStreamAsync(remoteStream, clientStream, token);
        await Task.WhenAny(clientToRemote, remoteToClient).ConfigureAwait(false);
    }

    private static async Task PumpStreamAsync(Stream source, Stream destination, CancellationToken token)
    {
        try
        {
            await source.CopyToAsync(destination, 81920, token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<TcpClient> ConnectViaSocksAsync(
        string socksHost,
        int socksPort,
        string destinationHost,
        int destinationPort,
        CancellationToken token)
    {
        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(socksHost, socksPort, token).ConfigureAwait(false);
            var stream = tcpClient.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var greeting = new byte[2];
            await ReadExactAsync(stream, greeting, token).ConfigureAwait(false);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
            {
                throw new InvalidOperationException("SOCKS5 proxy rejected no-auth negotiation.");
            }

            var request = BuildSocksConnectRequest(destinationHost, destinationPort);
            await stream.WriteAsync(request.AsMemory(0, request.Length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var header = new byte[4];
            await ReadExactAsync(stream, header, token).ConfigureAwait(false);
            if (header[0] != 0x05)
            {
                throw new InvalidOperationException("SOCKS5 proxy returned an invalid reply.");
            }

            if (header[1] != 0x00)
            {
                throw new InvalidOperationException(FormattableString.Invariant($"SOCKS5 connect failed with reply 0x{header[1]:X2}."));
            }

            var skipLength = header[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadAddressLengthAsync(stream, token).ConfigureAwait(false),
                _ => throw new InvalidOperationException("SOCKS5 proxy returned an unknown address type.")
            };
            var skip = new byte[skipLength + 2];
            await ReadExactAsync(stream, skip, token).ConfigureAwait(false);

            return tcpClient;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private static byte[] BuildSocksConnectRequest(string destinationHost, int destinationPort)
    {
        if (destinationPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationPort), "Destination port must be between 1 and 65535.");
        }

        if (IPAddress.TryParse(destinationHost, out var ipAddress))
        {
            var ipBytes = ipAddress.GetAddressBytes();
            var request = new byte[6 + ipBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = ipBytes.Length == 16 ? (byte)0x04 : (byte)0x01;
            Buffer.BlockCopy(ipBytes, 0, request, 4, ipBytes.Length);
            request[^2] = (byte)(destinationPort >> 8);
            request[^1] = (byte)(destinationPort & 0xFF);
            return request;
        }

        var host = NormalizeHost(destinationHost);
        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length is 0 or > 255)
        {
            throw new InvalidOperationException("Destination host is invalid for SOCKS5.");
        }

        var domainRequest = new byte[7 + hostBytes.Length];
        domainRequest[0] = 0x05;
        domainRequest[1] = 0x01;
        domainRequest[2] = 0x00;
        domainRequest[3] = 0x03;
        domainRequest[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, domainRequest, 5, hostBytes.Length);
        domainRequest[^2] = (byte)(destinationPort >> 8);
        domainRequest[^1] = (byte)(destinationPort & 0xFF);
        return domainRequest;
    }

    private static async Task<int> ReadAddressLengthAsync(Stream stream, CancellationToken token)
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
                throw new IOException("Unexpected end of stream.");
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

        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch
        {
            return host;
        }
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes.AsMemory(0, bytes.Length), token).AsTask();
    }

    private static async Task SendSimpleResponseAsync(
        Stream stream,
        int statusCode,
        string reasonPhrase,
        string message,
        CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = FormattableString.Invariant(
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await WriteAsciiAsync(stream, header, token).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body.AsMemory(0, body.Length), token).ConfigureAwait(false);
        }
    }

    private static async Task<HttpRequestHeader?> ReadRequestHeaderAsync(Stream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var data = new MemoryStream();
            while (data.Length < MaxHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                data.Write(buffer, 0, read);
                var payload = data.GetBuffer();
                var payloadLength = checked((int)data.Length);
                var terminatorIndex = IndexOf(payload, payloadLength, HeaderTerminator);
                if (terminatorIndex < 0)
                {
                    continue;
                }

                var headerBytes = new byte[terminatorIndex];
                Buffer.BlockCopy(payload, 0, headerBytes, 0, terminatorIndex);

                var remainderOffset = terminatorIndex + HeaderTerminator.Length;
                var remainderLength = payloadLength - remainderOffset;
                var remainder = remainderLength > 0 ? new byte[remainderLength] : Array.Empty<byte>();
                if (remainderLength > 0)
                {
                    Buffer.BlockCopy(payload, remainderOffset, remainder, 0, remainderLength);
                }

                var headerText = Encoding.ASCII.GetString(headerBytes);
                var lines = headerText.Split("\r\n", StringSplitOptions.None);
                if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                {
                    return null;
                }

                var requestLine = lines[0];
                var headerLines = lines.Skip(1)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();
                return new HttpRequestHeader(requestLine, headerLines, remainder);
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int IndexOf(byte[] source, int sourceLength, byte[] sequence)
    {
        if (sequence.Length == 0 || sourceLength < sequence.Length)
        {
            return -1;
        }

        for (var i = 0; i <= sourceLength - sequence.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (source[i + j] == sequence[j])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseRequestLine(string requestLine, out string method, out string target, out string version)
    {
        method = string.Empty;
        target = string.Empty;
        version = "HTTP/1.1";

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return false;
        }

        var parts = requestLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        method = parts[0].Trim();
        target = parts[1].Trim();
        version = parts.Length >= 3 ? parts[2].Trim() : "HTTP/1.1";

        if (method.Length == 0 || target.Length == 0)
        {
            return false;
        }

        if (!version.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            version = "HTTP/1.1";
        }

        return true;
    }

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> headerLines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerLines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator >= line.Length - 1)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            headers[name] = value;
        }

        return headers;
    }

    private static byte[] BuildForwardRequestHeader(
        string method,
        string version,
        IReadOnlyDictionary<string, string> originalHeaders,
        ProxyDestination destination,
        string pathAndQuery)
    {
        var builder = new StringBuilder(512);
        builder.Append(method);
        builder.Append(' ');
        builder.Append(pathAndQuery);
        builder.Append(' ');
        builder.Append(version);
        builder.Append("\r\n");

        var hasHost = false;
        foreach (var pair in originalHeaders)
        {
            if (pair.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hasHost = true;
            }

            builder.Append(pair.Key);
            builder.Append(": ");
            builder.Append(pair.Value);
            builder.Append("\r\n");
        }

        if (!hasHost)
        {
            builder.Append("Host: ");
            builder.Append(BuildHostHeaderValue(destination));
            builder.Append("\r\n");
        }

        builder.Append("Connection: close\r\n");
        builder.Append("Proxy-Connection: close\r\n");
        builder.Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string BuildHostHeaderValue(ProxyDestination destination)
    {
        var host = destination.Host;
        var isDefaultPort = destination.IsHttps ? destination.Port == 443 : destination.Port == 80;
        if (isDefaultPort)
        {
            return host;
        }

        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
        {
            host = "[" + host + "]";
        }

        return host + ":" + destination.Port.ToString(CultureInfo.InvariantCulture);
    }

    private static int? ParseContentLength(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Length", out var contentLengthRaw))
        {
            return null;
        }

        if (!int.TryParse(contentLengthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength))
        {
            return null;
        }

        return contentLength < 0 ? null : contentLength;
    }

    private static bool TryResolveForwardTarget(
        string target,
        IReadOnlyDictionary<string, string> headers,
        out ProxyDestination destination,
        out string pathAndQuery)
    {
        destination = default;
        pathAndQuery = "/";

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            if (!absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var host = absoluteUri.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            var port = absoluteUri.IsDefaultPort
                ? (absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : absoluteUri.Port;
            var path = absoluteUri.PathAndQuery;
            pathAndQuery = string.IsNullOrWhiteSpace(path) ? "/" : path;
            destination = new ProxyDestination(host, port, absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (!target.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        if (!headers.TryGetValue("Host", out var hostHeader) ||
            !TryParseHostAndPort(hostHeader, 80, out var hostFromHeader, out var portFromHeader))
        {
            return false;
        }

        pathAndQuery = target;
        destination = new ProxyDestination(hostFromHeader, portFromHeader, false);
        return true;
    }

    private static bool TryParseHostAndPort(string value, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = trimmed.IndexOf(']');
            if (closing <= 1)
            {
                return false;
            }

            host = trimmed[1..closing];
            if (closing + 1 < trimmed.Length)
            {
                if (trimmed[closing + 1] != ':')
                {
                    return false;
                }

                var portText = trimmed[(closing + 2)..];
                if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port is <= 0 or > 65535)
                {
                    return false;
                }
            }

            return true;
        }

        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon > 0 && trimmed.Count(character => character == ':') == 1)
        {
            var portText = trimmed[(lastColon + 1)..];
            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) &&
                parsedPort is > 0 and <= 65535)
            {
                host = trimmed[..lastColon];
                port = parsedPort;
                return host.Length > 0;
            }
        }

        host = trimmed;
        return host.Length > 0;
    }

    private static IPAddress ResolveListenAddress(string? listenAddress)
    {
        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            return IPAddress.Loopback;
        }

        var trimmed = listenAddress.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(trimmed, out var parsedAddress))
        {
            throw new InvalidOperationException($"Invalid listen address '{listenAddress}'.");
        }

        return parsedAddress;
    }

    private readonly record struct HttpRequestHeader(string RequestLine, string[] HeaderLines, byte[] Remainder);
    private readonly record struct ProxyDestination(string Host, int Port, bool IsHttps);
}

internal sealed class HttpProxyBridgeConfig
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; }
    public string SocksProxyHost { get; init; } = "127.0.0.1";
    public int SocksProxyPort { get; init; }
}
