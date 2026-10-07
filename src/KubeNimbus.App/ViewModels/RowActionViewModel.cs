using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>Which mutating action the row action strip is armed for.</summary>
public enum RowActionKind
{
    /// <summary>Set a workload's replica count through its <c>scale</c> subresource.</summary>
    Scale,

    /// <summary>Stamp <c>restartedAt</c> on a workload's pod template (<c>kubectl rollout restart</c>).</summary>
    Restart,

    /// <summary>Delete the object — for a controller-owned pod, that is how it is recreated.</summary>
    Delete,

    /// <summary>Make a node unschedulable (<c>spec.unschedulable: true</c>).</summary>
    Cordon,

    /// <summary>Make a node schedulable again.</summary>
    Uncordon,

    /// <summary>Cordon a node and evict the pods that may be evicted from it.</summary>
    Drain,

    /// <summary>Ask Argo CD to reconcile an Application with the revision Git declares.</summary>
    ArgoSync,

    /// <summary>Ask Argo CD to re-compare an Application against Git, changing nothing.</summary>
    ArgoRefresh,

    /// <summary>Create a Job from a CronJob's template now (<c>kubectl create job --from=cronjob/…</c>).</summary>
    Trigger,

    /// <summary>Stop a CronJob scheduling new Jobs (<c>spec.suspend: true</c>).</summary>
    Suspend,

    /// <summary>Let a suspended CronJob schedule again.</summary>
    Resume,
}

/// <summary>
/// The armed state of one mutating action on one object: what it is about to do, the
/// replica count or the drain options when it needs them, whether it is running, and how
/// it ended. It is the app's confirm step for scale / rollout restart / delete / cordon /
/// uncordon / drain / a CronJob's run-now, suspend and resume / Argo's sync and refresh, and
/// it is one view model for all of them deliberately — the confirm sentence, the in-flight
/// state, the RBAC 403 and the success line are identical work many times over otherwise,
/// and near-identical strips are exactly how they drift apart.
///
/// <para>
/// Drain is the one that is not a single request. It streams
/// (<see cref="ClusterClient.DrainNodeAsync"/>), it can run for minutes, and it can be
/// stopped halfway — so this view model owns the drain's cancellation, refuses to be
/// dismissed while one is running, and reports the partial state when one is stopped.
/// See CLAUDE.md's "Node operations" section for why a partial drain has to be a
/// designed state rather than an accident.
/// </para>
///
/// <para>
/// It carries its own target (client, descriptor, namespace, name), captured when the
/// action was armed. In an aggregated fleet list the client is the row's <em>own</em>
/// cluster's, resolved by <c>ClusterTabViewModel.ClientFor</c>, so a confirm can never
/// land on the cluster the tab happens to be pointed at. And because the target is
/// captured, moving the selection while the strip is open cannot silently re-aim it —
/// the strip names what it will act on, and that is what it acts on.
/// </para>
/// </summary>
public sealed partial class RowActionViewModel : ObservableObject
{
    /// <summary>
    /// Upper bound on the replica box. Not a policy — it is a fat-finger guard, since
    /// the difference between 5 and 5000 replicas is one keystroke and one of them
    /// pages somebody. Anything genuinely bigger is a YAML edit.
    /// </summary>
    /// <remarks>
    /// <c>decimal</c> because that is what <c>NumericUpDown.Maximum</c> is, and an int
    /// constant does not convert in XAML — the binding is to an <c>int?</c> either way.
    /// </remarks>
    public const decimal MaxReplicas = 10_000m;

    /// <summary>Null on the demo cluster — the action has no API server to talk to.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;

    /// <summary>The Pod kind's descriptor on this row's own cluster; only a drain needs it.</summary>
    private readonly ResourceDescriptor? _podDescriptor;

    /// <summary>The Job kind's descriptor on this row's own cluster; only a CronJob's run-now needs it.</summary>
    private readonly ResourceDescriptor? _jobDescriptor;
    private readonly string? _namespace;
    private readonly string _name;

    /// <summary>The pods on the node, as last listed — so re-planning after a checkbox
    /// moves costs nothing and the plan can update as you tick.</summary>
    private IReadOnlyList<DynamicResource> _podsOnNode = [];
    private bool _planLoaded;
    private CancellationTokenSource? _drainCts;

