using System.Net;

namespace KubeNimbus.Core;

/// <summary>
/// When a log pane follows a container again without being asked: only when the
/// container had not started when the follow was asked for, and has started since.
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
    /// True when a failed log request is the API server saying the container has not
    /// started yet: a 400 for a container that had not started when it was asked for. Any
    /// other failure (a 403, a 500, a dropped connection) is a verdict to show, not a wait.
    /// </summary>
    public static bool IsNotStartedRefusal(Exception error, ContainerRun? atStart) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.BadRequest } && !HasStarted(atStart);

    /// <summary>
    /// True when a follow that was refused or ended empty should be opened again now: the
    /// container had not started when it was asked for, and has started by the time the pod
    /// was read again.
    /// </summary>
    public static bool ShouldFollowAgain(ContainerRun? atStart, ContainerRun? now) =>
        !HasStarted(atStart) && HasStarted(now);
}
