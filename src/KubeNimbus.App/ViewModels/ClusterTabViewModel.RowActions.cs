using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// What a row can do: open and peek, logs, exec, port-forward, YAML, and the mutating workload and
// node actions that arm the RowActionStrip (UI rule 17, workload-actions.md, node-operations.md).
public sealed partial class ClusterTabViewModel
{
    /// <summary>Double-click / Enter: promotes (or opens) a permanent tab. Pod → detail; anything else → YAML.</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync() => await OpenRowAsync(SelectedRow, preview: false);

    /// <summary>Space: quick-peek — replaces the current preview tab in place.</summary>
    [RelayCommand]
    private async Task PeekSelectedAsync() => await OpenRowAsync(SelectedRow, preview: true);

    // --------------------------------------------------------- row actions
    //
    // Right-clicking a resource did nothing — the app had exactly one context menu
    // anywhere (the cluster tab's environment override), and none of logs / exec /
    // port-forward was reachable from the list at all: you had to open a pod's detail
    // tab first and find the buttons on its container strip. These commands back both
    // the row's ContextFlyout and the matching palette entries, so the same six
    // actions are reachable by mouse and by keyboard.

    /// <summary>True when the selected row is a pod — the only kind logs/exec/forward apply to.</summary>
    public bool IsPodRowSelected => ActionRow is { } row && DescriptorFor(row) is { Kind: "Pod", Group: "" };

    /// <summary>True whenever a row is selected at all (YAML and delete work for any kind).</summary>
    public bool HasSelectedRow => ActionRow is not null;

    /// <summary>
    /// The pod the selected row is an Event about, when it is one — see
    /// <see cref="LogTarget.InvolvedPod"/>. Where the Events list's L, logs icon and
    /// "Logs" menu item go.
    /// </summary>
    public OwnerRef? SelectedEventPod => SelectedRow is { } row ? LogTarget.InvolvedPod(row.Resource) : null;

    /// <summary>
    /// A pod's own logs are one step away: the selected row is a pod, or an Event about
    /// one. Gates the "Logs" menu item and L's pod half; exec, port-forward and previous
    /// logs stay on <see cref="IsPodRowSelected"/>, since they act on a pod row itself.
    /// </summary>
    public bool CanOpenPodLogsForSelectedRow => IsPodRowSelected || (ActionRow is not null && SelectedEventPod is not null);

    /// <summary>
    /// Opens pod detail on the Logs tab. Same tab-reuse path as a double-click, so
    /// this never opens a second tab for a pod that already has one. Through
    /// <see cref="OpenLogsForAsync"/>, the entry point the palette's log rows share. On
    /// an Event about a pod, that pod's logs (<see cref="OpenNamedLogsAsync"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenPodLogsForSelectedRow))]
    private Task OpenLogsAsync() => OpenSelectedRowLogsAsync(previous: false);

    /// <summary>
    /// Opens pod detail on the Logs tab, showing the crashed instance. This is the
    /// single most important gesture on a CrashLoopBackOff and it had no entry point
    /// outside a toggle that didn't work.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private Task OpenPreviousLogsAsync() => OpenSelectedRowLogsAsync(previous: true);

    private Task OpenSelectedRowLogsAsync(bool previous, bool? maximized = null) =>
        SelectedRow is { } row ? OpenLogsForRowAsync(row, previous, maximized) : Task.CompletedTask;

    /// <summary>
    /// True when the selected object names the pods it owns — which is the honest test
    /// for "can these be tailed as one stream", and is read off the object rather than
    /// off a list of kinds, exactly as the scale/restart capability checks are. A
    /// Deployment, StatefulSet, DaemonSet, ReplicaSet, Job, Service and a CRD that
    /// declares a pod selector all qualify on the same evidence; a pod does not (it has
    /// its own detail pane), and neither does an object whose selector is empty — see
    /// <see cref="LabelSelector.ForPodsOf"/> for why an empty selector is refused rather
    /// than read as "everything".
    /// </summary>
    public bool CanAggregateLogsForSelectedRow =>
        ActionRow is { } row && LabelSelector.ForPodsOf(row.Resource) is not null;

    /// <summary>
    /// One pane over every pod the selected workload owns. This is the gesture people
    /// leave for <c>stern</c>: during a rolling deployment the pod going away and the
    /// pod coming up are the same question, and reading them in two panes is reading
    /// them in the wrong order. Through <see cref="OpenLogsForAsync"/>, like the pod's.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAggregateLogsForSelectedRow))]
    private Task OpenWorkloadLogsAsync() => OpenSelectedRowLogsAsync(previous: false);