    public RowActionViewModel(
        RowActionKind kind,
        ClusterClient? client,
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        string clusterName = "",
        int? replicas = null,
        ResourceDescriptor? podDescriptor = null,
        ResourceDescriptor? jobDescriptor = null,
        ClusterEnvironment environment = ClusterEnvironment.Unknown)
    {
        Kind = kind;
        _client = client;
        _descriptor = descriptor;
        _podDescriptor = podDescriptor;
        _jobDescriptor = jobDescriptor;
        _namespace = @namespace;
        _name = name;
        _replicas = replicas;
        _fromReplicas = replicas;
        ClusterName = clusterName;
        Environment = environment;

        // Empty as well as null: a cluster-scoped row (a Node, a PersistentVolume) has
        // no namespace, and ResourceRowViewModel.Namespace is a non-nullable string, so
        // the naive null check printed "Node/demo-worker-1 in " — visible only in a
        // rendered strip, which is where it was found.
        var where = string.IsNullOrEmpty(@namespace) ? "" : $" in {@namespace}";

        // The cluster, always, by the name the switcher and the tab show (B3-1). It used to
        // be named only in an aggregated fleet list, so an ordinary tab's strip read "Delete
        // Pod/x in payments?" and said nothing about which cluster — the very wrong-cluster
        // incident the environment colours exist to prevent. Production is said in words as
        // well as in the strip's colour (UI rule 11: a colour carries the information only for
        // someone who already knows the code), because a cluster assigned production by hand
        // need not have "prod" anywhere in its name.
        var production = IsProduction ? " (production)" : "";
        var cluster = clusterName.Length > 0 ? $" on {clusterName}{production}" : production;
        Target = $"{descriptor.Kind}/{name}{where}{cluster}";
    }

    public RowActionKind Kind { get; }

    /// <summary>What this action will act on, spelled out — a confirm that doesn't name its object isn't one.</summary>
    public string Target { get; }

    /// <summary>The context the action lands on, as the cluster switcher names it; empty only in a hand-built strip.</summary>
    public string ClusterName { get; }

    /// <summary>The environment of the cluster the action lands on — the row's own cluster in a fleet list.</summary>
    public ClusterEnvironment Environment { get; }

    /// <summary>
    /// True when the action lands on a production cluster (classified or user-assigned). The
    /// strip carries the production colour, and a delete there always asks — see
    /// <see cref="NeedsConfirm"/>.
    /// </summary>
    public bool IsProduction => Environment == ClusterEnvironment.Production;

    /// <summary>
    /// Whether this action must stop at the strip rather than run on the press. Only a delete
    /// can skip it, and only when "Confirm before deleting" is off <em>and</em> the cluster is
    /// not production: the preference is a convenience for clusters where a wrong delete is
    /// cheap, and on production it never is (B3-1). Read with the preference as it is at the
    /// press, by both delete paths (the list and the YAML editor).
    /// </summary>
    public bool NeedsConfirm(bool confirmDeletesPreference) =>
        Kind != RowActionKind.Delete || DeleteNeedsConfirm(confirmDeletesPreference, Environment);

    /// <summary>
    /// The delete rule itself, shared by the strip and the YAML editor's own Delete so the two
    /// cannot disagree: ask when the preference says so, and on production always.
    /// </summary>
    public static bool DeleteNeedsConfirm(bool confirmDeletesPreference, ClusterEnvironment environment) =>
        confirmDeletesPreference || environment == ClusterEnvironment.Production;

    public bool IsScale => Kind == RowActionKind.Scale;

    public bool IsDelete => Kind == RowActionKind.Delete;

    public bool IsDrain => Kind == RowActionKind.Drain;

    /// <summary>True for the sync, which is the one Argo action with a decision attached to it.</summary>
    public bool IsArgoSync => Kind == RowActionKind.ArgoSync;

    /// <summary>
    /// Delete resources that have left Git as part of the sync — Argo's own prune, and the
    /// half of a sync that removes things rather than adding them. Off by default and stated
    /// in the confirm, the same treatment the drain's two destructive options get: pruning is
    /// ordinary GitOps and is also how a sync destroys something.
    /// </summary>
    [ObservableProperty]
    private bool _argoPrune;

