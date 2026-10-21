using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The Argo CD dashboard's reads and actions: reloading the Application list, opening one, and
// arming sync, sync with prune, refresh and terminate on the row-action strip (argo-cd.md).
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The Application kind's descriptor on this cluster, or null when Argo CD is not
    /// installed. Read from the discovery catalog the sidebar was built from, so the version
    /// is whatever this server serves.
    /// </summary>
    private ResourceDescriptor? ArgoApplicationDescriptor()
    {
        var catalog = SidebarSections
            .SelectMany(s => s.Kinds)
            .Select(k => k.Descriptor);

        return ArgoCd.ApplicationDescriptor(catalog);
    }

    /// <summary>
    /// Reloads the Argo dashboard. Cluster-wide rather than namespace-scoped, deliberately:
    /// Applications almost always live in one namespace (<c>argocd</c>) while the workloads
    /// they manage are spread across the rest, so a dashboard that followed the namespace
    /// picker would be empty everywhere except the one place nobody browses.
    /// </summary>
    [RelayCommand]
    private Task ReloadArgoAsync()
    {
        // Not behind a failed reconnect's view (#311), for the reason HelmBrowserViewModel.RefreshAsync gives.
        if ((Client is null && !IsDemo) || _reconnectFailed)
        {
            return Task.CompletedTask;
        }

        if (ArgoApplicationDescriptor() is not { } descriptor)
        {
            StopWatch();
            ArgoRefusal = null;
            IsArgoEmpty = true;
            return Task.CompletedTask;
        }

        // Only the warning this method set itself is cleared (#306): the tab's warning line
        // carries others too (a reconnect notice, a watch that dropped), and a new Argo watch
        // says nothing about them.
        if (ConnectionWarning?.StartsWith(ArgoReadWarning, StringComparison.Ordinal) == true)
        {
            ConnectionWarning = null;
        }

        // The fleet list (#336) and this cluster's own dashboard (#303) are watches
        // (ClusterTabViewModel.ArgoWatch.cs). The fleet list is one merged list with a Cluster
        // column and the counts summed (owner decision on #195); each row carries its own
        // cluster's descriptor and reaches its own cluster's client through ClientForArgoRow.
        if (IsFleetView && FleetMembersProvider?.Invoke() is { Count: > 0 } members)
        {
            StartArgoFleetWatch(members);
            return Task.CompletedTask;
        }

        if (Client is { } client)
        {
            StartArgoWatch(client, descriptor);
            return Task.CompletedTask;
        }

        // The demo cluster: its dataset, read once.
        StopWatch();
        var previous = SelectedArgoApplication is { } selected ? selected.Application.Key : null;
        ArgoRefusal = null;
        ArgoApplications.Clear();
        var applications = Demo.DemoData.ArgoApplications;
        foreach (var row in applications.Select(a => new ArgoApplicationRowViewModel(a, descriptor)).Order(ArgoSort.Comparer))
        {
            ArgoApplications.Add(row);
        }

        SelectedArgoApplication = ArgoApplications.FirstOrDefault(a => a.Application.Key == previous) ?? ArgoApplications.FirstOrDefault();
        ArgoCounts = ArgoSummary.Of(applications);
        IsArgoLoading = false;
        IsArgoEmpty = ArgoApplications.Count == 0;
        OnPropertyChanged(nameof(ArgoAttentionSummary));
        return Task.CompletedTask;
    }

    /// <summary>How every warning <see cref="ReloadArgoAsync"/> sets begins, so it clears only its own.</summary>
    internal const string ArgoReadWarning = "Could not read Argo CD Applications";

    private bool HasSelectedArgoApplication => SelectedArgoApplication is not null;

    /// <summary>
    /// The client of the cluster an Argo row came from. The dashboard's fleet list has its own
    /// watch, not the resource list's, so <see cref="ClientForCluster"/>'s targets do not know its
    /// clusters; the shell's members do. A row whose cluster has left the fleet since gets
    /// null — never this tab's own client, which would sync somebody else's namesake.
    /// </summary>
    private ClusterClient? ClientForArgoRow(ArgoApplicationRowViewModel row) =>
        row.ClusterName.Length == 0
            ? Client
            : FleetMembersProvider?.Invoke().FirstOrDefault(m => m.ClusterName == row.ClusterName)?.Client;

    /// <summary>Double-click / Enter on an Application row: opens its resources, conditions and history.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedArgoApplication))]
    private void OpenArgoApplication()
    {
        if (SelectedArgoApplication is not { } row || (Client is null && !IsDemo))
        {
            return;
        }

        var key = ArgoApplicationTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(
            new ArgoApplicationTabViewModel(
                ClientForArgoRow(row),
                row.Descriptor,
                row.Application,
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName),
                row.ClusterName,
                NamedLogsOpener(row.ClusterName, ClientForCluster(row.ClusterName))),
            replacePreview: false);
    }

    /// <summary>
    /// Whether the window is showing this tab's Applications mode rather than its Resources
    /// mode. The mode is the shell's, not the tab's, so the shell stamps this on every tab it
    /// holds; null (a tab nobody stamped, as in most tests) reads as Resources.
    /// </summary>
    public Func<bool>? IsApplicationsModeShown { get; set; }

    /// <summary>
    /// The Applications mode's Argo CD Application on screen: the one whose page is open, or
    /// else the list's selected row. Null when neither is an Argo Application (#364).
    /// </summary>
    private ArgoApplication? ApplicationsModeArgoApplication() =>
        (Applications.Page?.Entry ?? Applications.SelectedRow?.Entry)?.Argo;

    /// <summary>
    /// The Application a sync or refresh would act on. In the Applications mode, the one on
    /// screen there (#364: it used to fall through to whatever the Resources mode last had
    /// selected, so the palette named and terminated an Application nobody was looking at).
    /// In the Resources mode, the dashboard's selected row while the dashboard is showing, and
    /// otherwise the selected row of an ordinary Applications list. Two entry points to one
    /// action, because the ordinary list is still there and a menu item that worked in one of
    /// them and not the other would be the harder thing to explain.
    /// </summary>
    private (ClusterClient? Client, ResourceDescriptor Descriptor, string Namespace, string Name, string Cluster)?
        ArgoActionTarget()
    {
        if (IsApplicationsModeShown?.Invoke() == true)
        {
            return ApplicationsModeArgoApplication() is { } app && ArgoApplicationDescriptor() is { } argoDescriptor
                ? (Client, argoDescriptor, app.Namespace, app.Name, "")
                : null;
        }

        if (IsArgoView)
        {
            return SelectedArgoApplication is { } row
                ? (ClientForArgoRow(row), row.Descriptor, row.Namespace, row.Name, row.ClusterName)
                : null;
        }

        if (ActionRow is { } selected
            && DescriptorFor(selected) is { } descriptor
            && ArgoCd.SupportsSync(descriptor))
        {
            return (ClientFor(selected), descriptor, selected.Namespace, selected.Name, selected.ClusterName);
        }

        return null;
    }

    /// <summary>
    /// The row menu shows the Argo items for an Applications row; with several rows selected
    /// they stay, disabled (#305). The dashboard's own menu keeps <see cref="CanSyncSelectedArgoApplication"/>.
    /// </summary>
    public bool OffersArgoSyncOnRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor && ArgoCd.SupportsSync(descriptor);

    /// <summary>
    /// Whether a sync or refresh is offered at all. The kind is named inside
    /// <see cref="ArgoCd.SupportsSync"/>, and that is the honest exception argued there:
    /// <c>operation</c> is a field of Argo's own schema, so neither discovery nor the object
    /// can answer this the way a <c>scale</c> subresource or a pod template can.
    /// </summary>
    public bool CanSyncSelectedArgoApplication => ArgoActionTarget() is not null;

    /// <summary>
    /// "argocd/checkout" — what a sync or refresh would act on, for the palette's subtitle.
    /// Null when neither surface has an Application selected, which is the same condition
    /// <see cref="CanSyncSelectedArgoApplication"/> reports and is why the palette can gate
    /// on either one.
    /// </summary>
    public string? ArgoActionLabel =>
        ArgoActionTarget() is { } target ? $"{target.Namespace}/{target.Name}" : null;

    /// <summary>Sync, sent on the click (UI rule 17); the strip above the list is its result line.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncSelectedArgoApplication))]
    private void SyncArgoApplication() => ArmArgoAction(RowActionKind.ArgoSync);

    /// <summary>Sync with prune, which deletes, so it arms the confirm instead of firing.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncSelectedArgoApplication))]
    private void SyncArgoApplicationWithPrune() => ArmArgoAction(RowActionKind.ArgoSyncPrune);

    [RelayCommand(CanExecute = nameof(CanSyncSelectedArgoApplication))]
    private void RefreshArgoApplication() => ArmArgoAction(RowActionKind.ArgoRefresh);

    /// <summary>
    /// Whether the selected Application has a sync to stop: its operation phase is Running,
    /// read from the dashboard row or from the ordinary list's object. The phase can move on
    /// before the click lands; the terminate patch tests it again on the server, and the strip
    /// says so when the sync had already finished.
    /// </summary>
    public bool CanTerminateSelectedArgoSync =>
        ArgoActionTarget() is not null
        && (IsApplicationsModeShown?.Invoke() == true
            ? ApplicationsModeArgoApplication()?.CanTerminate == true
            : IsArgoView
            ? SelectedArgoApplication?.Application.CanTerminate == true
            : SelectedRow is { } row && ArgoCd.OperationPhaseOf(row.Resource) == "Running");

    /// <summary>Terminate the running sync. It stops Argo partway, so it asks first (UI rule 17).</summary>
    [RelayCommand(CanExecute = nameof(CanTerminateSelectedArgoSync))]
    private void TerminateArgoSync() => ArmArgoAction(RowActionKind.ArgoTerminate);

    private void ArmArgoAction(RowActionKind kind)
    {
        if (ArgoActionTarget() is not { } target)
        {
            return;
        }

        // The Applications mode hides the Resources view and the strip on it, so an action
        // armed there would ask, or report, where nobody can see it (#364, UI rules 9 and 17).
        // It goes to the strip that is on screen instead: the open page's, or with the list
        // showing, the list's own, which the row menu reports on too.
        if (IsApplicationsModeShown?.Invoke() == true)
        {
            if (Applications.Page is { } page)
            {
                page.ArmArgo(kind);
            }
            else
            {
                Applications.ArmArgoOnSelectedRow(kind);
            }

            return;
        }

        // Null only on the demo cluster, where the strip renders its refusal in place rather
        // than the action silently doing nothing (demo rule 5).
        if (target.Client is null && !IsDemo)
        {
            return;
        }

        if (PendingRowAction is { IsBusy: true } or { IsDraining: true } or { IsDeletingMany: true })
        {
            return;
        }

        var action = new RowActionViewModel(
            kind, target.Client, target.Descriptor, target.Namespace, target.Name, ContextNameFor(target.Cluster),
            environment: EnvironmentOf(target.Cluster));

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
    }
}