    /// <summary>
    /// True when the selected row has logs of either shape — a pod's own, or the pods a
    /// workload names. The union of <see cref="IsPodRowSelected"/> and
    /// <see cref="CanAggregateLogsForSelectedRow"/>, which is exactly where L does something.
    /// </summary>
    public bool CanOpenLogsForSelectedRow => CanOpenPodLogsForSelectedRow || CanAggregateLogsForSelectedRow;

    /// <summary>
    /// Shift+L: the same logs L opens, with the inspector maximized over the list whatever
    /// the preference says. Through <see cref="OpenLogsForAsync"/>, so it reuses the tab L
    /// would have opened.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenLogsForSelectedRow))]
    private Task OpenLogsMaximizedAsync() => OpenSelectedRowLogsAsync(previous: false, maximized: true);

    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private void ExecIntoSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        // Null in demo mode, which is exactly what the tab reads as "not available
        // here" — see InspectorTabViewModelBase.IsDemo.
        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        AddInspectorTab(new ExecTabViewModel(client, row.Namespace, row.Name, FirstContainerOf(row)));
    }

    [RelayCommand(CanExecute = nameof(CanPortForwardSelected))]
    private void PortForwardSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (IsServiceRowSelected)
        {
            if (CanPortForwardSelected)
            {
                PortForwardSelectedService();
            }

            return;
        }

        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        AddInspectorTab(AdoptForward(new PortForwardTabViewModel(client, row.Namespace, row.Name, DeclaredPortsOf(row)), row.ClusterName));
    }

    /// <summary>
    /// Always the YAML editor, even for a pod — whose default action is the detail
    /// pane, leaving no way to reach its manifest from the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void EditSelectedYaml()
    {
        if (SelectedRow is not { } row || DescriptorFor(row) is not { } descriptor)
        {
            return;
        }

        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        var key = YamlEditorTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new YamlEditorTabViewModel(
            client, descriptor, row.Namespace, row.Name, row.Resource.ToYaml(), row.ClusterName));
    }

    /// <summary>
    /// Opens (or re-selects) the YAML editor for an object that is not necessarily a row of
    /// the list — the Applications page's Edit YAML. Same tab key and same editor as E on a
    /// row, so the two routes can never open two editors for one object.
    /// </summary>
    public void OpenYamlFor(ResourceDescriptor descriptor, DynamicResource resource)
    {
        if (Client is null && !IsDemo)
        {
            return;
        }

        var key = YamlEditorTabViewModel.KeyFor("", descriptor, resource.Namespace, resource.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new YamlEditorTabViewModel(
            Client, descriptor, resource.Namespace, resource.Name, resource.ToYaml(), ""));
    }

    /// <summary>The descriptor this cluster's discovery gave a group/kind, or null — the Applications page's actions need the real one.</summary>
    public ResourceDescriptor? DescriptorOf(string group, string kind) =>
        SidebarSections.SelectMany(s => s.Kinds)
            .Select(k => k.Descriptor)
            .FirstOrDefault(d => d.Group == group && d.Kind == kind);


    // ------------------------------------------------- mutating workload actions
    //
    // Scale / rollout restart / delete. The app was read-mostly before these: the only
    // way to change a replica count was to edit YAML, and "restart that deployment" —
    // the single most common on-call GUI action, and one click in every competitor —
    // had no entry point at all. All three land on the same armed confirm strip
    // (see RowActionViewModel), which is what makes "confirmable" one implementation
    // rather than three, and what gives the replica count somewhere to be typed.

    /// <summary>
    /// The armed action, or null when nothing is pending. Set by the three commands
    /// below and cleared when the strip is dismissed or the list changes underneath it.
    /// </summary>
    [ObservableProperty]
    private RowActionViewModel? _pendingRowAction;

    /// <summary>
    /// Whether the selected row's kind can be scaled — the server declares a
    /// <c>scale</c> subresource for it. Discovery, never a list of kinds: in an
    /// aggregated fleet list the descriptor is the one that cluster's own discovery
    /// produced, so the same CRD can be scalable on one cluster and not on another and
    /// the menu is right on both.
    /// </summary>
    public bool CanScaleSelectedRow =>
        ActionRow is { } row && DescriptorFor(row) is { } descriptor && WorkloadActions.SupportsScale(descriptor);

    /// <summary>
    /// Whether the selected object can be rollout-restarted: it has a pod template to
    /// stamp. A property of the object rather than of its kind, which is what makes it
    /// true for Deployments, StatefulSets, DaemonSets <em>and</em> a CRD that embeds a
    /// pod template, with none of the four named anywhere.
    /// </summary>
    public bool CanRestartSelectedRow =>
        ActionRow is { } row && DescriptorFor(row) is { } descriptor
        && WorkloadActions.SupportsRestart(descriptor, row.Resource);

    /// <summary>Whether the server says the selected row's kind can be deleted at all.</summary>
    public bool CanDeleteSelectedRow => DeleteTargets().Count > 0;

    [RelayCommand(CanExecute = nameof(CanScaleSelectedRow))]
    private async Task ScaleSelectedAsync()
    {
        if (ArmRowAction(RowActionKind.Scale) is { } action)
        {
            // Opens on the object's own spec.replicas so the box is never empty, then
            // replaces it with the scale subresource's answer, which is the field the
            // patch will actually set.
            await action.LoadCurrentScaleAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartSelectedRow))]
    private void RestartSelected() => ArmRowAction(RowActionKind.Restart);

    /// <summary>
    /// Delete, with the confirm armed in place. It used to open the object's YAML with
    /// that editor's own confirm armed, which put an editor tab and a manifest between
    /// someone and a one-line question; the strip asks it where the row is, and names
    /// the object either way. The YAML editor keeps its own Delete for when you are
    /// already in there.
    ///
    /// <para>
    /// "Confirm before deleting" is read here, at the press, exactly as
    /// <c>YamlEditorTabViewModel.RequestDeleteAsync</c> reads it: someone who turns it
    /// back on after a near-miss expects the very next delete to ask. Scale and restart
    /// do not consult it — it is a setting about deleting, and scale needs its input
    /// step regardless.
    /// </para>
    /// <para>
    /// A production cluster always asks, whatever the preference says
    /// (<see cref="RowActionViewModel.NeedsConfirm"/>) — in a fleet list, by the row's own
    /// cluster's environment.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRow))]
    private void DeleteSelected()
    {
        // Several rows selected: one strip for all of them (FEAT-6), and it always asks (#305,
        // the owner's decision of 2026-10-12): "Confirm before deleting" is a choice made about
        // one object, and it does not stretch to N. One row keeps following the preference.
        var action = HasMultipleSelection ? ArmBulkDelete() : ArmRowAction(RowActionKind.Delete);
        if (action is { IsBulk: false } && !action.NeedsConfirm(App.LoadSettings().ConfirmDeletes))
        {
            action.RunNow();
        }
    }

    // ---------------------------------------------------------------- node actions
    //
    // Cordon, uncordon and drain, on the same strip as scale/restart/delete — the first two
    // firing on their click, since each takes the other back, and the drain asking. They
    // are node-only and they say so through the same kind of capability check the other
    // three use — with one honest difference, argued in NodeActions.SupportsCordon: there
    // is no discovery signal or object marker for "can be cordoned", because
    // spec.unschedulable is a field of the core Node schema that an uncordoned node omits
    // entirely. Drain adds the signal there *is* one for: whether this server serves
    // pods/eviction.

    /// <summary>True when the selected row is a core/v1 Node this server says is patchable.</summary>
    public bool CanCordonSelectedRow => !HasMultipleSelection && OffersCordon;

    /// <summary>The row menu shows Cordon for this row; with several rows selected it shows it disabled (#305).</summary>
    public bool OffersCordon =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsCordon(descriptor)
        && !NodeActions.IsCordoned(row.Resource);

    /// <summary>
    /// Uncordon is offered only for a node that <em>is</em> cordoned, and cordon only for
    /// one that is not. Two commands, one slot: the menu shows whichever applies, which
    /// is the "a control pair where one half is always disabled is one control" rule the
    /// port-forward pane's Start/Stop settled (UI rule 11). Two commands rather than one
    /// toggle so that neither the palette nor a test has to infer which way it would go.
    /// </summary>
    public bool CanUncordonSelectedRow => !HasMultipleSelection && OffersUncordon;

    /// <inheritdoc cref="OffersCordon"/>
    public bool OffersUncordon =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsCordon(descriptor)
        && NodeActions.IsCordoned(row.Resource);

    /// <summary>True when this cluster serves <c>pods/eviction</c> and the row is a node.</summary>
    public bool CanDrainSelectedRow => !HasMultipleSelection && OffersDrain;

    /// <inheritdoc cref="OffersCordon"/>
    public bool OffersDrain =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsDrain(descriptor, PodDescriptorFor(row));

    [RelayCommand(CanExecute = nameof(CanCordonSelectedRow))]
    private void CordonSelected() => ArmRowAction(RowActionKind.Cordon);

    [RelayCommand(CanExecute = nameof(CanUncordonSelectedRow))]
    private void UncordonSelected() => ArmRowAction(RowActionKind.Uncordon);

    /// <summary>
    /// Arms a drain and reads the pods on the node so the strip can state what it would
    /// do — and refuse, by name, for the pods that need an option nobody has given. The
    /// plan is loaded before anything is evicted for the same reason the replica count is
    /// read before a scale: the confirm has to be about something real.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDrainSelectedRow))]
    private async Task DrainSelectedAsync()
    {
        if (ArmRowAction(RowActionKind.Drain) is { } action)
        {
            await action.LoadDrainPlanAsync();
        }
    }

    /// <summary>
    /// Builds the pending action for the selected row, against that row's own client and
    /// descriptor (its cluster's, in fleet mode — an action that resolved either from the
    /// tab would fire at the wrong cluster).
    /// </summary>
    private RowActionViewModel? ArmRowAction(RowActionKind kind, ResourceRowViewModel? targetRow = null, ResourceDescriptor? targetDescriptor = null, ClusterClient? targetClient = null)
    {
        // The list's own row actions act on one object, so not while several rows are
        // selected (#305); a row handed in by a detail pane is its own target.
        var row = targetRow ?? ActionRow;
        var descriptor = targetDescriptor ?? (row is null ? null : DescriptorFor(row));
        if (row is null || descriptor is null)
        {
            return null;
        }

        // Null only on the demo cluster, which is what the strip reads as "not available
        // here" — same shape as the exec and port-forward panes.
        var client = targetRow is not null ? targetClient : ClientFor(row);
        if (client is null && !IsDemo)
        {
            return null;
        }

        // One action at a time, and a running one is never replaced. It matters most for
        // a drain, whose eviction loop lives in the strip: re-arming over it would leave
        // the loop running with nothing on screen reporting it. Portainer reached the
        // same rule from the other direction (portainer#4006) — a drain should be issued
        // to one node at a time, and a single-slot strip is what enforces that here.
        if (PendingRowAction is { IsBusy: true } or { IsDraining: true } or { IsDeletingMany: true })
        {
            return null;
        }

        var action = new RowActionViewModel(
            kind, client, descriptor, row.Namespace, row.Name, ContextNameFor(row.ClusterName),
            kind == RowActionKind.Scale ? WorkloadActions.DeclaredReplicas(row.Resource) : null,
            PodDescriptorFor(row), JobDescriptorFor(row), EnvironmentOf(row.ClusterName));
        ConfigureCronJobAction(action, row, client);

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingRowAction, action))
            {
                PendingRowAction = null;
            }
        };

        PendingRowAction = action;
        if (RowActionViewModel.FiresOnClick(kind))
        {
            action.RunNow();
        }

        return action;
    }

    /// <summary>
    /// The container <c>kubectl exec</c> defaults to: the pod's
    /// <c>kubectl.kubernetes.io/default-container</c>, else its first container
    /// (<see cref="PodDetails.DefaultContainer"/>, FEAT-38) — a shell opened into an
    /// injected mesh proxy is not the shell anyone pressed S for. The pane's own picker is
    /// where a different one gets chosen.
    /// </summary>
    private static string FirstContainerOf(ResourceRowViewModel row) =>
        PodDetails.DefaultContainer(row.Resource.Raw) ?? "";

    /// <summary>Every TCP port every container declares, so the forward pane can offer them.</summary>
    private static IReadOnlyList<ContainerPort> DeclaredPortsOf(ResourceRowViewModel row)
    {
        var ports = new List<ContainerPort>();
        if (!row.Resource.Raw.TryGetProperty("spec", out var spec)
            || !spec.TryGetProperty("containers", out var containers)
            || containers.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return ports;
        }

        foreach (var container in containers.EnumerateArray())
        {
            if (!container.TryGetProperty("ports", out var portsEl)
                || portsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            foreach (var port in portsEl.EnumerateArray())
            {
                if (port.TryGetProperty("containerPort", out var cp) && cp.TryGetInt32(out var number)
                    && (!port.TryGetProperty("protocol", out var proto) || proto.GetString() is null or "TCP"))
                {
                    var name = port.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                    ports.Add(new ContainerPort(number, name));
                }
            }
        }

        return ports;
    }
}