    /// <summary>The sentence above the controls. States the consequence, not the API call.</summary>
    public string Question => Kind switch
    {
        RowActionKind.Scale => ScaleQuestion,
        RowActionKind.Restart =>
            $"Restart {Target}? Its pods roll under the controller's own update strategy — surge, "
            + "maxUnavailable and PodDisruptionBudgets are all honored.",
        RowActionKind.Cordon =>
            $"Cordon {Target}? Nothing new will schedule on it. Pods already running stay where they are — "
            + "that is what a drain is for.",
        RowActionKind.Uncordon =>
            $"Uncordon {Target}? The scheduler starts placing pods on it again.",
        // The lifetime sentence is the important half and it is deliberately in the
        // confirm rather than in a tooltip: a drain runs inside this app's process, so
        // the one thing someone must know before starting one is what happens if they
        // close it.
        RowActionKind.Drain =>
            $"Drain {Target}? It is cordoned first, then its pods are evicted one at a time, honouring "
            + "PodDisruptionBudgets. The drain runs inside kubeNimbus — closing this tab or quitting stops "
            + "it partway, leaving the node cordoned with some pods moved and some not.",
        // Both Argo sentences say what the cluster does next rather than what this app is
        // about to send, because in both cases the app's part ends immediately: Argo's
        // controller does the work and reports it back through the watch, seconds later.
        RowActionKind.ArgoSync =>
            $"Sync {Target}? Argo CD applies the revision the Application targets, under its own sync options. "
            + "kubeNimbus asks; Argo does the work and reports back on the Application.",
        RowActionKind.ArgoRefresh =>
            $"Refresh {Target}? Argo re-compares it against Git. Nothing on the cluster changes — this only "
            + "updates what Argo thinks the difference is.",
        // The run is a Job owned by the CronJob, like a scheduled one: its history limits
        // clean it up and deleting the CronJob deletes it. What it is not is scheduled —
        // the CronJob controller does not put a Job it did not create on its active list,
        // so the schedule and its concurrencyPolicy carry on as if nothing had run.
        RowActionKind.Trigger =>
            $"Run {Target} now? A Job is created from its job template, as kubectl create job --from=cronjob does. "
            + "The schedule is unchanged.",
        RowActionKind.Suspend =>
            $"Suspend {Target}? No new Jobs are scheduled until it is resumed. Jobs already running keep running.",
        // The missed-run clause is the thing people are surprised by, and it is the API's
        // documented behaviour: resuming a CronJob with no startingDeadlineSeconds starts
        // the most recent run it missed while suspended straight away.
        RowActionKind.Resume =>
            $"Resume {Target}? Its schedule applies again — and a run it missed while suspended can start straight "
            + "away, unless startingDeadlineSeconds has passed.",
        _ => $"Delete {Target}? This cannot be undone.",
    };

    public string ConfirmLabel => Kind switch
    {
        RowActionKind.Scale => "Scale",
        RowActionKind.Restart => "Restart",
        RowActionKind.Cordon => "Cordon",
        RowActionKind.Uncordon => "Uncordon",
        RowActionKind.Drain => "Drain",
        RowActionKind.ArgoSync => "Sync",
        RowActionKind.ArgoRefresh => "Refresh",
        RowActionKind.Trigger => "Run now",
        RowActionKind.Suspend => "Suspend",
        RowActionKind.Resume => "Resume",
        _ => "Delete",
    };

    /// <summary>
    /// True for the demo cluster. Every one of these actions needs a real API server, so
    /// the strip says so in place and the confirm button is disabled — never a spinner
    /// that hangs and never a silent no-op (CLAUDE.md's demo rule 5, UI rule 9).
    /// </summary>
    public bool IsDemo => _client is null;

    public const string DemoNotice =
        "Scale, restart, delete, cordon, drain, running or suspending a CronJob and the Argo CD actions all "
        + "change objects on a live API server — the demo cluster has none. Everything else about this step is "
        + "exactly what a real cluster shows.";

    /// <summary>
    /// The Job a run-now created, once the server has answered — the one follow-up any of
    /// these actions has, because the reason to run a CronJob by hand is to watch that run.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowUp))]
    [NotifyCanExecuteChangedFor(nameof(OpenCreatedJobCommand))]
    private DynamicResource? _createdJob;

