using System.Text.Json;
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
    public static async Task<(string Text, bool Problem, bool NotStarted)> ExplainAsync(
        ClusterClient client, string @namespace, string podName, string container,
        ContainerRun? atStart, CancellationToken token)
    {
        var (text, problem, notStarted, _) = await ExplainWithRunAsync(
            client, @namespace, podName, container, atStart, token).ConfigureAwait(false);
        return (text, problem, notStarted);
    }

    /// <summary>
    /// <see cref="ExplainAsync"/>, plus the container's run as read — so a caller can act
    /// on <see cref="StartedAfterRequest"/> rather than only print the sentence.
    /// </summary>
    public static async Task<(string Text, bool Problem, bool NotStarted, ContainerRun? Now)> ExplainWithRunAsync(
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
            var (text, problem) = Describe(container, atStart, null, readError: end < 0 ? message : message[..end]);
            return (text, problem, false, null);
        }

        if (pod is null)
        {
            var (text, problem) = Describe(container, atStart, null, podGone: true);
            return (text, problem, false, null);
        }

        var (said, isProblem, notStarted) = DescribePod(pod.Raw, container, atStart);
        return (said, isProblem, notStarted, PodDetails.ContainerRunOf(pod.Raw, container));
    }

    /// <summary>
    /// The verdict for a pod object already in hand — what <see cref="ExplainAsync"/> says
    /// once its read returns, and what the demo cluster says about its own dataset, so a
    /// demo pod that never started reads exactly as a real one would (ENG-45).
    /// <c>NotStarted</c> is true when the container has never run at all — the multi-pod
    /// pane's chip then reads "not started" rather than "ended", which beside a sentence
    /// saying it never started was the contradiction ENG-45 is about.
    /// </summary>
    public static (string Text, bool Problem, bool NotStarted) DescribePod(
        JsonElement pod, string container, ContainerRun? atStart)
    {
        var now = PodDetails.ContainerRunOf(pod, container);

        // No status for the container and no node: the scheduler has not placed the pod, so
        // no runtime has ever been asked to create the container. The API server answers a
        // follow request for such a pod with an immediate 204 No Content (read on the k3s
        // 1.33 sandbox), which is what ended the stream, and "reports no state" would leave
        // the reader guessing why.
        if (now is null && !IsScheduled(pod))
        {
            var reason = UnscheduledReason(pod);
            return ($"{container} has not started — the pod is not scheduled onto a node yet"
                + (reason is null ? "" : $" ({reason})")
                + ", so there is no log to read.", false, true);
        }

        var (text, problem) = Describe(container, atStart, now);
        return (text, problem, now is { State: ContainerRunState.Waiting, ContainerId: null or "" });
    }

    private static bool IsScheduled(JsonElement pod) =>
        pod.ValueKind == JsonValueKind.Object
        && pod.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("nodeName", out var node) && node.ValueKind == JsonValueKind.String
        && node.GetString() is { Length: > 0 };

    /// <summary>The PodScheduled condition's reason when it is False ("Unschedulable").</summary>
    private static string? UnscheduledReason(JsonElement pod) =>
        pod.ValueKind == JsonValueKind.Object && pod.TryGetProperty("status", out var status)
            ? PodDetails.Conditions(status)
                .FirstOrDefault(c => c is { Type: "PodScheduled", Status: "False" } && c.Reason.Length > 0)?.Reason
            : null;

    /// <summary>
    /// True when the follow was opened before the container had started and it is running
    /// now — so the stream that ended was never this run's, and following again is the
    /// whole remedy.
    /// </summary>
    /// <remarks>
    /// Observed against a real kubelet (k3s v1.33, <c>WorkloadLogsLiveTests</c>): a follow
    /// requested in the moment between a container being created and started is answered
    /// 200 with an empty body that closes at once, because the kubelet ends a follow when it
    /// reaches the end of the log of a container that is not running. During a rollout the
    /// multi-pod pane opens its stream the instant the watch reports a new pod, so it hits
    /// that window; left alone, the new pod sat in the strip with no lines and a sentence
    /// blaming a dropped connection.
    /// </remarks>
    public static bool StartedAfterRequest(ContainerRun? atStart, ContainerRun? now) =>
        now is { State: ContainerRunState.Running }
        && atStart is not { State: ContainerRunState.Running };

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
