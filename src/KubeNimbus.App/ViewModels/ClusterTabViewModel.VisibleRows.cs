using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.ViewModels;

// The list's two collections and everything between them. Rows is the watch's own view of the
// cluster, VisibleRows is what the grid renders, and this file is the one place that knows the
// free-text filter, Unhealthy only and the sort exist (UI rule 13, pinned by
// ClusterTabRowFilterTests). Every producer writes to Rows; OnRowsChanged mirrors it here.
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// Every row the watch knows about — the informer's own view of the cluster.
    /// Added/Modified/Deleted are applied against this by key, so nothing may be
    /// removed from it for display reasons: a row filtered out of sight has to stay
    /// here, or the next watch event for that object would look like a fresh add.
    /// </summary>
    public RangeObservableCollection<ResourceRowViewModel> Rows { get; } = [];

    /// <summary>
    /// What the list actually renders: <see cref="Rows"/> minus whatever
    /// <see cref="RowFilter"/> excludes. Kept in sync from <see cref="Rows"/>'s own
    /// <c>CollectionChanged</c>, so every producer — the watch, the fleet merge, the
    /// demo dataset, the screenshot fixtures — keeps writing to <c>Rows</c> and
    /// exactly one place in the app knows the filter exists.
    /// </summary>
    public RangeObservableCollection<ResourceRowViewModel> VisibleRows { get; } = [];

    /// <summary>
    /// Free-text filter over the list, matched against the columns that identify an
    /// object (see <see cref="ResourceRowViewModel.Matches"/>). The sidebar filters
    /// <em>kinds</em>; nothing filtered the objects, so finding one pod in a namespace
    /// of two hundred meant scrolling — which is the one thing <c>kubectl get | grep</c>
    /// has always been for. Cleared when the selected kind changes: it is a question
    /// about the list it was typed into.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowFiltering))]
    private string _rowFilter = "";

    /// <summary>Trimmed <see cref="RowFilter"/>, cached so matching a 5000-row list
    /// doesn't re-trim the same string once per row.</summary>
    private string _rowQuery = "";

    public bool IsRowFiltering => _rowQuery.Length > 0;

    /// <summary>"12 of 87" — shown beside the box whenever a filter is on, because a
    /// list that is short and doesn't say why is indistinguishable from a small
    /// cluster.</summary>
    [ObservableProperty]
    private string _rowFilterSummary = "";

    partial void OnRowFilterChanged(string value)
    {
        // The filter TextBox's two-way binding can round-trip null on control
        // (re)creation — same reasoning as SelectedNamespace and SidebarFilter.
        _rowQuery = (value ?? "").Trim();
        RebuildVisibleRows();
    }

    [RelayCommand]
    private void ClearRowFilter() => RowFilter = "";

    /// <summary>
    /// The list's second narrowing, beside the search box: only rows whose health
    /// verdict is warn or error (<see cref="ResourceHealth.IsUnhealthy"/>). k9s's
    /// "toggle faults", and the answer to "what is broken here?" without sorting by
    /// Status and scanning.
    ///
    /// <para>
    /// It does not reopen UI rule 13's "the search box does not match status". That
    /// rule is about free text: "Running" typed into a name box would match most of a
    /// healthy list. This matches no text at all — it reads the same computed verdict
    /// that colours the status pill, so what it keeps is exactly what is drawn amber or
    /// red.
    /// </para>
    ///
    /// <para>
    /// A <em>mode</em>, unlike <see cref="RowFilter"/>: it is not a question about the
    /// list it was set on, so it survives a kind change (someone hunting for trouble
    /// wants it on Deployments after Pods). Per tab and never persisted — reopening the
    /// app on a filtered list that does not say why is the empty-looking-list bug with
    /// a restart in the middle. Bound two-way to the chip's <c>IsChecked</c> with no
    /// command beside it (UI rule 8b); the work is in the changed hook.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isUnhealthyOnly;

    partial void OnIsUnhealthyOnlyChanged(bool value) => RefreshHealthFilter(rebuild: true);

    /// <summary>
    /// Whether the selected kind's rows carry a health verdict at all. A kind with no
    /// status summary (ConfigMap, Secret, Service…) has every row at
    /// <see cref="ResourceHealth.Idle"/>, so "unhealthy only" there would be a list
    /// that is always empty — the toggle is disabled instead, and says why.
    /// </summary>
    public bool CanFilterUnhealthy =>
        SelectedKind is { IsHelmReleases: false, IsArgoDashboard: false } kind
        && ResourceStatusSummary.ShowsStatus(kind.Descriptor);

    /// <summary>
    /// The mode is on <em>and</em> means something for this kind. A field rather than a
    /// computed property because <see cref="MatchesRowFilter"/> reads it once per row
    /// per rebuild, and the kind lookup behind <see cref="CanFilterUnhealthy"/> builds a
    /// string.
    /// </summary>
    private bool _healthFilterActive;

    public bool IsHealthFiltering => _healthFilterActive;

    /// <summary>The chip's tooltip: what it does and its key, or why it cannot.</summary>
    public string UnhealthyToggleTip
    {
        get
        {
            var kind = SelectedKind?.DisplayName ?? "rows";
            var key = CommandBindings.ShortcutLabel(CommandId.ToggleUnhealthyOnly);
            if (CanFilterUnhealthy)
            {
                return $"Show only unhealthy {kind} — warnings and errors, the rows drawn amber or red ({key} in the list)";
            }

            return IsUnhealthyOnly
                ? $"{kind} carry no health status, so there is nothing to narrow to. Unhealthy-only stays on and applies again on a kind that has one."
                : $"{kind} carry no health status, so there is nothing to narrow to.";
        }
    }

    /// <summary>
    /// "Show every row" in the all-healthy empty state. An explicit target rather than
    /// an inversion (UI rule 8b's pattern): it can never race the chip's own toggle.
    /// </summary>
    [RelayCommand]
    private void ShowAllRows() => IsUnhealthyOnly = false;

    private void RefreshHealthFilter(bool rebuild)
    {
        OnPropertyChanged(nameof(CanFilterUnhealthy));
        OnPropertyChanged(nameof(UnhealthyToggleTip));

        var active = IsUnhealthyOnly && CanFilterUnhealthy;
        if (active == _healthFilterActive)
        {
            return;
        }

        _healthFilterActive = active;
        OnPropertyChanged(nameof(IsHealthFiltering));
        if (rebuild)
        {
            RebuildVisibleRows();
        }
    }

    private bool MatchesRowFilter(ResourceRowViewModel row) =>
        (_rowQuery.Length == 0 || row.Matches(_rowQuery))
        && (!_healthFilterActive || row.IsUnhealthy);

    /// <summary>
    /// Which column the list is ordered by (a <see cref="ResourceColumn"/> id), or null
    /// for the watch's own arrival order — which is the default and a real third state,
    /// not "unsorted by accident": it is the order the informer holds the objects in,
    /// and clicking a header a third time comes back to it.
    /// </summary>
    [ObservableProperty]
    private string? _sortColumnId;

    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>
    /// The header-click cycle: this column ascending, this column descending, then off.
    /// Three states rather than the two a DataGrid gives by default, because the third
    /// is the only way back to arrival order — and on a live list that order is
    /// information, not noise (it is what puts a newly created object where the watch
    /// put it).
    /// </summary>
    internal void ToggleSort(string columnId)
    {
        if (!string.Equals(SortColumnId, columnId, StringComparison.Ordinal))
        {
            SetSort(columnId, descending: false);
        }
        else if (!SortDescending)
        {
            SetSort(columnId, descending: true);
        }
        else
        {
            SetSort(null, descending: false);
        }
    }

    /// <summary>
    /// Sets the sort and re-orders what is on screen. Persisted per kind, so the choice
    /// survives switching to another kind and back (and a restart) — see
    /// <see cref="GridLayoutStore"/>.
    /// </summary>
    internal void SetSort(string? columnId, bool descending, bool persist = true)
    {
        SortColumnId = columnId;
        SortDescending = descending;
        RebuildVisibleRows();

        if (persist && GridLayoutKey is { } key)
        {
            // Stored relative to the kind's default: choosing the default back is "no
            // choice" (null, so the layout can drop out of the file), and clearing the
            // sort on a kind that *has* a default is a choice of its own (Unsorted) —
            // otherwise the default would come back the next time the kind was opened.
            var (defaultColumn, defaultDescending) = DefaultSortFor(SelectedKind?.Descriptor);
            var (storedColumn, storedDescending) =
                columnId == defaultColumn && (columnId is null || descending == defaultDescending)
                    ? ((string?)null, false)
                    : (columnId ?? ResourceColumn.Unsorted, descending);

            GridLayoutStore.Update(key, layout => layout with
            {
                SortColumn = storedColumn,
                SortDescending = storedDescending,
            });
        }
    }

    /// <summary>
    /// The key this kind's column widths and sort are remembered under, or null where
    /// there is nothing to remember: no kind selected, or one of the two browsers that
    /// replace the resource list entirely — Helm and the Argo dashboard — each of which is a
    /// different grid with its own columns.
    /// </summary>
    internal string? GridLayoutKey =>
        !IsHelmView && !IsArgoView && SelectedKind?.Descriptor is { } descriptor
            ? GridLayoutStore.KeyFor(descriptor)
            : null;

    /// <summary>
    /// The comparer for the current sort, or null when the list is in arrival order.
    /// Rebuilt per sort pass rather than cached: it closes over the printer columns,
    /// which arrive asynchronously the first time a CRD kind is opened.
    /// </summary>
    private ResourceRowComparer? RowComparer =>
        SortColumnId is { } columnId && ResourceRowComparer.CanSort(columnId, VisiblePrinterColumns)
            ? new ResourceRowComparer(columnId, SortDescending, VisiblePrinterColumns)
            : null;

    private void RebuildVisibleRows()
    {
        // The selection is restored rather than left to the DataGrid, which clears it on
        // a Clear() — losing the row someone had selected because they typed into the
        // search box (or sorted the list they were reading) is a small bug with a
        // disproportionate cost: the row actions and the inspector all hang off it.
        var selected = SelectedRow;

        // One Reset rather than a Clear and an Add per row: this runs on every keystroke in
        // the search box, and on 5,000 pods that was 5,000 notifications into the grid each.
        var visible = Rows.Where(MatchesRowFilter).ToList();
        if (RowComparer is { } comparer)
        {
            visible.Sort(comparer);
        }

        VisibleRows.ReplaceAll(visible);

        if (selected is not null && VisibleRows.Contains(selected))
        {
            SelectedRow = selected;
        }

        RecomputeListEmpty();
    }

    /// <summary>
    /// Raised after a re-sort moved rows, with the rows of a multi-row selection that are still
    /// listed, for the view to select again. <c>SelectedItems</c> is not bindable, so the view does it.
    /// </summary>
    public event EventHandler<IReadOnlyList<ResourceRowViewModel>>? MultipleSelectionRestored;

    /// <summary>
    /// Puts a multi-row selection back after a re-sort moved rows (#301, FEAT-6). A metrics poll
    /// on a list sorted by CPU that leaves a few rows out of place moves each one (a Remove and an
    /// Insert), and the DataGrid drops its selection on the Remove: three rows selected for a bulk
    /// delete or restart came back as one (<c>ux-bulk-selection-resort</c>). A poll that
    /// reshuffles the list replaces it with one Reset instead, and Avalonia 12's DataGrid keeps the
    /// selection of items still listed across a Reset (checked in the same scenario), so that path
    /// needs nothing. Only rows still listed are kept.
    /// </summary>
    private void RestoreMultipleSelection(IReadOnlyList<ResourceRowViewModel>? selection)
    {
        if (selection is not { Count: > 1 })
        {
            return;
        }

        var listed = new HashSet<ResourceRowViewModel>(VisibleRows);
        IReadOnlyList<ResourceRowViewModel> kept = [.. selection.Where(listed.Contains)];
        if (kept.Count < 2)
        {
            return;
        }

        SetSelectedRows(kept);
        MultipleSelectionRestored?.Invoke(this, kept);
    }

    /// <summary>
    /// Where a row belongs in the sorted list — a binary search, so a watch tick on a
    /// sorted 5000-row list costs a handful of comparisons rather than a re-sort.
    /// </summary>
    private int SortedIndexFor(ResourceRowViewModel row, ResourceRowComparer comparer, int limit = -1)
    {
        var low = 0;
        var high = limit < 0 ? VisibleRows.Count : limit;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (comparer.Compare(VisibleRows[middle], row) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Re-orders the rendered list after its sort keys changed under it — the metrics
    /// poll, on a CPU- or memory-sorted list.
    ///
    /// <para>
    /// A few rows out of place are moved one by one, an insertion pass: each out-of-order
    /// row goes to where it belongs among the rows above it, so a list that has barely
    /// moved costs a comparison per row and a notification per moved row. Past
    /// <see cref="InPlaceResortLimit"/> the list is sorted off to the side and replaced
    /// with one Reset instead, because a poll rewrites every row's usage at once and on a
    /// 5,000-pod list the insertion pass was about 10,000 notifications into the grid and
    /// two seconds of frozen window every fifteen seconds (measured by the harness's
    /// stress mode). The Reset costs nothing the reader can see: the DataGrid keeps its
    /// scroll offset across one (checked against Avalonia 12's), and the selection is put
    /// back here.
    /// </para>
    /// </summary>
    /// <summary>Out-of-order rows past which <see cref="ResortVisibleRows"/> replaces the list rather than moving rows.</summary>
    internal const int InPlaceResortLimit = 32;

    internal void ResortVisibleRows()
    {
        if (RowComparer is not { } comparer)
        {
            return;
        }

        var outOfOrder = 0;
        for (var i = 1; i < VisibleRows.Count && outOfOrder <= InPlaceResortLimit; i++)
        {
            if (comparer.Compare(VisibleRows[i - 1], VisibleRows[i]) > 0)
            {
                outOfOrder++;
            }
        }

        if (outOfOrder > InPlaceResortLimit)
        {
            var selected = SelectedRow;
            // Stable, like the insertion pass: rows with equal usage keep their order
            // rather than trading places on every poll.
            VisibleRows.ReplaceAll(VisibleRows.OrderBy(row => row, comparer).ToList());
            if (selected is not null)
            {
                SelectedRow = selected;
            }

            return;
        }

        var before = HasMultipleSelection ? _selectedRows : null;
        var moved = false;
        for (var i = 1; i < VisibleRows.Count; i++)
        {
            var row = VisibleRows[i];
            if (comparer.Compare(VisibleRows[i - 1], row) <= 0)
            {
                continue;
            }

            VisibleRows.RemoveAt(i);
            VisibleRows.Insert(SortedIndexFor(row, comparer, i), row);
            moved = true;
        }

        // A moved row is a Remove and an Insert, and a DataGrid drops the selection of a row
        // removed under it, so a multi-row selection is put back here.
        if (moved)
        {
            RestoreMultipleSelection(before);
        }
    }

    /// <summary>
    /// Moves a row that a watch event has just changed back to where the sort says it
    /// belongs — a Modified can change the very value the list is ordered by (a pod
    /// going CrashLoopBackOff while the list is sorted by Status is the case that
    /// matters), and a sorted list that quietly stops being sorted is worse than an
    /// unsorted one. Does nothing while the row is still in order, so a status refresh
    /// that changes nothing relevant moves nothing on screen.
    /// </summary>
    private void RepositionRow(ResourceRowViewModel row)
    {
        if (RowComparer is not { } comparer)
        {
            return;
        }

        var index = VisibleRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var inOrder = (index == 0 || comparer.Compare(VisibleRows[index - 1], row) <= 0)
            && (index == VisibleRows.Count - 1 || comparer.Compare(row, VisibleRows[index + 1]) <= 0);
        if (inOrder)
        {
            return;
        }

        // A Modified that moves a selected row (a pod going CrashLoopBackOff on a list sorted
        // by Status) drops it from the grid's selection the same way a poll's move does.
        var before = HasMultipleSelection ? _selectedRows : null;
        VisibleRows.RemoveAt(index);
        VisibleRows.Insert(Math.Min(SortedIndexFor(row, comparer), VisibleRows.Count), row);
        RestoreMultipleSelection(before);
    }

    /// <summary>
    /// What a Modified does to the rendered list. The watch updates the row object in
    /// place (it never replaces it), so nothing reaches <see cref="OnRowsChanged"/> —
    /// and with unhealthy-only on, a Modified is precisely what changes whether the row
    /// belongs on screen: a pod going CrashLoopBackOff has to appear, and one that
    /// recovers has to leave. The text search needs it too: most identity fields (name,
    /// namespace) never change under an object, but a kind's own ones can — an Ingress's
    /// hosts (<see cref="RowFilterFields"/>) — so an edit can move a row in or out of a search.
    ///
    /// <para>
    /// The row is inserted where it would have been all along — the sort's position, or
    /// its place in <see cref="Rows"/>' arrival order — never appended, so turning the
    /// mode off and on again produces the same list the events produced.
    /// </para>
    /// </summary>
    private void RefreshRowVisibility(ResourceRowViewModel row)
    {
        if (!_healthFilterActive && !IsRowFiltering)
        {
            RepositionRow(row);
            return;
        }

        var index = VisibleRows.IndexOf(row);
        var belongs = MatchesRowFilter(row);

        if (index >= 0 && !belongs)
        {
            VisibleRows.RemoveAt(index);
        }
        else if (index < 0 && belongs)
        {
            VisibleRows.Insert(
                RowComparer is { } comparer ? SortedIndexFor(row, comparer) : ArrivalIndexFor(row),
                row);
        }
        else if (index >= 0)
        {
            RepositionRow(row);
        }
    }

    /// <summary>
    /// A pane updated one of this list's rows in place from its own read — workload
    /// detail's Refresh — which, like a watch Modified, reaches no <c>CollectionChanged</c>.
    /// Only a row the watch still holds is re-evaluated: a pane opened by owner navigation,
    /// or left open after the list moved to another kind, holds a row that is not in
    /// <see cref="Rows"/>, and inserting that into <see cref="VisibleRows"/> would put an
    /// object on screen that the list does not have.
    /// </summary>
    internal void RowRefreshedInPlace(ResourceRowViewModel row)
    {
        if (Rows.Any(r => ReferenceEquals(r, row)))
        {
            RefreshRowVisibility(row);
        }
    }

    /// <summary>
    /// Where a row belongs in an unsorted <see cref="VisibleRows"/>, which is then a
    /// subsequence of <see cref="Rows"/>: count the visible rows that come before it in
    /// arrival order. One walk over both lists, and only on a visibility change.
    /// </summary>
    private int ArrivalIndexFor(ResourceRowViewModel row)
    {
        var visible = 0;
        foreach (var candidate in Rows)
        {
            if (ReferenceEquals(candidate, row))
            {
                break;
            }

            if (visible < VisibleRows.Count && ReferenceEquals(VisibleRows[visible], candidate))
            {
                visible++;
            }
        }

        return visible;
    }

    /// <summary>
    /// Mirrors <see cref="Rows"/> into <see cref="VisibleRows"/> through the filter and
    /// the sort.
    /// Rows only ever appends (watch, fleet merge, demo dataset) or removes by object,
    /// so those two cases are handled incrementally and a watch tick on a filtered
    /// list costs one match, not a rescan; anything else — a Clear, an insert in the
    /// middle — falls back to a rebuild rather than guessing at an index.
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add
                when e.NewItems is { } added && e.NewStartingIndex + added.Count == Rows.Count:
                var comparer = RowComparer;
                var matching = added.OfType<ResourceRowViewModel>().Where(MatchesRowFilter).ToList();

                // Appended in arrival order, in one notification however many a batch of
                // watch events added — or inserted where the sort puts it, since a sorted
                // list that appends new objects at the bottom is a list that stops being
                // sorted the moment anything is created. Past a few dozen, one re-sort and
                // Reset is cheaper than an insert and a notification per row.
                if (comparer is null)
                {
                    VisibleRows.AddRange(matching);
                }
                else if (matching.Count > InPlaceResortLimit)
                {
                    RebuildVisibleRows();
                    return; // already recomputed the counters
                }
                else
                {
                    foreach (var row in matching)
                    {
                        VisibleRows.Insert(SortedIndexFor(row, comparer), row);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed:
                if (removed.Count > InPlaceResortLimit)
                {
                    RebuildVisibleRows();
                    return;
                }

                foreach (var row in removed.OfType<ResourceRowViewModel>())
                {
                    VisibleRows.Remove(row);
                }

                break;

            default:
                RebuildVisibleRows();
                return; // already recomputed the counters
        }

        RecomputeListEmpty();
    }
}