    /// <summary>Opens the created Job's detail (its pods, live); set by the owning cluster tab.</summary>
    public Func<DynamicResource, Task>? OpenJob { get; set; }

    /// <summary>True once there is a created Job to open and somewhere to open it.</summary>
    public bool HasFollowUp => CreatedJob is not null && OpenJob is not null;

    [RelayCommand(CanExecute = nameof(HasFollowUp))]
    private async Task OpenCreatedJobAsync()
    {
        if (CreatedJob is { } job && OpenJob is { } open)
        {
            await open(job);
            Dismissed?.Invoke();
        }
    }

    /// <summary>
    /// The target replica count. Null while the authoritative read of the <c>scale</c>
    /// subresource is still in flight, or if it failed — the confirm stays disabled
    /// until there is a number, rather than defaulting to one nobody chose.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(Question))]
    [NotifyPropertyChangedFor(nameof(ScaleWarning))]
    [NotifyPropertyChangedFor(nameof(HasScaleWarning))]
    [NotifyPropertyChangedFor(nameof(IsScaleWarningProminent))]
    [NotifyPropertyChangedFor(nameof(HasPlainScaleWarning))]
    private int? _replicas;

    /// <summary>
    /// The count the scale starts from: the object's own <c>spec.replicas</c> while the scale
    /// subresource is being read, then that read's answer. The "from" of "from N to M".
    /// </summary>
    private int? _fromReplicas;

    /// <summary>"2 running now" — read from the scale subresource, which is
    /// authoritative where the object's own spec.replicas may not be (a CRD can declare a
    /// different specReplicasPath).</summary>
    [ObservableProperty]
    private string? _currentScale;

    /// <summary>
    /// The scale question, live as the box changes (B3-4): "from 3 to 300" is the sentence
    /// that catches a slipped digit, where "Scale Deployment/x" beside a number did not.
    /// </summary>
    private string ScaleQuestion => (Replicas, _fromReplicas) switch
    {
        (null, _) => $"Scale {Target}",
        ({ } to, null) => $"Scale {Target} to {to}",
        ({ } to, { } from) when to == from => $"Scale {Target} — it is already at {from}",
        ({ } to, { } from) => $"Scale {Target} from {from} to {to}",
    };

    /// <summary>
    /// What the strip warns about the number in the box, or null: every pod stopping, or a
    /// jump of ten times or more (or to ten or more from none). See <see cref="ScaleWarningFor"/>.
    /// </summary>
    public string? ScaleWarning => IsScale ? ScaleWarningFor(_fromReplicas, Replicas, IsProduction) : null;

    public bool HasScaleWarning => ScaleWarning is not null;

    /// <summary>
    /// Scaling a production workload to zero: the one scale warning drawn as an
    /// <c>infoBar</c> rather than a line of warn text, because on production it is an outage.
    /// </summary>
    public bool IsScaleWarningProminent => IsScale && IsProduction && Replicas == 0 && _fromReplicas != 0;

    /// <summary>A scale warning drawn as an ordinary warn line.</summary>
    public bool HasPlainScaleWarning => HasScaleWarning && !IsScaleWarningProminent;

    /// <summary>
    /// The warning for scaling from <paramref name="from"/> (null when unknown) to
    /// <paramref name="to"/>. Deterministic thresholds, stated: to zero from anything but zero;
    /// ten times the current count or more; ten or more from zero. They are a fat-finger
    /// guard, not a policy — the confirm stays live either way.
    /// </summary>
    internal static string? ScaleWarningFor(int? from, int? to, bool production)
    {
        if (to is not { } target)
        {
            return null;
        }

        if (target == 0 && from != 0)
        {
            return production
                ? "This is a production cluster: 0 replicas stops every pod this workload owns, and it serves "
                  + "nothing until it is scaled up again."
                : "0 replicas stops every pod this workload owns. Nothing else about it is removed.";
        }

        if (from is { } current && current > 0 && target >= 10L * current)
        {
            return $"{target} is {target / current}× the current {current}. Check the number before scaling.";
        }

        if (from == 0 && target >= 10)
        {
            return $"{target} pods would start where none run now. Check the number before scaling.";
        }

        return null;
    }

