using System.Text.Json;

namespace KubeNimbus.Core.Applications;

/// <summary>Which of the three container states the kubelet reported.</summary>
public enum ContainerStateKind
{
    Unknown,
    Waiting,
    Running,
    Terminated,
}

/// <summary>
/// One <c>terminated</c> block — a container's current state or its
/// <c>lastState</c>. <see cref="ContainerId"/> matters more than it looks: the kubelet
/// serves a terminated run's logs only while it still has that container, and an empty id
/// is its signal that it does not.
/// </summary>
public sealed record TerminatedState(
    int? ExitCode,
    string Reason,
    string Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string ContainerId,
    int? Signal)
{
    /// <summary>"exit 1 (Error)" — the words every rule and every log footer uses.</summary>
    public string ExitText =>
        (ExitCode is { } code ? $"exit {code}" : "no exit code")
        + (Reason.Length > 0 ? $" ({Reason})" : "");

    internal static TerminatedState? Read(JsonElement terminated) =>
        terminated.ValueKind != JsonValueKind.Object
            ? null
            : new TerminatedState(
                J.Int(terminated, "exitCode"),
                J.Str(terminated, "reason"),
                J.Str(terminated, "message"),
                J.Time(terminated, "startedAt"),
                J.Time(terminated, "finishedAt"),
                J.Str(terminated, "containerID"),
                J.Int(terminated, "signal"));
}

/// <summary>
/// One container, as its status reports it, joined with the two things from its spec the
/// rules quote: the image and the memory limit.
/// </summary>
public sealed record ContainerFacts(
    string Name,
    bool IsInit,
    bool Ready,
    int RestartCount,
    ContainerStateKind State,
    string WaitingReason,
    string WaitingMessage,
    DateTimeOffset? RunningSince,
    TerminatedState? Terminated,
    TerminatedState? LastTerminated,
    string Image,
    string? MemoryLimit,
    bool HasReadinessProbe)
{
    /// <summary>Waiting reasons that mean the container cannot run as it is, not that it is on its way.</summary>
    public static readonly IReadOnlySet<string> FailingWaitingReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ErrImagePull", "ImagePullBackOff", "InvalidImageName",
        "CreateContainerConfigError", "CreateContainerError", "RunContainerError",
        "ErrImageNeverPull", "PreCreateHookError", "PreStartHookError", "PostStartHookError",
    };

    public bool IsCrashLooping => State == ContainerStateKind.Waiting && WaitingReason == "CrashLoopBackOff";

    public bool IsFailing =>
        (State == ContainerStateKind.Waiting && FailingWaitingReasons.Contains(WaitingReason))
        || (State == ContainerStateKind.Terminated && !IsInit && Terminated is { ExitCode: not (null or 0) });

    /// <summary>The run that most recently ended, current or last — what an exit code is quoted from.</summary>
    public TerminatedState? LatestTermination =>
        Terminated is { FinishedAt: { } current }
        && (LastTerminated?.FinishedAt is not { } last || current >= last)
            ? Terminated
            : LastTerminated ?? Terminated;
}

/// <summary>A pod's state reduced to the one square the Applications list draws for it.</summary>
public enum PodDisplayState
{
    /// <summary>Running and Ready.</summary>
    Ready,

    /// <summary>Running, not Ready.</summary>
    NotReady,

    /// <summary>Not started yet — scheduling, pulling, initializing.</summary>
    Pending,

    /// <summary>A container cannot run: crash loop, image pull, config error, non-zero exit.</summary>
    Failing,

    /// <summary>Finished with success (a Job's pod).</summary>
    Succeeded,

    /// <summary>Finished with failure (a Job's pod).</summary>
    Failed,

    /// <summary>Being deleted.</summary>
    Terminating,
}

