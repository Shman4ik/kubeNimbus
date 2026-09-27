using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What to say when a followed log stream ends without an error. The API server closes a
/// follow when the container exits — but also when a load balancer or proxy drops an idle
/// connection, or the server restarts, and the pane used to say "Stream ended — nginx
/// exited." for all of them. The difference is read from the pod rather than assumed: the
/// same container id still running means the connection went, not the container.
/// </summary>
public static class LogStreamEnd
{
    /// <summary>
    /// How long to wait before reading the pod. The kubelet closes the stream as the
    /// container exits and reports the exit on a later status sync, about a second on;
    /// reading at once would find the ended run still "running" and blame the connection.
    /// </summary>
    internal static TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads the pod after <see cref="SettleDelay"/> and says why the stream ended.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="token"/> is
    /// cancelled — the pane has moved on and nothing should be written.
    /// </summary>
    public static async Task<(string Text, bool Problem)> ExplainAsync(
        ClusterClient client, string @namespace, string podName, string container,
        ContainerRun? atStart, CancellationToken token)
    {
        await Task.Delay(SettleDelay, token).ConfigureAwait(false);
        DynamicResource? pod;
        try
        {
            pod = await client.ReadResourceAsync(ResourceDescriptor.Pods, @namespace, podName, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex.Message;
            var end = message.IndexOfAny(['\r', '\n']);
            return Describe(container, atStart, null, readError: end < 0 ? message : message[..end]);
        }

        return pod is null
            ? Describe(container, atStart, null, podGone: true)
            : Describe(container, atStart, PodDetails.ContainerRunOf(pod.Raw, container));
    }

    /// <summary>Shown between the stream ending and the pod being read.</summary>
    public static string Checking(string container) =>
        $"Stream ended — checking whether {container} is still running…";

    /// <param name="container">The container whose logs were being followed.</param>
    /// <param name="atStart">Its run when the follow started, from the watched pod; null if unknown.</param>
    /// <param name="now">Its run as read after the stream ended; null if the pod reports none.</param>
    /// <param name="podGone">The pod no longer exists.</param>
    /// <param name="readError">The pod could not be read, and the server's reason.</param>
    public static (string Text, bool Problem) Describe(
        string container, ContainerRun? atStart, ContainerRun? now, bool podGone = false, string? readError = null)
    {
        if (podGone)
        {
            return ("Stream ended — the pod is gone.", false);
        }

        if (readError is not null)
        {
            return ($"Stream ended, and the pod could not be read to say why: {readError}", true);
        }

        return now switch
        {
            null or { State: ContainerRunState.Unknown } =>
                ($"Stream ended — the pod reports no state for {container}.", false),

            { State: ContainerRunState.Running } when atStart?.ContainerId is { } startId
                && now.ContainerId is { } nowId && startId != nowId =>
                ($"Stream ended — {container} restarted (restart {now.RestartCount}). Follow picks up the new run; Previous shows the one that ended.", false),

            { State: ContainerRunState.Running } =>
                ($"The connection closed, not the container — {container} is still running. Follow resumes the stream.", true),

            { State: ContainerRunState.Terminated } =>
                ($"Stream ended — {container} exited{Detail(now)}.", false),

            // No container id at all: the runtime has never created it (Pending, pulling,
            // ContainerCreating), so there is nothing that could have exited.
            { State: ContainerRunState.Waiting, ContainerId: null or "" } =>
                ($"{container} has not started yet{Detail(now)} — there is no log to follow until it does.", false),

            { State: ContainerRunState.Waiting } =>
                ($"Stream ended — {container} is not running{Detail(now)}.", false),

            _ => ("Stream ended.", false),
        };
    }

    // "(Error, exit code 1)", "(CrashLoopBackOff)", "(exit code 0)", or nothing.
    private static string Detail(ContainerRun run)
    {
        var parts = new List<string>(2);
        if (run.Reason is { Length: > 0 } reason) parts.Add(reason);
        if (run.ExitCode is { } code) parts.Add($"exit code {code}");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }
}
