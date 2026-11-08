using System.Net;

namespace KubeNimbus.Core;

/// <summary>
/// When a log pane follows a container again without being asked: only when the
/// container had not started when the follow was asked for, and has started since. The
/// request asked again is the one that was refused: a Follow-off range load is loaded as a
/// range, never turned into a live follow (#331). And when a refused request is the end of
/// its pod rather than a failure (<see cref="IsPodGone"/>, #332).
/// Both log panes use it, and so does <c>Live/WorkloadLogsLiveTests</c>, so the rule the
/// live cluster is checked against is the one the panes run.
/// </summary>
/// <remarks>
/// <para>
/// A follow asked of a container that has not started is refused by the API server with
/// a 400 in its own words: <c>container "app" in pod "x" is not available</c> while the
/// pod is scheduled but the kubelet has not reported the container yet, and
/// <c>container "app" in pod "x" is waiting to start: ContainerCreating</c> while it is
/// being created. Both panes used to treat that as the end: the multi-pod pane waited for
/// the pod's next Modified event, which under load had already come and gone (#309), and
/// pod detail did not wait at all, so a pod whose scheduling gate was released sat on
/// "is not available (400 Bad Request)" while it ran (#184, the 0.7.0 QA run's G-13).
/// </para>
/// <para>
/// It cannot loop: a follow opened on a container that has started is never a
/// not-started refusal, so the follow it leads to is never retried this way.
/// </para>
/// </remarks>
public static class LogFollowRetry
{
    /// <summary>
    /// True when the container has a run whose log can be read: running, ended, or waiting
    /// with a container id (a crash loop between runs). A container with no status, or
    /// waiting with no container id (pending, pulling, ContainerCreating), has none yet.
    /// </summary>
    public static bool HasStarted(ContainerRun? run) =>
        run is { State: ContainerRunState.Running or ContainerRunState.Terminated }
        || run is { ContainerId.Length: > 0 };

    /// <summary>
    /// True when a failed log request means the container has not started yet, for a
    /// container that had not started when it was asked for. That is the API server's 400,
    /// or the kubelet's 404 relayed by the API server
    /// (<see cref="IsRelayedKubeletNotFound"/>). Any other failure (a 403, a 500, the API
    /// server's own 404, a dropped connection) is a verdict to show, not a wait.
    /// </summary>
    /// <remarks>
    /// The 404 is #418. <c>GatedPodLogsLiveTests</c> met it in every full live run of 2026-11-08,
    /// and never when the test ran alone. The sequence is a gated pod released, then its
    /// log asked for while it comes up. Under load, the API server forwarded the request to
    /// the kubelet before the kubelet had the container, and relayed the kubelet's 404:
    /// <c>the server could not find the requested resource ( pods/log x)</c>. A pane treated
    /// that as a failure, so a pod opened in the moment it started read as an error beside a
    /// running pod. Requiring a container that had not started keeps it from looping: the
    /// follow it leads to is asked of a started container, and that 404 stays a failure.
    /// </remarks>
    public static bool IsNotStartedRefusal(Exception error, ContainerRun? atStart) =>
        !HasStarted(atStart)
        && error is HttpRequestException { StatusCode: var code } refusal
        && (code == HttpStatusCode.BadRequest || (code == HttpStatusCode.NotFound && IsRelayedKubeletNotFound(refusal)));

    /// <summary>
    /// The kubelet's 404 as the API server relays it, <c>the server could not find the
    /// requested resource ( pods/log x)</c>. The API server's own 404,
    /// <c>pods "x" not found</c>, means the pod object is gone, and is never this.
    /// </summary>
    internal static bool IsRelayedKubeletNotFound(HttpRequestException error) =>
        error.Message.Contains("could not find the requested resource", StringComparison.Ordinal)
        && error.Message.Contains("pods/log", StringComparison.Ordinal);

    /// <summary>
    /// True when a follow that was refused or ended empty should be opened again now: the
    /// container had not started when it was asked for, and has started by the time the pod
    /// was read again.
    /// </summary>
    public static bool ShouldFollowAgain(ContainerRun? atStart, ContainerRun? now) =>
        !HasStarted(atStart) && HasStarted(now);

    /// <summary>
    /// True when a log request answered 404 is the pod going away, the stated end of its
    /// source rather than a failure (#332): the pod as read after the 404 is gone, or is
    /// being deleted. A 404 comes in two shapes. The API server's own,
    /// <c>pods "x" not found</c>, once the pod object is deleted; and the kubelet's, relayed
    /// as <c>the server could not find the requested resource ( pods/log x)</c>, when the
    /// node no longer has a pod whose status still says its container ran. The API server
    /// answers a container that has not run from the pod's status, with the 400 that
    /// <see cref="IsNotStartedRefusal"/> reads. The kubelet's 404 for a container that had
    /// not started when it was asked for is a pod coming up (#418), and
    /// <see cref="IsNotStartedRefusal"/> takes it before this is asked. A 404 for a pod that
    /// is still there and not being deleted is not this, and stays a failure.
    /// </summary>
    public static bool IsPodGone(Exception error, DynamicResource? podNow) =>
        IsNotFound(error) && (podNow is null || IsBeingDeleted(podNow));

    private static bool IsBeingDeleted(DynamicResource pod) =>
        pod.Raw.ValueKind == System.Text.Json.JsonValueKind.Object
        && pod.Raw.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == System.Text.Json.JsonValueKind.Object
        && metadata.TryGetProperty("deletionTimestamp", out var deleting)
        && deleting.ValueKind == System.Text.Json.JsonValueKind.String;

    /// <summary>
    /// True when a log request failed with a 404, so the pane should read the pod before
    /// deciding (<see cref="IsPodGone"/>) whether the source has ended or failed.
    /// </summary>
    public static bool IsNotFound(Exception error) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.NotFound };
}
