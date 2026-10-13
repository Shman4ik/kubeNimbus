using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// Applying watch events to Rows by key: one at a time, in batches, and tagged by namespace or by
// fleet member. Nothing here consults the row filter; VisibleRows follows Rows by itself.
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// Applies one watch event to <see cref="Rows"/> by key. Nothing here consults the
    /// row filter, and that is the invariant, not an omission: <c>Rows</c> is the
    /// informer's own view of the cluster, so a row hidden by the filter has to stay in
    /// it — drop it and the next Modified for that object finds no entry in
    /// <see cref="_rowsByKey"/> and reads as a fresh add, which resurfaces the row in
    /// the middle of a filtered list. Pinned by <c>ClusterTabRowFilterTests</c>.
    ///
    /// Internal rather than private only so that test can drive this path for real;
    /// nothing else in the app calls it.
    /// </summary>
    internal void Apply(ResourceEvent<DynamicResource> evt)
    {
        ApplyOne(evt);
        RecomputeListEmpty();
    }

    /// <summary>
    /// A batch of watch events, as <see cref="AsyncBatching.InBatches"/> delivers them: one
    /// hop onto the UI thread for everything that had arrived. New rows are appended to
    /// <see cref="Rows"/> together, so an initial list of 5,000 objects reaches the grid in a
    /// few notifications rather than 5,000.
    /// </summary>
    internal void ApplyBatch(IReadOnlyList<ResourceEvent<DynamicResource>> events)
    {
        _stagedRows = [];
        try
        {
            foreach (var evt in events)
            {
                ApplyOne(evt);
            }
        }
        finally
        {
            FlushStagedRows();
            _stagedRows = null;
        }

        RecomputeListEmpty();
    }

    /// <summary>The fleet counterpart of <see cref="ApplyBatch"/>.</summary>
    internal void ApplyFleetBatch(IReadOnlyList<FleetResourceEvent> events)
    {
        _stagedRows = [];
        try
        {
            foreach (var evt in events)
            {
                ApplyFleetOne(evt);
            }
        }
        finally
        {
            FlushStagedRows();
            _stagedRows = null;
        }

        RecomputeListEmpty();
    }

    // New rows of the batch being applied, not yet in Rows. Null outside a batch. Anything
    // that reads Rows or a row's visibility flushes them first, so a batch has exactly the
    // effect its events would have had one at a time.
    private List<ResourceRowViewModel>? _stagedRows;

    private void AddRow(ResourceRowViewModel row)
    {
        if (_stagedRows is { } staged)
        {
            staged.Add(row);
        }
        else
        {
            Rows.Add(row);
        }
    }

    private void FlushStagedRows()
    {
        if (_stagedRows is { Count: > 0 } staged)
        {
            Rows.AddRange(staged.ToList());
            staged.Clear();
        }
    }

    private void ApplyOne(ResourceEvent<DynamicResource> evt)
    {
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                // A Reset is the *start* of a list, not the end of one, so it turns the
                // loading state back on rather than off. Ending it here is what made a
                // distant cluster render "No pods found" for the whole duration of the
                // list request — the empty state and the loading state swapped places,
                // which is the exact failure UI rule 18 exists to prevent. Synced below
                // is the honest end; the first row arriving is the other one.
                IsListLoading = true;
                _stagedRows?.Clear();
                Rows.Clear();
                _rowsByKey.Clear();
                ConnectionWarning = null;
                break;

            case ResourceEventType.Synced:
                // Everything that existed when the sync started has been delivered. For
                // an empty namespace this is the only frame that ever arrives, so it is
                // what settles "no objects" against "not there yet".
                IsListLoading = false;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } resource:
                // The first row is enough to stop waiting: the list paginates, and
                // hiding page one behind a spinner until page four lands is the same
                // unresponsiveness in the other direction.
                IsListLoading = false;
                if (_rowsByKey.TryGetValue(resource.Key, out var existing))
                {
                    FlushStagedRows();
                    existing.Update(resource);
                    RefreshRowVisibility(existing);
                }
                else
                {
                    var row = new ResourceRowViewModel(resource);
                    row.SetPrinterColumns(VisiblePrinterColumns);
                    _rowsByKey[resource.Key] = row;
                    ApplyLastUsage(resource.Key, row);
                    AddRow(row);
                }

                break;

            case ResourceEventType.Deleted when evt.Resource is { } resource:
                IsListLoading = false;
                FlushStagedRows();
                if (_rowsByKey.Remove(resource.Key, out var removed))
                {
                    Rows.Remove(removed);
                }

                break;
        }
    }

    /// <summary>
    /// Fleet-mode counterpart of <see cref="Apply"/>. The one thing it must not do is
    /// treat a Reset as "clear the list": a Reset is scoped to the cluster that sent it
    /// (initial sync, or a relist after 410 Gone), so clearing everything would wipe
    /// four healthy clusters because the fifth reconnected.
    ///
    /// Internal for the same reason <see cref="Apply"/> is — the cluster-qualified keys
    /// make this a second way to get the filter/informer split wrong.
    /// </summary>
    internal void ApplyFleet(FleetResourceEvent tagged)
    {
        ApplyFleetOne(tagged);
        RecomputeListEmpty();
    }

    private void ApplyFleetOne(FleetResourceEvent tagged)
    {
        var cluster = tagged.ClusterName;

        switch (tagged.Event.Type)
        {
            case ResourceEventType.Synced:
                // Any one member finishing its list is enough to stop waiting: partial
                // is the normal state of a fleet view and the header already says how
                // many clusters are in it, so holding the spinner for the slowest
                // member would hide four healthy lists behind the fifth.
                IsListLoading = false;
                break;

            case ResourceEventType.Reset:
            {
                // One pass and one notification, not a Rows.Remove per row: a member
                // relisting 5,000 pods after a 410 Gone was a linear search and two
                // notifications (Rows and VisibleRows) per row.
                FlushStagedRows();
                var stale = new HashSet<ResourceRowViewModel>();
                foreach (var key in _rowsByKey
                    .Where(entry => string.Equals(entry.Value.ClusterName, cluster, StringComparison.Ordinal))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    if (_rowsByKey.Remove(key, out var row))
                    {
                        stale.Add(row);
                    }
                }

                if (stale.Count > 0)
                {
                    Rows.ReplaceAll(Rows.Where(row => !stale.Contains(row)).ToList());
                }

                ConnectionWarning = null;
                break;
            }

            case ResourceEventType.Added or ResourceEventType.Modified when tagged.Event.Resource is { } added
                && _fleetNamespaceFilter is { } only && !only.Contains(added.Namespace ?? ""):
                // Read because the cluster was read whole; not one of the chosen namespaces.
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when tagged.Event.Resource is { } added:
                IsListLoading = false;
                var addedKey = ResourceRowViewModel.KeyFor(cluster, added.Key);
                if (_rowsByKey.TryGetValue(addedKey, out var existing))
                {
                    FlushStagedRows();
                    existing.Update(added);
                    RefreshRowVisibility(existing);
                }
                else
                {
                    var row = new ResourceRowViewModel(added, cluster);
                    row.SetPrinterColumns(VisiblePrinterColumns);
                    _rowsByKey[addedKey] = row;
                    ApplyLastUsage(addedKey, row);
                    AddRow(row);
                }

                break;

            case ResourceEventType.Deleted when tagged.Event.Resource is { } deleted:
                IsListLoading = false;
                FlushStagedRows();
                if (_rowsByKey.Remove(ResourceRowViewModel.KeyFor(cluster, deleted.Key), out var gone))
                {
                    Rows.Remove(gone);
                }

                break;
        }
    }
}
