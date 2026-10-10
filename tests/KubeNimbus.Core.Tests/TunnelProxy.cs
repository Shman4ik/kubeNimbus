using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// An in-process tunnelling proxy on a loopback port that speaks both of the protocols a
/// kubeconfig's <c>proxy-url</c> can name for an https API server: an HTTP proxy's
/// <c>CONNECT</c>, and SOCKS5 (RFC 1928, no authentication, the <c>CONNECT</c> command). Every
/// tunnel it opens is recorded with the name the client asked for and the bytes it carried,
/// so a test can assert the traffic really went through it.
/// </summary>
/// <remarks>
/// Every requested destination is sent to one upstream endpoint — the way the far side of a
/// bastion resolves a name the client cannot. That is what makes a test meaningful: the client
/// is given an API server name that does not resolve on this machine, so a request that
/// succeeds can only have come through here. A stand-in rather than a container image, because
/// a test run must not depend on pulling one.
/// </remarks>
internal sealed class TunnelProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly IPEndPoint _upstream;

    public TunnelProxy(IPEndPoint upstream)
    {
        _upstream = upstream;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every tunnel opened, in order.</summary>
    public ConcurrentQueue<Tunnel> Tunnels { get; } = new();

    /// <summary>What the stand-in refused, so a failing test can say why.</summary>
    public ConcurrentQueue<string> Refusals { get; } = new();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var first = new byte[1];
            if (await stream.ReadAsync(first, _stop.Token) == 0)
            {
                return;
            }

            var (protocol, host, port) = first[0] == 0x05
                ? await Socks5HandshakeAsync(stream)
                : await HttpConnectAsync(stream, (char)first[0]);
            if (host is null)
            {
                return;
            }

            using var upstream = new TcpClient();
            await upstream.ConnectAsync(_upstream, _stop.Token);
            var tunnel = new Tunnel(protocol, host, port);
            Tunnels.Enqueue(tunnel);

            if (protocol == "socks5")
            {
                // Succeeded, bound to 0.0.0.0:0 (the client does not use it).
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, _stop.Token);
            }
            else
            {
                await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _stop.Token);
            }

            var far = upstream.GetStream();
            using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var up = PumpAsync(stream, far, n => Interlocked.Add(ref tunnel.BytesUp, n), done.Token);
            var down = PumpAsync(far, stream, n => Interlocked.Add(ref tunnel.BytesDown, n), done.Token);
            await Task.WhenAny(up, down);
            await done.CancelAsync();
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client or the upstream went away; the tunnel is over.
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, Action<int> counted, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                counted(read);
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task<(string Protocol, string? Host, int Port)> Socks5HandshakeAsync(NetworkStream stream)
    {
        // Greeting: VER (already read), NMETHODS, METHODS.
        var count = await ReadByteAsync(stream);
        var methods = await ReadExactlyAsync(stream, count);
        if (!methods.Contains((byte)0x00))
        {
            Refusals.Enqueue("socks5: the client offered no 'no authentication' method");
            await stream.WriteAsync(new byte[] { 0x05, 0xFF }, _stop.Token);
            return ("socks5", null, 0);
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, _stop.Token);

        // Request: VER, CMD, RSV, ATYP, DST.ADDR, DST.PORT.
        var head = await ReadExactlyAsync(stream, 4);
        if (head[0] != 0x05 || head[1] != 0x01)
        {
            Refusals.Enqueue($"socks5: command {head[1]} is not CONNECT");
            await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, _stop.Token);
            return ("socks5", null, 0);
        }

        var host = head[3] switch
        {
            0x01 => new IPAddress(await ReadExactlyAsync(stream, 4)).ToString(),
            0x04 => new IPAddress(await ReadExactlyAsync(stream, 16)).ToString(),
            0x03 => Encoding.ASCII.GetString(await ReadExactlyAsync(stream, await ReadByteAsync(stream))),
            _ => null,
        };
        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactlyAsync(stream, 2));
        if (host is null)
        {
            Refusals.Enqueue($"socks5: address type {head[3]}");
        }

        return ("socks5", host, port);
    }

    private async Task<(string Protocol, string? Host, int Port)> HttpConnectAsync(NetworkStream stream, char first)
    {
        var header = new StringBuilder().Append(first);
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0 || header.Length > 16 * 1024)
            {
                return ("http", null, 0);
            }

            header.Append((char)one[0]);
        }

        var requestLine = header.ToString().Split("\r\n")[0].Split(' ');
        if (requestLine is not ["CONNECT", var target, _])
        {
            Refusals.Enqueue($"http: '{requestLine[0]}' is not CONNECT");
            await stream.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), _stop.Token);
            return ("http", null, 0);
        }

        var colon = target.LastIndexOf(':');
        return ("http", target[..colon].Trim('[', ']'), int.Parse(target[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<int> ReadByteAsync(NetworkStream stream) => (await ReadExactlyAsync(stream, 1))[0];

    private async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, _stop.Token);
        return buffer;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    /// <summary>One tunnel: the protocol, the name and port the client asked for, and what it carried each way.</summary>
    internal sealed class Tunnel(string protocol, string host, int port)
    {
        public string Protocol { get; } = protocol;

        public string Host { get; } = host;

        public int Port { get; } = port;

        public long BytesUp;

        public long BytesDown;

        public override string ToString() => $"{Protocol} {Host}:{Port} ({BytesUp} up, {BytesDown} down)";
    }
}
