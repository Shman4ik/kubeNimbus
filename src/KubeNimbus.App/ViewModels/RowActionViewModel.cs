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
}

/// <summary>
/// The armed state of one mutating action on one object: what it is about to do, the
/// replica count or the drain options when it needs them, whether it is running, and how
/// it ended. It is the app's confirm step for scale / rollout restart / delete / cordon /
/// uncordon / drain, and it is one view model for all six deliberately — the confirm
/// sentence, the in-flight state, the RBAC 403 and the success line are identical work six
/// times over otherwise, and six near-identical strips is exactly how they drift apart.
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
        ResourceDescriptor? podDescriptor = null)
    {
        Kind = kind;
        _client = client;
        _descriptor = descriptor;
        _podDescriptor = podDescriptor;
        _namespace = @namespace;
        _name = name;
        _replicas = replicas;

        var where = @namespace is null ? "" : $" in {@namespace}";
        var cluster = clusterName.Length > 0 ? $" · {clusterName}" : "";
        Target = $"{descriptor.Kind}/{name}{where}{cluster}";
    }

    public RowActionKind Kind { get; }

    /// <summary>What this action will act on, spelled out — a confirm that doesn't name its object isn't one.</summary>
    public string Target { get; }

    public bool IsScale => Kind == RowActionKind.Scale;

    public bool IsDelete => Kind == RowActionKind.Delete;

    public bool IsDrain => Kind == RowActionKind.Drain;

    /// <summary>The sentence above the controls. States the consequence, not the API call.</summary>
    public string Question => Kind switch
    {
        RowActionKind.Scale => $"Scale {Target}",
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
        _ => $"Delete {Target}? This cannot be undone.",
    };

    public string ConfirmLabel => Kind switch
    {
        RowActionKind.Scale => "Scale",
        RowActionKind.Restart => "Restart",
        RowActionKind.Cordon => "Cordon",
        RowActionKind.Uncordon => "Uncordon",
        RowActionKind.Drain => "Drain",
        _ => "Delete",
    };

    /// <summary>
    /// True for the demo cluster. Every one of these actions needs a real API server, so
    /// the strip says so in place and the confirm button is disabled — never a spinner
    /// that hangs and never a silent no-op (CLAUDE.md's demo rule 5, UI rule 9).
    /// </summary>
    public bool IsDemo => _client is null;

    public const string DemoNotice =
        "Scale, restart, delete, cordon and drain change objects on a live API server — the demo cluster "
        + "has none. Everything else about this step is exactly what a real cluster shows.";

    /// <summary>
    /// The target replica count. Null while the authoritative read of the <c>scale</c>
    /// subresource is still in flight, or if it failed — the confirm stays disabled
    /// until there is a number, rather than defaulting to one nobody chose.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsScalingToZero))]
    private int? _replicas;

    /// <summary>"currently 3 set · 2 running" — read from the scale subresource, which is
    /// authoritative where the object's own spec.replicas may not be (a CRD can declare a
    /// different specReplicasPath).</summary>
    [ObservableProperty]
    private string? _currentScale;

    /// <summary>Scaling to zero stops every pod. Legitimate and common — and worth saying out loud.</summary>
    public bool IsScalingToZero => IsScale && Replicas == 0;

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
    [NotifyPropertyChangedFor(nameof(CanDismiss))]
    private bool _isDraining;

    /// <summary>
    /// Cancel/Close is live except while a drain is running, where the honest button is
    /// Stop instead: dismissing a strip whose eviction loop kept running would leave a
    /// mutating action with no surface at all.
    /// </summary>
    public bool CanDismiss => !IsDraining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isBusy;

    /// <summary>True once the action has succeeded: the strip stops being a prompt and
    /// becomes its own result, with one button left (Close).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
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
        && (!IsScale || Replicas is not null)
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
            Replicas = scale.Replicas;
            CurrentScale = scale.CurrentReplicas is { } running
                ? $"currently {scale.Replicas} set · {running} running"
                : $"currently {scale.Replicas}";
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
        if (_client is not { } client || !IsDrain || _podDescriptor is null)
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
