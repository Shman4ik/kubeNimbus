using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// What survives the list starting over: the selected row (by key, not by object) and the pod and
// node panes that read a row of this list (ILiveRowTab). A Refresh, a namespace change or coming
// back to the kind builds new row objects, and before this both were held by object: Refresh (F5)
// dropped the selection and left an open pod pane reading a row nothing updated any more, and
// reopening the pod found that stale pane by key (aot-watch-and-discovery.md, "A relist is a diff").
public sealed partial class ClusterTabViewModel
{
    // The key (ResourceRowViewModel.Key) of the row to select again once the list delivers it:
    // set by Refresh and by a Reset, cleared when it is found, when the reader selects something
    // else, and when the list has synced without it (the object is gone).
    private string? _reselectKey;

    private IEnumerable<ILiveRowTab> LiveTabs => InspectorTabs.OfType<ILiveRowTab>();

    /// <summary>
    /// The list is about to drop every row (a Reset, or <see cref="RestartWatch"/> from Refresh):
    /// remembers the selected row's key and clears the selection, which the grid does anyway once
    /// the row it selected is no longer listed.
    /// </summary>
    private void RememberSelectionForRelist()
    {
        if (!IsFleetView && SelectedRow is { } selected)
        {
            _reselectKey = selected.Key;
        }

        SelectedRow = null;
    }

    /// <summary>Selects the remembered row once it is listed and visible.</summary>
    private void TrySelectRemembered()
    {
        if (_reselectKey is not { } key)
        {
            return;
        }

        if (SelectedRow is not null)
        {
            // The reader chose something while the list was arriving: theirs wins.
            _reselectKey = null;
            return;
        }

        if (VisibleRows.FirstOrDefault(row => row.Key == key) is { } match)
        {
            _reselectKey = null;
            SelectedRow = match;
        }
    }

    /// <summary>
    /// A new row object entered the list: a pod or node pane that shows the same object reads it
    /// from now on, and is no longer marked deleted if it was.
    /// </summary>
    private void RebindLiveTabs(ResourceRowViewModel row)
    {
        if (SelectedKind?.Descriptor is not { } descriptor)
        {
            return;
        }

        foreach (var tab in LiveTabs)
        {
            if (tab.Row.Key == row.Key && tab.IsOf(descriptor) && (!ReferenceEquals(tab.Row, row) || tab.IsDeleted))
            {
                tab.Rebind(row);
            }
        }
    }

    /// <summary>The list said the object is gone: a pane showing it says so.</summary>
    private void MarkLiveTabsDeleted(string rowKey)
    {
        if (SelectedKind?.Descriptor is not { } descriptor)
        {
            return;
        }

        foreach (var tab in LiveTabs)
        {
            if (tab.Row.Key == rowKey && tab.IsOf(descriptor))
            {
                tab.MarkDeleted();
            }
        }
    }

    /// <summary>
    /// The list has synced (every namespace of a several-namespace list has): the remembered
    /// selection is found or dropped, and a pod or node pane whose object this list covers but no
    /// longer holds is marked deleted. That catches the deletion a Refresh or a namespace change
    /// would otherwise hide: the object went while no Deleted could reach the pane.
    /// </summary>
    private void ConcludeListSync()
    {
        FlushStagedRows();
        TrySelectRemembered();
        _reselectKey = null;

        if (IsFleetView || SelectedKind?.Descriptor is not { } descriptor)
        {
            return;
        }

        var scope = WatchNamespaces(descriptor);
        foreach (var tab in LiveTabs.ToList())
        {
            var resource = tab.Row.Resource;

            // A row of a fleet list names its cluster; this tab's own list's rows do not.
            if (tab.Row.ClusterName.Length > 0 || !tab.IsOf(descriptor)
                || !(scope.Contains(null) || scope.Contains(resource.Namespace)))
            {
                continue;
            }

            if (_rowsByKey.TryGetValue(resource.Key, out var listed))
            {
                if (!ReferenceEquals(listed, tab.Row) || tab.IsDeleted)
                {
                    tab.Rebind(listed);
                }
            }
            else
            {
                tab.MarkDeleted();
            }
        }
    }
}
