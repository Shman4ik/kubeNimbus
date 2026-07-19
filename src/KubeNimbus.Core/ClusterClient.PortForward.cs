using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int LocalPort { get; private set; }

    /// <summary>Raised (off the UI thread) when an accepted local connection's upstream forward fails.</summary>
    public event Action<Exception>? ConnectionFailed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, requestedLocalPort);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
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

            _ = HandleConnectionAsync(tcpClient, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken ct)
    {
        using var _ = tcpClient;
        WebSocket? ws = null;
        try
        {
            ws = await client.OpenPortForwardWebSocketAsync(@namespace, podName, podPort, ct).ConfigureAwait(false);
            using var netStream = tcpClient.GetStream();

            var toUpstream = PumpTcpToWebSocketAsync(netStream, ws, ct);
            var toLocal = PumpWebSocketToTcpAsync(ws, netStream, ct);
            await Task.WhenAny(toUpstream, toLocal).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConnectionFailed?.Invoke(ex);
        }
        finally
        {
            ws?.Dispose();
        }
    }

    /// <summary>Local → pod: prefix each chunk with channel 0 (data, single requested port).</summary>
    private static async Task PumpTcpToWebSocketAsync(NetworkStream net, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[BufferSize + 1];
        buffer[0] = 0;
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

        if (ws.State == WebSocketState.Open)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Pod → local: first byte is the channel (0 = data for this port, 1 = error); data is forwarded, errors dropped.</summary>
    private static async Task PumpWebSocketToTcpAsync(WebSocket ws, NetworkStream net, CancellationToken ct)
    {
        var buffer = new byte[BufferSize + 1];
        while (!ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.Count <= 1 || buffer[0] != 0)
            {
                continue;
            }

            await net.WriteAsync(buffer.AsMemory(1, result.Count - 1), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
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

        _cts.Dispose();
    }
}
