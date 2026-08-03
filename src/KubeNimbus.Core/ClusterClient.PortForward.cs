using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using k8s;

namespace KubeNimbus.Core;

public sealed partial class ClusterClient
{
    /// <summary>
    /// Starts a local TCP listener that forwards each accepted connection to
    /// <paramref name="podPort"/> on the given pod. <paramref name="localPort"/>
    /// 0 picks an ephemeral port (read back from <see cref="PortForwardSession.LocalPort"/>
    /// once started). Matches kubectl's own approach: rather than multiplexing
    /// several local TCP clients over one upstream connection (which the k8s
    /// websocket port-forward channel framing doesn't support per port), a
    /// fresh upstream websocket is opened per accepted local connection.
    /// </summary>
    public PortForwardSession StartPortForward(string @namespace, string podName, int podPort, int localPort = 0) =>
        new(this, @namespace, podName, podPort, localPort);

    /// <summary>One port-forward websocket to the pod's port-forward subresource, single requested port.</summary>
    internal Task<WebSocket> OpenPortForwardWebSocketAsync(
        string @namespace, string podName, int podPort, CancellationToken cancellationToken) =>
        _client.WebSocketNamespacedPodPortForwardAsync(
            podName,
            @namespace,
            [podPort],
            WebSocketProtocol.V4BinaryWebsocketProtocol,
            new Dictionary<string, List<string>>(),
            cancellationToken);
}

