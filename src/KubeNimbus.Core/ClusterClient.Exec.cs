using System.Text;
using k8s;

namespace KubeNimbus.Core;

/// <summary>
/// Interactive exec into a container. <see cref="Kubernetes.MuxedStreamNamespacedPodExecAsync"/>
/// is the one exec/attach helper KubernetesClient.Aot does ship (it's WebSocket-
/// based, unlike SPDY, so it needed no reflection-based transport) — it reuses
/// the client's own auth exactly like the manual watch/log requests elsewhere
/// in this class.
/// </summary>
public sealed partial class ClusterClient
{
    public async Task<ExecSession> ExecAsync(
        string @namespace,
        string podName,
        string container,
        IReadOnlyList<string> command,
        bool tty = true,
        CancellationToken cancellationToken = default)
    {
        var demuxer = await _client.MuxedStreamNamespacedPodExecAsync(
            podName,
            @namespace,
            command,
            container,
            stderr: !tty,
            stdin: true,
            stdout: true,
            tty: tty,
            webSocketSubProtocol: WebSocketProtocol.V4BinaryWebsocketProtocol,
            customHeaders: new Dictionary<string, List<string>>(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        demuxer.Start();

        var stdIn = demuxer.GetStream(null, ChannelIndex.StdIn);
        var stdOut = demuxer.GetStream(ChannelIndex.StdOut, null);
        var stdErr = tty ? null : demuxer.GetStream(ChannelIndex.StdErr, null);

        return new ExecSession(demuxer, stdIn, stdOut, stdErr);
    }
}

/// <summary>
/// One live exec session. Dispose (or cancel the token passed to
/// <see cref="ClusterClient.ExecAsync"/>) to end it — closing the underlying
/// WebSocket. <see cref="ResizeAsync"/> only matters when the session was
/// opened with tty=true.
/// </summary>
public sealed class ExecSession(IStreamDemuxer demuxer, Stream stdIn, Stream stdOut, Stream? stdErr) : IDisposable
{
    public Stream StdIn { get; } = stdIn;

    public Stream StdOut { get; } = stdOut;

    /// <summary>Null when the session was opened with tty=true (stderr is merged into stdout for a PTY).</summary>
    public Stream? StdErr { get; } = stdErr;

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"Width":{{columns}},"Height":{{rows}}}""");
        return demuxer.Write(ChannelIndex.Resize, payload, 0, payload.Length, cancellationToken);
    }

    public void Dispose() => (demuxer as IDisposable)?.Dispose();
}