/// <summary>
/// The facts about one pod the Applications rules read: its phase, its readiness, whether
/// the scheduler has placed it, and every container's state. Read once per pod per
/// evaluation, from the object's own status — no Events are needed for anything here.
/// </summary>
public sealed record PodFacts(
    DynamicResource Pod,
    string Name,
    string Namespace,
    string Phase,
    bool Ready,
    DateTimeOffset? ReadySince,
    bool Terminating,
    bool Unschedulable,
    string ScheduledMessage,
    IReadOnlyList<ContainerFacts> Containers,
    IReadOnlyList<ContainerFacts> InitContainers,
    DateTimeOffset? Created,
    string NodeName)
{
    public static PodFacts Read(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var spec = J.Obj(pod.Raw, "spec");
        var status = J.Obj(pod.Raw, "status");
        var metadata = J.Obj(pod.Raw, "metadata");

        var ready = J.Condition(status, "Ready");
        var scheduled = J.Condition(status, "PodScheduled");

        return new PodFacts(
            pod,
            pod.Name,
            pod.Namespace ?? "",
            J.Str(status, "phase"),
            J.Str(ready, "status") == "True",
            J.Time(ready, "lastTransitionTime"),
            J.Has(metadata, "deletionTimestamp"),
            J.Str(scheduled, "status") == "False" && J.Str(scheduled, "reason") == "Unschedulable",
            J.Str(scheduled, "message"),
            ReadContainers(spec, status, "containers", "containerStatuses", isInit: false),
            ReadContainers(spec, status, "initContainers", "initContainerStatuses", isInit: true),
            pod.CreationTimestamp,
            J.Str(spec, "nodeName"));
    }

    private static IReadOnlyList<ContainerFacts> ReadContainers(
        JsonElement spec, JsonElement status, string specArray, string statusArray, bool isInit)
    {
        var statuses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in J.Arr(status, statusArray))
        {
            statuses[J.Str(item, "name")] = item;
        }

        // Spec order first (it is the order kubectl prints and the one "the first app
        // container" means), then any status the spec does not declare — a trimmed object
        // still says what state its containers are in.
        var containers = J.Arr(spec, specArray).ToList();
        var declared = containers.Select(c => J.Str(c, "name")).ToHashSet(StringComparer.Ordinal);
        foreach (var name in statuses.Keys.Where(n => !declared.Contains(n)))
        {
            using var stub = JsonDocument.Parse($$"""{"name":{{JsonSerializerString(name)}}}""");
            containers.Add(stub.RootElement.Clone());
        }

        var result = new List<ContainerFacts>();
        foreach (var container in containers)
        {
            var name = J.Str(container, "name");
            statuses.TryGetValue(name, out var containerStatus);
            var state = J.Obj(containerStatus, "state");
            var waiting = J.Obj(state, "waiting");
            var running = J.Obj(state, "running");
            var terminated = J.Obj(state, "terminated");
            var memory = J.Str(J.Obj(container, "resources", "limits"), "memory");

            result.Add(new ContainerFacts(
                name,
                isInit,
                J.Bool(containerStatus, "ready"),
                J.Int(containerStatus, "restartCount") ?? 0,
                waiting.ValueKind == JsonValueKind.Object ? ContainerStateKind.Waiting
                : running.ValueKind == JsonValueKind.Object ? ContainerStateKind.Running
                : terminated.ValueKind == JsonValueKind.Object ? ContainerStateKind.Terminated
                : ContainerStateKind.Unknown,
                J.Str(waiting, "reason"),
                J.Str(waiting, "message"),
                J.Time(running, "startedAt"),
                TerminatedState.Read(terminated),
                TerminatedState.Read(J.Obj(J.Obj(containerStatus, "lastState"), "terminated")),
                J.Str(container, "image"),
                memory.Length == 0 ? null : memory,
                J.Has(container, "readinessProbe")));
        }

        return result;
    }

    private static string JsonSerializerString(string value) =>
        "\"" + System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(value) + "\"";

    /// <summary>Restarts of the app containers — what the list's Restarts column sums.</summary>
    public int Restarts => Containers.Sum(c => c.RestartCount);

    /// <summary>Whether this pod still counts toward what the app is serving (not finished, not going away).</summary>
    public bool IsLive => !Terminating && Phase is not ("Succeeded" or "Failed");

    public PodDisplayState DisplayState
    {
        get
        {
            if (Terminating)
            {
                return PodDisplayState.Terminating;
            }

            switch (Phase)
            {
                case "Succeeded":
                    return PodDisplayState.Succeeded;
                case "Failed":
                    return PodDisplayState.Failed;
            }

            if (Containers.Any(c => c.IsFailing) || InitContainers.Any(c => c.IsFailing))
            {
                return PodDisplayState.Failing;
            }

            if (Unschedulable || Phase == "Pending")
            {
                return PodDisplayState.Pending;
            }

            return Ready ? PodDisplayState.Ready : PodDisplayState.NotReady;
        }
    }

    /// <summary>What a pod row says about its state, in the words the kubelet used.</summary>
    public string StateText
    {
        get
        {
            if (Terminating)
            {
                return "Terminating";
            }

            if (Containers.Concat(InitContainers).FirstOrDefault(c => c.IsFailing) is { } failing)
            {
                return failing.State == ContainerStateKind.Waiting
                    ? failing.WaitingReason
                    : $"Exited, {failing.Terminated!.ExitText}";
            }

            if (Unschedulable)
            {
                return "Pending: Unschedulable";
            }

            if (Phase == "Pending")
            {
                var waiting = InitContainers.Concat(Containers).FirstOrDefault(c => c.WaitingReason.Length > 0);
                return waiting is null ? "Pending" : $"Pending: {waiting.WaitingReason}";
            }

            if (Phase == "Running")
            {
                return Ready ? "Running" : "Running, not Ready";
            }

            return Phase.Length == 0 ? "Unknown" : Phase;
        }
    }
}