/// <summary>
/// A running local-port → pod-port forward. <see cref="StartAsync"/> binds the
/// listener; dispose (or cancel the token passed to StartAsync) to stop
/// accepting and tear down any in-flight connections.
/// </summary>
public sealed class PortForwardSession(ClusterClient client, string @namespace, string podName, int podPort, int requestedLocalPort)
    : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;

    /// <summary>Channel 0 of the V4 framing carries data for the first (here: only) requested port.</summary>
    private const byte DataChannel = 0;

    /// <summary>Channel 1 carries the kubelet's error text for that same port.</summary>
    private const byte ErrorChannel = 1;

    /// <summary>One data + one error channel; a channel id outside this is not ours to route.</summary>
    private const int ChannelCount = 2;

    /// <summary>Width of the little-endian port number the kubelet writes first on each channel.</summary>
    private const int PortHeaderBytes = 2;

    private readonly List<Task> _connections = [];
    private readonly object _connectionsGate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    public int LocalPort { get; private set; }

    /// <summary>
    /// Raised (off the UI thread) when an accepted local connection's upstream
    /// forward fails — including when the kubelet declines it on the error
    /// channel ("error forwarding port 8080 to pod x: connection refused"),
    /// which is otherwise indistinguishable from a working forward.
    /// </summary>
    public event Action<Exception>? ConnectionFailed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("This port-forward session has already been started.");
        }

        var listener = new TcpListener(IPAddress.Loopback, requestedLocalPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // Bind failures are the one error the user can act on directly, and
            // a raw SocketException names neither the port nor the remedy.
            listener.Stop();
            throw new PortForwardException(
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Local port {requestedLocalPort} is already in use — choose a different local port."
                    : $"Could not listen on local port {requestedLocalPort}: {ex.Message}",
                ex);
        }

        // The CTS is created only once the listener is actually bound; creating
        // it first leaked an undisposed one on every failed bind.
        _listener = listener;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Tracked, not fire-and-forget: DisposeAsync used to return while
            // connections were still pumping against sockets it had just closed.
            var connection = HandleConnectionAsync(tcpClient, ct);
            lock (_connectionsGate)
            {
                _connections.RemoveAll(static t => t.IsCompleted);
                _connections.Add(connection);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken ct)
    {
        using var _ = tcpClient;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        WebSocket? ws = null;
        Exception? failure = null;
        try
        {
            ws = await client.OpenPortForwardWebSocketAsync(@namespace, podName, podPort, linked.Token).ConfigureAwait(false);
            using var netStream = tcpClient.GetStream();

            var toUpstream = ObservePumpAsync(PumpTcpToWebSocketAsync(netStream, ws, linked.Token));
            var toLocal = ObservePumpAsync(PumpWebSocketToTcpAsync(ws, netStream, linked.Token));

            var first = await Task.WhenAny(toUpstream, toLocal).ConfigureAwait(false);

            // Cancel before anything below disposes the stream or the socket:
            // the losing pump is still blocked in a read, and letting it wake up
            // on a disposed handle faults a task nobody is observing.
            await linked.CancelAsync().ConfigureAwait(false);
            var outcomes = await Task.WhenAll(toUpstream, toLocal).ConfigureAwait(false);

            // The kubelet's own explanation wins; otherwise take the pump that
            // finished first, since the other one only stopped because of the
            // cancellation two lines up.
            failure = outcomes.FirstOrDefault(static e => e is PortForwardException)
                ?? await first.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            ws?.Dispose();
        }

        if (failure is not null and not OperationCanceledException)
        {
            ConnectionFailed?.Invoke(failure);
        }
    }

    /// <summary>Runs a pump to completion and hands back its failure instead of throwing, so both can be awaited.</summary>
    private static async Task<Exception?> ObservePumpAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Local → pod: prefix each chunk with channel 0 (data, single requested
    /// port). Only the server side of the V4 protocol writes a port header, so
    /// nothing precedes the payload here.
    /// </summary>
    private static async Task PumpTcpToWebSocketAsync(NetworkStream net, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[BufferSize + 1];
        buffer[0] = DataChannel;
        while (!ct.IsCancellationRequested)
        {
            var read = await net.ReadAsync(buffer.AsMemory(1), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await ws.SendAsync(buffer.AsMemory(0, read + 1), WebSocketMessageType.Binary, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }

        // CloseOutputAsync, not CloseAsync: the local client going quiet is a
        // half-close, and CloseAsync would block this task on the peer's close
        // frame — which never arrives while the pod still has data to send.
        if (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The peer already went away; there is nothing left to tell it.
            }
        }
    }

    /// <summary>
    /// Pod → local. Every websocket <em>message</em> in the V4 framing starts
    /// with a 1-byte channel id: <see cref="DataChannel"/> carries bytes for the
    /// forwarded port, <see cref="ErrorChannel"/> the kubelet's error text.
    /// </summary>
    /// <remarks>
    /// Three things about this are easy to get wrong, and all three were:
    /// <list type="bullet">
    /// <item><b>The port header.</b> The kubelet writes the requested port as a
    /// 2-byte little-endian value as the first payload on each channel, so a
    /// channel's first message is protocol and carries no data. Skipping only
    /// the channel byte handed the local client two junk bytes — a forward to
    /// nginx began <c>P\0HTTP/1.1 200 OK</c> (<c>0x50 0x00</c> = port 80) and
    /// every HTTP, gRPC, Postgres and Redis client fell over on it.</item>
    /// <item><b>Message fragmentation.</b> The kubelet copies pod → client
    /// through a 32 KiB buffer, so one upstream write arrives as several
    /// partial receives against this 16 KiB one. Only the first fragment
    /// carries the channel id; treating each receive as a fresh message read a
    /// data byte as a channel id and dropped the whole chunk whenever it wasn't
    /// 0. Fragments are forwarded as they arrive rather than buffered — a
    /// stream must not wait on <see cref="ValueWebSocketReceiveResult.EndOfMessage"/>
    /// to move.</item>
    /// <item><b>The error channel.</b> It was dropped on the floor, so
    /// forwarding to a port nothing listens on looked exactly like success: the
    /// listener accepted, the client hung, and the app said nothing.</item>
    /// </list>
    /// </remarks>
    private static async Task PumpWebSocketToTcpAsync(WebSocket ws, NetworkStream net, CancellationToken ct)
    {
        var buffer = new byte[BufferSize + 1];
        var portHeaderSeen = new bool[ChannelCount];
        ArrayBufferWriter<byte>? errorBytes = null;
        byte channel = 0;
        var continuation = false;

        while (!ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var offset = 0;
            var count = result.Count;

            if (!continuation)
            {
                if (count == 0)
                {
                    continuation = !result.EndOfMessage;
                    continue;
                }

                channel = buffer[0];
                offset = 1;
                count--;

                if (channel < portHeaderSeen.Length && !portHeaderSeen[channel])
                {
                    portHeaderSeen[channel] = true;
                    var skip = Math.Min(PortHeaderBytes, count);
                    offset += skip;
                    count -= skip;
                }
            }

            continuation = !result.EndOfMessage;

            if (count > 0)
            {
                switch (channel)
                {
                    case DataChannel:
                        await net.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
                        break;
                    case ErrorChannel:
                        // Accumulated as bytes, decoded once the message ends, so
                        // a multi-byte character split across fragments survives.
                        (errorBytes ??= new ArrayBufferWriter<byte>()).Write(buffer.AsSpan(offset, count));
                        break;
                }
            }

            if (result.EndOfMessage && errorBytes is { WrittenCount: > 0 })
            {
                // The kubelet says this once and then drops the connection.
                // Surfacing it is the difference between "connection refused"
                // and a local port that accepts and answers nothing.
                throw new PortForwardException(Encoding.UTF8.GetString(errorBytes.WrittenSpan).Trim());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        // Safe to snapshot only now: the accept loop above has stopped, so
        // nothing more can be added to the list.
        Task[] pending;
        lock (_connectionsGate)
        {
            pending = [.. _connections];
            _connections.Clear();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connections dying as they are torn down is what a dispose looks
            // like; each already reported through ConnectionFailed if it mattered.
        }

        _cts?.Dispose();
    }
}

/// <summary>
/// A port-forward that could not be established or was refused. Raised through
/// <see cref="PortForwardSession.ConnectionFailed"/>, carrying either the local
/// bind failure or the kubelet's own error-channel text
/// ("error forwarding port 8080 to pod x: connection refused").
/// </summary>
public sealed class PortForwardException : Exception
{
    public PortForwardException(string message)
        : base(message)
    {
    }

    public PortForwardException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