    private void SetFromReplicas(int? value)
    {
        _fromReplicas = value;
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(ScaleWarning));
        OnPropertyChanged(nameof(HasScaleWarning));
        OnPropertyChanged(nameof(IsScaleWarningProminent));
        OnPropertyChanged(nameof(HasPlainScaleWarning));
    }

    // ------------------------------------------------------------------ drain
    //
    // Two options and nothing else. They are kubectl's --force and
    // --delete-emptydir-data under plain-English names, and they are here rather than
    // hidden because each one authorizes destroying something that does not come back:
    // a pod nothing will recreate, and a directory that lives only on this node's disk.
    // The other three flags kubectl carries are deliberately absent — --ignore-daemonsets
    // has one possible answer and the plan states what it left behind instead,
    // --disable-eviction bypasses PodDisruptionBudgets (this app will not offer that as a
    // checkbox), and --timeout is replaced by a drain you can watch and stop.

    /// <summary>Evict pods no controller owns. Off by default; without it they are refused by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrainOptions))]
    private bool _drainForce;

    /// <summary>Evict pods with <c>emptyDir</c> volumes, deleting that data. Off by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrainOptions))]
    private bool _drainDeleteEmptyDirData;

    public DrainOptions DrainOptions => new(DrainForce, DrainDeleteEmptyDirData);

    /// <summary>The plan as last computed — what will be evicted, what is refused, what stays.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(HasDrainPlan))]
    [NotifyPropertyChangedFor(nameof(IsDrainBlocked))]
    private DrainPlan? _drainPlan;

    public bool HasDrainPlan => DrainPlan is not null;

    /// <summary>True while at least one pod needs an option nobody has ticked. The confirm stays dead.</summary>
    public bool IsDrainBlocked => DrainPlan is { IsBlocked: true };

    /// <summary>One line per pod the drain is refusing to touch, naming the pod and why.</summary>
    public ObservableCollection<string> DrainBlockers { get; } = [];

    /// <summary>
    /// The running log: one row per pod as the drain reaches it, plus the node-level
    /// steps. It is the whole answer to "is this hung or is it working" — a drain held
    /// by a PodDisruptionBudget is *correct* and can last minutes, and without a line
    /// saying so it is indistinguishable from a frozen window.
    /// </summary>
    public ObservableCollection<DrainStepViewModel> DrainSteps { get; } = [];

    /// <summary>True while the eviction loop is running, which is when the strip cannot be dismissed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopDrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(CanDismiss))]
    [NotifyPropertyChangedFor(nameof(IsPromptVisible))]
    private bool _isDraining;

    /// <summary>
    /// Cancel/Close is live except while a drain is running, where the honest button is
    /// Stop instead: dismissing a strip whose eviction loop kept running would leave a
    /// mutating action with no surface at all.
    /// </summary>
    public bool CanDismiss => !IsDraining;

    /// <summary>
    /// Whether the confirm/cancel pair is on screen at all. A running drain takes their
    /// slot for Stop, rather than leaving a dead Drain button under it — the same "one
    /// slot, swapped on the state" the port-forward pane settled (UI rule 11). The first
    /// rendering of this had Stop drawn over the confirm, which the screenshot caught.
    /// </summary>
    public bool IsPromptVisible => !IsDone && !IsDraining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isBusy;

    /// <summary>True once the action has succeeded: the strip stops being a prompt and
    /// becomes its own result, with one button left (Close).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(IsPromptVisible))]
    private bool _isDone;

    /// <summary>Inputs and the confirm are live only while the action is neither running nor finished.</summary>
    public bool IsEditable => !IsBusy && !IsDone;

    /// <summary>What happened — in flight, succeeded, or the server's own refusal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isSuccess;

    /// <summary>Called when the strip should go away; set by the owning cluster tab.</summary>
    public Action? Dismissed { get; set; }

    private bool CanConfirm =>
        IsEditable
        && !IsDemo
        // A running drain is confirmed already. The Confirm button is not rendered while
        // it runs, but the command must refuse on its own: a hidden button is a layout
        // fact, and a second eviction loop over the same node is not recoverable.
        && !IsDraining
        && (!IsScale || Replicas is not null)
        // A run-now creates a Job, and without this cluster's Job kind there is nowhere to.
        && (Kind != RowActionKind.Trigger || _jobDescriptor is not null)
        // A drain confirms against a plan, never against a guess: until the pods on the
        // node have been read there is nothing to agree to, and a plan with refusals is
        // one the drain will not run.
        && (!IsDrain || DrainPlan is { IsBlocked: false });

    /// <summary>
    /// Reads the current scale before the user picks a new one. Deliberately the
    /// subresource rather than the row's <c>spec.replicas</c>: that is what the patch
    /// will hit, and for a custom resource it is the only field guaranteed to mean
    /// "replicas". A failure here (RBAC on the subresource, typically) is stated and
    /// the box falls back to whatever the object declared, rather than blocking the
    /// action on a read it doesn't strictly need.
    /// </summary>
    public async Task LoadCurrentScaleAsync()
    {
        if (_client is null || !IsScale)
        {
            return;
        }

        IsBusy = true;
        Message = "Reading the current scale…";
        try
        {
            var scale = await _client.GetScaleAsync(_descriptor, _namespace, _name);
            SetFromReplicas(scale.Replicas);
            Replicas = scale.Replicas;
            // Only what the question above does not already say: it carries the set count
            // ("from 3 to 5"), so beside the box this is the running count alone (UI rule 20).
            CurrentScale = scale.CurrentReplicas is { } running ? $"{running} running now" : null;
            Message = null;
        }
        catch (Exception ex)
        {
            IsError = true;
            Message = $"Could not read the current scale: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Lists the pods on the node and works out what a drain would do to each, before
    /// anything is evicted. This is the whole of the safety design: the refusals
    /// (a pod nothing would recreate, a pod whose <c>emptyDir</c> data goes with it) are
    /// discovered and named <em>here</em>, where the answer is still "don't", rather than
    /// halfway through an eviction loop where it is already too late for the pods behind
    /// it.
    /// </summary>
    public async Task LoadDrainPlanAsync()
    {
        if (!IsDrain)
        {
            return;
        }

        if (_client is not { } client)
        {
            // The demo cluster plans for real. The classification is pure and the demo
            // dataset has pods on nodes, so what renders offline is the production
            // plan — including the two refusals — and only the eviction itself is
            // unavailable (demo rules 4 and 5). A demo that showed an empty plan would
            // teach that a drain has nothing to check.
            _podsOnNode = [.. Demo.DemoData.Pods.Where(p =>
                string.Equals(NodeActions.NodeNameOf(p), _name, StringComparison.Ordinal))];
            _planLoaded = true;
            RebuildDrainPlan();
            return;
        }

        if (_podDescriptor is null)
        {
            return;
        }

        IsBusy = true;
        Message = "Reading the pods on this node…";
        try
        {
            _podsOnNode = await client.ListPodsOnNodeAsync(_podDescriptor, _name);
            _planLoaded = true;
            RebuildDrainPlan();
            Message = null;
        }
        catch (Exception ex)
        {
            IsError = true;
            Message = $"Could not read the pods on this node: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-plans from the pods already read. Ticking an option must change the plan on
    /// screen immediately — the refusal it clears is the reason the box is being ticked,
    /// and a plan that only updated on confirm would be asking someone to take it on
    /// trust.
    /// </summary>
    partial void OnDrainForceChanged(bool value) => RebuildDrainPlan();

    partial void OnDrainDeleteEmptyDirDataChanged(bool value) => RebuildDrainPlan();

    private void RebuildDrainPlan()
    {
        // Never before the pods have been read: a plan over an empty list looks like a
        // node with nothing on it, and it would enable the confirm.
        if (!IsDrain || !_planLoaded)
        {
            return;
        }

        var plan = NodeActions.Plan(_podsOnNode, DrainOptions);
        DrainPlan = plan;

        DrainBlockers.Clear();
        foreach (var pod in plan.Blocked)
        {
            DrainBlockers.Add($"{pod.Key} — {pod.Note}");
        }
    }

    /// <summary>
    /// The eviction loop, rendered as it happens. Every stage the stream reports lands
    /// either on its own pod row or on the strip's message line; nothing is swallowed,
    /// including the failures, because "which pods did not move" is the question a
    /// half-finished drain exists to answer.
    /// </summary>
    private async Task RunDrainAsync(ClusterClient client)
    {
        if (_podDescriptor is null)
        {
            IsError = true;
            Message = "This cluster's Pod kind has not been discovered yet, so there is nothing to evict through.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _drainCts = cts;
        IsDraining = true;
        DrainSteps.Clear();

        var rows = new Dictionary<string, DrainStepViewModel>(StringComparer.Ordinal);
        var stopped = false;

        try
        {
            await foreach (var progress in client.DrainNodeAsync(
                _descriptor, _podDescriptor, _name, DrainOptions, cts.Token))
            {
                if (progress.Plan is { } plan)
                {
                    DrainPlan = plan;
                }

                if (progress.PodKey is { } key)
                {
                    if (!rows.TryGetValue(key, out var row))
                    {
                        rows[key] = row = new DrainStepViewModel(key);
                        DrainSteps.Add(row);
                    }

                    row.Update(progress.Stage, progress.Message);
                    continue;
                }

                Message = progress.Message;
                IsError = progress.Stage is DrainStage.Refused or DrainStage.CompletedWithFailures;
                IsSuccess = progress.Stage == DrainStage.Completed;

                if (progress.Stage is DrainStage.Refused or DrainStage.Completed or DrainStage.CompletedWithFailures)
                {
                    IsDone = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose. Not an error and not a rollback — say exactly what the
            // cluster is left in, which is the state CLAUDE.md's node section calls the
            // one that must never be implicit.
            stopped = true;
        }
        catch (Exception ex)
        {
            IsError = true;
            Message = $"Drain failed: {FirstLine(ex.Message)}";
            IsDone = true;
        }
        finally
        {
            IsDraining = false;
            _drainCts = null;
        }

        if (stopped)
        {
            var moved = rows.Values.Count(r => r.Stage is DrainStage.PodEvicted or DrainStage.PodGone);
            IsError = true;
            IsSuccess = false;
            Message =
                $"Drain stopped. {moved} pod(s) were evicted and the rest were not; {_name} is still cordoned. "
                + "Run the drain again to finish, or uncordon to put the node back into service as it is.";
            IsDone = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        if (Kind == RowActionKind.Drain)
        {
            // A drain is not one request: it owns its own busy state, its own
            // cancellation and its own per-pod reporting.
            IsError = false;
            IsSuccess = false;
            await RunDrainAsync(client);
            return;
        }

        IsBusy = true;
        IsError = false;
        IsSuccess = false;
        Message = Kind switch
        {
            RowActionKind.Scale => $"Scaling to {Replicas}…",
            RowActionKind.Restart => "Restarting…",
            RowActionKind.Cordon => "Cordoning…",
            RowActionKind.Uncordon => "Uncordoning…",
            RowActionKind.ArgoSync => "Asking Argo to sync…",
            RowActionKind.ArgoRefresh => "Asking Argo to refresh…",
            RowActionKind.Trigger => "Creating the Job…",
            RowActionKind.Suspend => "Suspending…",
            RowActionKind.Resume => "Resuming…",
            _ => "Deleting…",
        };

        try
        {
            switch (Kind)
            {
                case RowActionKind.Scale:
                    // Replicas is non-null here — CanConfirm gates on it.
                    var result = await client.ScaleAsync(_descriptor, _namespace, _name, Replicas!.Value);
                    Message = $"Scaled to {result.Replicas}. The list follows the rollout as the watch reports it.";
                    break;

                case RowActionKind.Restart:
                    var at = DateTimeOffset.UtcNow;
                    await client.RestartWorkloadAsync(_descriptor, _namespace, _name, at);
                    Message = $"Restart requested — pod template stamped {WorkloadActions.FormatRestartedAt(at)}.";
                    break;

                case RowActionKind.Cordon:
                    await client.SetNodeSchedulableAsync(_descriptor, _name, schedulable: false);
                    Message =
                        $"{_name} is cordoned. Its pods keep running — drain the node to move them.";
                    break;

                case RowActionKind.Uncordon:
                    await client.SetNodeSchedulableAsync(_descriptor, _name, schedulable: true);
                    Message = $"{_name} is schedulable again.";
                    break;

                // Both of these report a *request*, not a result, and the wording is
                // deliberate: the API server accepting the patch means Argo has been asked.
                // What it then did lands in status.operationState some seconds later and
                // reaches the list through the watch, so a message claiming "synced" here
                // would be asserting something this app has not observed.
                case RowActionKind.ArgoSync:
                    await client.SyncArgoApplicationAsync(_descriptor, _namespace, _name, ArgoPrune);
                    Message = ArgoPrune
                        ? "Sync requested, with prune. Argo reports progress on the Application — the list "
                          + "follows it as the watch reports it."
                        : "Sync requested. Argo reports progress on the Application — the list follows it as "
                          + "the watch reports it.";
                    break;

                case RowActionKind.ArgoRefresh:
                    await client.RefreshArgoApplicationAsync(_descriptor, _namespace, _name);
                    Message =
                        "Refresh requested. Argo clears the annotation once it has re-compared, so there is "
                        + "nothing on the object to watch — the sync status updates when it is done.";
                    break;

                // The Job's name is the server's (generateName), so it is only known from
                // the answer — and it is the one thing the next step needs.
                case RowActionKind.Trigger:
                    var job = await client.CreateJobFromCronJobAsync(_descriptor, _jobDescriptor!, _namespace, _name);
                    CreatedJob = job;
                    Message = $"Created Job/{job.Name}. Its pods appear as the Job controller starts them.";
                    break;

                case RowActionKind.Suspend:
                    await client.SetCronJobSuspendedAsync(_descriptor, _namespace, _name, suspended: true);
                    Message = $"{_name} is suspended. Jobs already running carry on; nothing new is scheduled until it is resumed.";
                    break;

                case RowActionKind.Resume:
                    await client.SetCronJobSuspendedAsync(_descriptor, _namespace, _name, suspended: false);
                    Message = $"{_name} is scheduled again.";
                    break;

                default:
                    await client.DeleteResourceAsync(_descriptor, _namespace, _name);
                    Message = "Deleted.";
                    break;
            }

            IsSuccess = true;
            IsDone = true;
        }
        catch (Exception ex)
        {
            // The API server's own sentence, which on the common failure here (a 403)
            // names the subject, the verb and the resource — i.e. the whole diagnosis.
            IsError = true;
            Message = $"{ConfirmLabel} failed: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Dismisses the strip — the cancel before the action, and the close after it.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsDraining)
        {
            // Belt and braces: the button is hidden while a drain runs, and the palette
            // cannot reach this. A dismissed strip over a live eviction loop is the one
            // outcome this whole design is trying not to have.
            return;
        }

        Dismissed?.Invoke();
    }

    /// <summary>
    /// Stops a running drain where it is. Not a rollback and not an error: the node
    /// stays cordoned and whatever was evicted stays evicted, which is exactly what the
    /// final message says.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDraining))]
    private void StopDrain() => _drainCts?.Cancel();

    /// <summary>Cancels a drain this strip owns when the tab or the app is going away.</summary>
    public void CancelDrain() => _drainCts?.Cancel();

    /// <summary>API-server messages run to several lines; an inline strip gets the first one.</summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}

/// <summary>
/// One pod's row in a running drain: what the eviction loop last said about it, and how
/// that should read. Its own type rather than a formatted string, because the state
/// changes — a pod that a PodDisruptionBudget blocks now is very often evicted a minute
/// later, and the row has to stop saying "blocked" when that happens.
/// </summary>
public sealed partial class DrainStepViewModel(string podKey) : ObservableObject
{
    public string PodKey { get; } = podKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private DrainStage _stage;

    [ObservableProperty]
    private string _detail = "";

    /// <summary>Held by a PodDisruptionBudget. Correct behaviour, so it reads as a wait, not a failure.</summary>
    public bool IsBlocked => Stage == DrainStage.PodBlocked;

    /// <summary>Refused in a way retrying will not fix — an RBAC 403, typically.</summary>
    public bool IsFailed => Stage == DrainStage.PodFailed;

    public bool IsDone => Stage is DrainStage.PodEvicted or DrainStage.PodGone;

    internal void Update(DrainStage stage, string message)
    {
        Stage = stage;
        Detail = message;
    }
}
