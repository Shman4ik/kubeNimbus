namespace KubeNimbus.Core.Applications;

/// <summary>
/// One application's verdict. The declaration order <em>is</em> the list's order: needs
/// attention first (Degraded through OutOfSync), then everything else. Sync and health are
/// folded into one word here only because the list sorts on one key; the page still shows
/// Argo's sync as its own pill.
/// </summary>
public enum AppStatus
{
    /// <summary>Something the app needs is failing: crash loops, image pulls, failed Jobs, pods that cannot be created or placed.</summary>
    Degraded,

    /// <summary>Argo CD declares resources the cluster does not have.</summary>
    Missing,

    /// <summary>Argo CD's last sync operation failed or errored.</summary>
    SyncFailed,

    /// <summary>The app's state cannot be read — Argo cannot compare, or reports Unknown.</summary>
    Unknown,

    /// <summary>A rollout stopped making progress (ProgressDeadlineExceeded).</summary>
    Stalled,

    /// <summary>The cluster differs from Git and nothing is syncing it.</summary>
    OutOfSync,

    /// <summary>A rollout or sync is in flight — the system working, not a problem.</summary>
    Progressing,

    /// <summary>Deliberately not running: a suspended CronJob, a paused rollout, Argo Suspended.</summary>
    Suspended,

    Healthy,
}

public static class AppStatusExtensions
{
    /// <summary>Whether the list files this under "Needs attention".</summary>
    public static bool NeedsAttention(this AppStatus status) => status <= AppStatus.OutOfSync;

    /// <summary>The pill's word.</summary>
    public static string Label(this AppStatus status) => status switch
    {
        AppStatus.SyncFailed => "Sync failed",
        _ => status.ToString(),
    };
}

public enum FindingSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// One piece of evidence behind a finding: the exact field (or event) it was read from and
/// the value it held, printed as <c>field: value</c>. Quoting rather than paraphrasing is
/// the point — the reader can check it against <c>kubectl get -o yaml</c> and get the same
/// answer, which is what makes the page trustworthy under pressure.
/// </summary>
public sealed record Evidence(string Field, string Value)
{
    public string Text => Value.Length == 0 ? Field : $"{Field}: {Value}";

    public override string ToString() => Text;
}

/// <summary>
/// A fact the rules found. Not a diagnosis: <see cref="Title"/> says what the cluster
/// reports, <see cref="Detail"/> says it in a sentence, and <see cref="Evidence"/> quotes
/// where it came from.
/// </summary>
/// <param name="Rule">Stable rule id — what the tests pin and what the UI keys styles on.</param>
/// <param name="Short">The few words the list's reason line uses for this finding.</param>
/// <param name="Implies">The status this finding pushes the app toward; the app takes the most urgent.</param>
/// <param name="Pod">The pod the finding names, when it names one — the page preselects it.</param>
public sealed record Finding(
    string Rule,
    FindingSeverity Severity,
    string Title,
    string Detail,
    IReadOnlyList<Evidence> Evidence,
    string Short,
    AppStatus Implies,
    string? Pod = null,
    string? Container = null);

/// <summary>One square in the Pods column: a pod, or a replica that was never created.</summary>
/// <param name="Name">Empty for a replica the controller could not create.</param>
public sealed record PodMark(string Name, PodDisplayState? State, string Text)
{
    /// <summary>True for a desired replica with no pod behind it — drawn hollow.</summary>
    public bool IsMissing => State is null;
}

/// <summary>The most recent deploy the cluster can date: an Argo sync, or a ReplicaSet's creation.</summary>
/// <param name="Revision">Short SHA, chart version, or <c>rev N</c>.</param>
public sealed record DeployMark(string Revision, DateTimeOffset? At, string Source);

/// <summary>What the rules concluded about one application.</summary>
public sealed record ApplicationAssessment(
    AppStatus Status,
    string Reason,
    IReadOnlyList<Finding> Findings,
    int Ready,
    int Desired,
    int Restarts,
    DateTimeOffset? LastRestartAt,
    IReadOnlyList<PodMark> Pods,
    DeployMark? LastDeploy)
{
    /// <summary>"1/4", or "—" for an app with nothing that runs replicas (a CronJob between runs, an Argo app with no workload here).</summary>
    public string PodsText => Desired < 0 ? "—" : $"{Ready}/{Desired}";
}

/// <summary>
/// Everything the rules read for one application: its workloads and what they own, plus the
/// Argo Application when one tracks them. Events are optional — the list is built without
/// them (status alone is enough for a verdict and a reason), and the page adds them.
/// </summary>
public sealed record ApplicationInput(
    ArgoApplication? Argo,
    IReadOnlyList<DynamicResource> Workloads,
    IReadOnlyList<DynamicResource> ReplicaSets,
    IReadOnlyList<DynamicResource> Jobs,
    IReadOnlyList<DynamicResource> Pods,
    IReadOnlyList<DynamicResource> Events,
    DateTimeOffset Now);
