using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// Extended selection in the resource list, and the one action that uses it: delete (FEAT-6).
// SelectedRow stays the row every single-object command acts on — the grid's current row —
// and SelectedRows is the whole selection, pushed in by the view from the grid's
// SelectionChanged (a DataGrid's SelectedItems is not a bindable property).
public sealed partial class ClusterTabViewModel
{
    private IReadOnlyList<ResourceRowViewModel> _selectedRows = [];

    /// <summary>Every selected row, in the grid's selection order; empty or one row in the ordinary case.</summary>
    public IReadOnlyList<ResourceRowViewModel> SelectedRows => _selectedRows;

    /// <summary>True when more than one row is selected — the state the bulk delete and the count exist for.</summary>
    public bool HasMultipleSelection => _selectedRows.Count > 1;

    /// <summary>
    /// "3 selected", beside the list's other counts in the header row, only while more than
    /// one row is selected: one selected row is what the highlight already says (UI rule 20).
    /// </summary>
    public string SelectionSummary => HasMultipleSelection ? $"{_selectedRows.Count} selected" : "";

    /// <summary>
    /// The row a single-object action acts on: the current row while one row is selected, and
    /// none while several are (#305, the owner's decision of 2026-10-12). With three nodes
    /// highlighted, Cordon used to cordon the grid's current row alone and leave the other two
    /// highlighted and untouched; now every action that acts on one object is disabled, with
    /// <see cref="SingleRowActionTip"/> saying why, and Delete — which acts on every selected
    /// row — is the one that stays.
    /// </summary>
    private ResourceRowViewModel? ActionRow => HasMultipleSelection ? null : SelectedRow;

    /// <summary>
    /// The tooltip a disabled single-object row action shows while several rows are selected;
    /// null otherwise, when each item keeps its own tooltip.
    /// </summary>
    public string? SingleRowActionTip => HasMultipleSelection
        ? $"Acts on one object, and {_selectedRows.Count} rows are selected. Select one row to use it; Delete acts on all of them."
        : null;

    /// <summary>Called by the view on the grid's SelectionChanged, and with nothing when the kind changes.</summary>
    public void SetSelectedRows(IReadOnlyList<ResourceRowViewModel> rows)
    {
        if (rows.Count == _selectedRows.Count && rows.SequenceEqual(_selectedRows))
        {
            return;
        }

        _selectedRows = [.. rows];
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SingleRowActionTip));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        NotifySingleRowActions();
    }

    /// <summary>
    /// Every single-object row action reads <see cref="ActionRow"/>, so a change in how many rows
    /// are selected re-asks each of them — the same set a <c>SelectedRow</c> change notifies.
    /// </summary>
    private void NotifySingleRowActions()
    {
        foreach (var name in (string[])
        [
            nameof(HasSelectedRow), nameof(IsPodRowSelected), nameof(CanOpenPodLogsForSelectedRow),
            nameof(CanAggregateLogsForSelectedRow), nameof(CanOpenLogsForSelectedRow), nameof(CanPortForwardSelected),
            nameof(CanScaleSelectedRow), nameof(CanRestartSelectedRow), nameof(CanDrainSelectedRow),
            nameof(CanSyncSelectedArgoApplication), nameof(CanTerminateSelectedArgoSync),
        ])
        {
            OnPropertyChanged(name);
        }

        OpenLogsCommand.NotifyCanExecuteChanged();
        OpenPreviousLogsCommand.NotifyCanExecuteChanged();
        OpenWorkloadLogsCommand.NotifyCanExecuteChanged();
        OpenLogsMaximizedCommand.NotifyCanExecuteChanged();
        ExecIntoSelectedCommand.NotifyCanExecuteChanged();
        PortForwardSelectedCommand.NotifyCanExecuteChanged();
        EditSelectedYamlCommand.NotifyCanExecuteChanged();
        ScaleSelectedCommand.NotifyCanExecuteChanged();
        RestartSelectedCommand.NotifyCanExecuteChanged();
        DrainSelectedCommand.NotifyCanExecuteChanged();
        SyncArgoApplicationCommand.NotifyCanExecuteChanged();
        SyncArgoApplicationWithPruneCommand.NotifyCanExecuteChanged();
        RefreshArgoApplicationCommand.NotifyCanExecuteChanged();
        TerminateArgoSyncCommand.NotifyCanExecuteChanged();

        // Cordon, uncordon, run now, suspend, resume and the storage link.
        NotifyStateDependentRowActions();
    }

    /// <summary>
    /// The rows a Delete acts on: the whole selection when there is more than one row and
    /// every one of them can be deleted, otherwise the current row alone.
    /// </summary>
    private IReadOnlyList<(ResourceRowViewModel Row, ResourceDescriptor Descriptor)> DeleteTargets()
    {
        var rows = HasMultipleSelection ? _selectedRows : SelectedRow is { } one ? [one] : [];
        var targets = new List<(ResourceRowViewModel, ResourceDescriptor)>(rows.Count);
        foreach (var row in rows)
        {
            if (DescriptorFor(row) is not { } descriptor || !WorkloadActions.SupportsDelete(descriptor))
            {
                // A kind the server will not delete is refused for the whole selection rather
                // than quietly dropped from it: the count in the sentence must be the count
                // that is deleted.
                return [];
            }

            targets.Add((row, descriptor));
        }

        return targets;
    }

    /// <summary>
    /// Arms one strip for the whole selection. The sentence names the count and the cluster
    /// (the row's own, in a fleet list; "on 2 clusters" when the selection spans several), and
    /// it always asks, whatever "Confirm before deleting" says (#305); the production border
    /// marks a selection with any row on a production cluster.
    /// </summary>
    private RowActionViewModel? ArmBulkDelete()
    {
        var targets = DeleteTargets();
        if (targets.Count < 2)
        {
            return null;
        }

        if (PendingRowAction is { IsBusy: true } or { IsDraining: true } or { IsDeletingMany: true })
        {
            return null;
        }

        var bulk = new List<BulkTarget>(targets.Count);
        foreach (var (row, descriptor) in targets)
        {
            var client = ClientFor(row);
            if (client is null && !IsDemo)
            {
                return null;
            }

            bulk.Add(new BulkTarget(client, descriptor, row.Namespace, row.Name, ContextNameFor(row.ClusterName)));
        }

        var environment = targets.Any(t => EnvironmentOf(t.Row.ClusterName) == ClusterEnvironment.Production)
            ? ClusterEnvironment.Production
            : EnvironmentOf(targets[0].Row.ClusterName);
        var first = targets[0];
        var action = new RowActionViewModel(
            RowActionKind.Delete, bulk[0].Client, first.Descriptor, first.Row.Namespace, first.Row.Name,
            bulk[0].ClusterName, environment: environment, bulk: bulk);

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingRowAction, action))
            {
                PendingRowAction = null;
            }
        };

        PendingRowAction = action;
        return action;
    }
}
