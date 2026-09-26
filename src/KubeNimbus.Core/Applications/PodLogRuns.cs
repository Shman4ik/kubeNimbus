namespace KubeNimbus.Core.Applications;

/// <summary>
/// One container run whose log the kubelet will actually serve. <see cref="Previous"/> is
/// the value of the <c>previous</c> query parameter that fetches it.
/// </summary>
/// <param name="EndedWith">
/// How the run ended, when it has — the log view closes such a run with an "exited with code
/// N (Reason) at T" line, because the last line a crashed process printed is rarely the
/// reason it stopped.
/// </param>
public sealed record LogRun(string Container, bool Previous, string Label, TerminatedState? EndedWith);

/// <summary>
/// Which of a container's runs can be read, following the kubelet's own
/// <c>validateContainerLogStatus</c> (<c>pkg/kubelet/kubelet_pods.go</c>) rather than the
/// intuition that "current" and "previous" are always two different runs.
/// </summary>
/// <remarks>
/// <para>
/// The kubelet keeps only the most recent terminated container per container name. With
/// <c>previous=true</c> it serves <c>lastState.terminated</c>; with <c>previous=false</c> it
/// serves the running container if there is one, else the terminated one, else — and this
/// is the case that matters — <c>lastState.terminated</c> again. So for a container
/// <em>waiting</em> in CrashLoopBackOff, <c>logs</c> and <c>logs --previous</c> return the
/// same run, and offering both as tabs would show the same bytes twice under two names. A
/// distinct earlier run exists only while the container is running (or has itself
/// terminated) again.
/// </para>
/// <para>
/// Both the terminated and the last-terminated branch also require a non-empty
/// <c>containerID</c>: an empty one is the kubelet saying it no longer has that container,
/// and it answers "is terminated" instead of a log.
/// </para>
/// </remarks>
public static class PodLogRuns
{
    public static IReadOnlyList<LogRun> For(ContainerFacts container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var last = container.LastTerminated is { ContainerId.Length: > 0 } l ? l : null;
        switch (container.State)
        {
            case ContainerStateKind.Running:
                return last is null
                    ? [new LogRun(container.Name, false, "Current run", null)]
                    : [new LogRun(container.Name, false, "Current run", null),
                       new LogRun(container.Name, true, "Run before", last)];

            case ContainerStateKind.Terminated when container.Terminated is { ContainerId.Length: > 0 } ended:
                return last is null
                    ? [new LogRun(container.Name, false, "Last run", ended)]
                    : [new LogRun(container.Name, false, "Last run", ended),
                       new LogRun(container.Name, true, "Run before", last)];

            // The kubelet's "next container didn't start" branch: an empty terminated id
            // falls back to lastState — one run, served by either request.
            case ContainerStateKind.Terminated when last is not null:
            case ContainerStateKind.Waiting when last is not null:
            case ContainerStateKind.Unknown when last is not null:
                return [new LogRun(container.Name, false, "Last run", last)];

            default:
                return [];
        }
    }

    /// <summary>
    /// Why a pod has no log to show, from the same status fields the kubelet itself answers
    /// with — never an empty panel (UI rule 9). Null when some container has a readable run.
    /// </summary>
    public static string? NoLogsReason(PodFacts pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        if (pod.Containers.Any(c => For(c).Count > 0))
        {
            return null;
        }

        if (pod.Unschedulable)
        {
            return "No logs: the pod has not been scheduled to a node, so no container has started."
                + (pod.ScheduledMessage.Length > 0 ? $" The scheduler says: {pod.ScheduledMessage}" : "");
        }

        if (pod.InitContainers.FirstOrDefault(c => c.State != ContainerStateKind.Terminated || c.Terminated is { ExitCode: not 0 }) is { } init
            && pod.Containers.All(c => c.State is ContainerStateKind.Waiting or ContainerStateKind.Unknown))
        {
            return init.State == ContainerStateKind.Waiting && init.WaitingReason.Length > 0
                ? $"No logs yet: init container {init.Name} has not finished ({init.WaitingReason})."
                : $"No logs yet: init container {init.Name} has not finished.";
        }

        if (pod.Containers.FirstOrDefault(c => c.State == ContainerStateKind.Waiting) is { } waiting)
        {
            return waiting.WaitingReason switch
            {
                "ErrImagePull" or "ImagePullBackOff" or "InvalidImageName" or "ErrImageNeverPull" =>
                    $"No logs: the image for {waiting.Name} cannot be pulled, so it has never started."
                    + (waiting.WaitingMessage.Length > 0 ? $" {waiting.WaitingMessage}" : ""),
                "CreateContainerConfigError" or "CreateContainerError" or "RunContainerError" =>
                    $"No logs: container {waiting.Name} could not be created ({waiting.WaitingReason})."
                    + (waiting.WaitingMessage.Length > 0 ? $" {waiting.WaitingMessage}" : ""),
                "ContainerCreating" or "PodInitializing" =>
                    $"No logs yet: container {waiting.Name} is being created.",
                { Length: > 0 } reason => $"No logs yet: container {waiting.Name} is waiting to start ({reason}).",
                _ => $"No logs yet: container {waiting.Name} is waiting to start.",
            };
        }

        return pod.Phase switch
        {
            "Pending" => "No logs yet: the pod is Pending and the kubelet has not started its containers.",
            "Succeeded" or "Failed" => $"No logs: the pod has {pod.Phase.ToLowerInvariant()} and the kubelet no longer holds its containers.",
            _ => "No logs: the kubelet reports no container run for this pod.",
        };
    }
}
