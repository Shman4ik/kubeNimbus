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
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
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
    /// production — any row on a production cluster — always asks, as a single delete does.
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
