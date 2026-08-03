using System.Text;
using System.Text.Json;
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

        // Channel 3 must be opened here, not on demand: StreamDemuxer only
        // buffers channels a stream has been taken for and silently discards
        // the rest, so a session that asks later has already missed the status.
        var error = demuxer.GetStream(ChannelIndex.Error, null);

        return new ExecSession(demuxer, stdIn, stdOut, stdErr, error);
    }
}

/// <summary>
/// One live exec session. Dispose (or cancel the token passed to
/// <see cref="ClusterClient.ExecAsync"/>) to end it — closing the underlying
/// WebSocket. <see cref="ResizeAsync"/> only matters when the session was
/// opened with tty=true.
/// </summary>
public sealed class ExecSession(IStreamDemuxer demuxer, Stream stdIn, Stream stdOut, Stream? stdErr, Stream errorStream)
    : IDisposable
{
    public Stream StdIn { get; } = stdIn;

    public Stream StdOut { get; } = stdOut;

    /// <summary>Null when the session was opened with tty=true (stderr is merged into stdout for a PTY).</summary>
    public Stream? StdErr { get; } = stdErr;

    /// <summary>
    /// The API server's error channel (channel 3) — <b>not</b> the process's
    /// stderr. It carries exactly one <c>metav1.Status</c> document describing
    /// how the exec itself ended, then EOF.
    /// </summary>
    /// <remarks>
    /// Most callers want <see cref="ReadTerminalStatusAsync"/> instead. This is
    /// the raw stream for anything that needs the document itself.
    /// </remarks>
    public Stream ErrorStream { get; } = errorStream;

    /// <summary>
    /// Awaits the session's terminal status and returns the reason it failed, or
    /// null when it ended cleanly. Completes when the session ends, so callers
    /// start it alongside the stdout pump rather than awaiting it up front.
    /// </summary>
    /// <remarks>
    /// The websocket upgrade succeeding says nothing about the command: an image
    /// with no shell answers <c>OCI runtime exec failed: exec: "/bin/sh": stat
    /// /bin/sh: no such file or directory</c> and a non-zero exit answers
    /// <c>command terminated with exit code 126</c> — both on this channel and
    /// on neither stdout nor stderr. Without reading it, the most common exec
    /// failure there is presents as a connected session over a permanently blank
    /// terminal, which reads as the app being broken.
    /// </remarks>
    public async Task<string?> ReadTerminalStatusAsync(CancellationToken cancellationToken = default)
    {
        using var payload = new MemoryStream();
        await ErrorStream.CopyToAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseTerminalStatus(payload.ToArray());
    }

    /// <summary>
    /// Reads the <c>metav1.Status</c> the error channel carries: null when it
    /// says Success (or said nothing at all), otherwise its <c>message</c>.
    /// </summary>
    /// <remarks>
    /// Parsed with <see cref="JsonDocument"/> for the same reason watch frames
    /// are — AOT-safe, and the shape is one field deep. A payload that isn't
    /// JSON is handed back verbatim: an unparseable reason is still a reason,
    /// and inventing "unknown error" in its place helps nobody.
    /// </remarks>
    internal static string? ParseTerminalStatus(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return text;
            }

            if (root.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "Success", StringComparison.Ordinal))
            {
                return null;
            }

            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"Width":{{columns}},"Height":{{rows}}}""");
        return demuxer.Write(ChannelIndex.Resize, payload, 0, payload.Length, cancellationToken);
    }

    public void Dispose() => (demuxer as IDisposable)?.Dispose();
}
