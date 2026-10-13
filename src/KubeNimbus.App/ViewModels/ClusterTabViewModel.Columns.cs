namespace KubeNimbus.App.ViewModels;

// The column chooser (FEAT-13). The kind decides which columns exist (ResourceStatusSummary,
// the CRD's printer columns, the metrics and fleet gates); the reader decides which of those
// to hide. The hidden set is session state beside the widths and the sort, in the same
// per-kind GridLayout record in workspace.json — see docs/engineering/resource-grid-resize-sort.md.
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The columns the reader has hidden on the selected kind, by id
    /// (<see cref="ResourceColumn"/>, or <c>crd:&lt;name&gt;</c>). Read back from the store
    /// when the kind changes; the view applies it after every one of its own visibility
    /// passes, so a hidden column stays hidden whichever pass ran last.
    /// </summary>
    public IReadOnlySet<string> HiddenColumns => _hiddenColumns;

    private HashSet<string> _hiddenColumns = new(StringComparer.Ordinal);

    /// <summary>
    /// The columns that identify a row and therefore cannot be hidden: Name, and on the
    /// Events list (which has no Name column) the Object the event is about. A list with
    /// neither is a list of values nobody can act on, and the chooser would be the only way
    /// back to it. The health dot is not offered at all — it has no header to name it by.
    /// </summary>
    public static bool IsColumnAlwaysShown(string columnId) =>
        columnId is ResourceColumn.Name or ResourceColumn.EventObject or ResourceColumn.Health;

    /// <summary>
    /// Raised after the hidden set or the remembered widths change from the chooser, so the
    /// view re-runs its visibility passes and re-applies the kind's widths. A property name
    /// rather than an event, so it travels the same <c>PropertyChanged</c> path every other
    /// column notification does.
    /// </summary>
    public const string ColumnLayoutChangedProperty = "ColumnLayout";

    /// <summary>Hides a shown column or shows a hidden one, and remembers the choice for this kind.</summary>
    public void SetColumnHidden(string columnId, bool hidden)
    {
        if (IsColumnAlwaysShown(columnId) || GridLayoutKey is not { } key)
        {
            return;
        }

        var changed = hidden ? _hiddenColumns.Add(columnId) : _hiddenColumns.Remove(columnId);
        if (!changed)
        {
            return;
        }

        var snapshot = _hiddenColumns.Order(StringComparer.Ordinal).ToList();
        GridLayoutStore.Update(key, layout => layout with { HiddenColumns = snapshot.Count == 0 ? null : snapshot });

        // #301: the sort arrow is drawn in its column's header, so a list sorted by a column
        // it no longer shows would be in an order nothing on screen explains (UI rule 20).
        // Hiding that column clears the sort, as the third header click would.
        if (hidden && string.Equals(SortColumnId, columnId, StringComparison.Ordinal))
        {
            SetSort(null, descending: false);
        }

        OnPropertyChanged(ColumnLayoutChangedProperty);
    }

    /// <summary>
    /// The sort a kind opens on, unless its column is hidden: a remembered or default sort by
    /// a column the reader hid would order the list with no arrow to say so (#301).
    /// </summary>
    private (string? Column, bool Descending) ShownSort((string? Column, bool Descending) sort) =>
        sort.Column is { } column && _hiddenColumns.Contains(column) ? (null, false) : sort;

    /// <summary>Whether this kind's columns differ from what it ships with: something hidden or dragged.</summary>
    public bool HasCustomColumns =>
        GridLayoutKey is { } key && GridLayoutStore.Load(key) is { } layout
        && (layout.Hidden.Count > 0 || layout.Widths.Count > 0);

    /// <summary>
    /// "Reset columns": every column the kind shows comes back, at the width the list
    /// declares. The sort is left alone — it is a different question, with its own way back
    /// (the third header click).
    /// </summary>
    public void ResetColumns()
    {
        if (GridLayoutKey is not { } key)
        {
            return;
        }

        _hiddenColumns.Clear();
        GridLayoutStore.Update(key, layout => layout with { HiddenColumns = null, ColumnWidths = null });
        OnPropertyChanged(ColumnLayoutChangedProperty);
    }

    /// <summary>
    /// Raised to ask the view to open the column chooser from the keyboard (#301): over the
    /// list's header row, focus on its first column that can be hidden, and focus given back
    /// when it closes. The palette's "Choose the list's columns…" is the way in, since a
    /// column header is not a keyboard stop. A property name, like
    /// <see cref="ColumnLayoutChangedProperty"/>.
    /// </summary>
    public const string ColumnChooserRequestedProperty = "ColumnChooserRequested";

    /// <summary>Whether the list on screen has columns to choose: a kind is listed, with a stored layout.</summary>
    public bool CanChooseColumns => IsResourceListVisible && GridLayoutKey is not null;

    /// <summary>Opens the column chooser from the keyboard; nothing when the list has none to choose.</summary>
    public void RequestColumnChooser()
    {
        if (CanChooseColumns)
        {
            OnPropertyChanged(ColumnChooserRequestedProperty);
        }
    }

    /// <summary>Reads the kind's hidden set back as the kind changes (no write: reading a choice is not making one).</summary>
    private void LoadHiddenColumns(GridLayout layout)
    {
        _hiddenColumns = new HashSet<string>(layout.Hidden.Where(id => !IsColumnAlwaysShown(id)), StringComparer.Ordinal);
    }
}
