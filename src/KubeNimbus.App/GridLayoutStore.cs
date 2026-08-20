using KubeNimbus.Core;

namespace KubeNimbus.App;

/// <summary>
/// One remembered column width. The unit matters as much as the number, because the
/// grid's columns are not all the same shape and a drag does different things to them:
/// dragging a <c>*</c> column rewrites its <b>star value</b> (Avalonia keeps the unit
/// and re-derives the ratio, so 2* becomes 2.52*), while dragging an <c>Auto</c> column
/// leaves its declared width alone and only changes what it displays. So a star column
/// is remembered as a ratio — which reproduces the same proportional layout at any
/// window width — and everything else as the pixels it ended up at.
/// </summary>
/// <param name="Unit"><c>star</c> or <c>px</c>. A value this build does not know is
/// ignored on read rather than failing the file, same as every other setting.</param>
public sealed record GridColumnWidth(string Unit, double Value)
{
    public const string Star = "star";
    public const string Pixels = "px";
}

/// <summary>
/// How one kind's list is laid out: what each column was dragged to, and which column
/// it is sorted by. Both are per kind — a Pod list and a ConfigMap list have different
/// columns and answer different questions, so a width chosen for one says nothing about
/// the other.
/// </summary>
public sealed record GridLayout(
    Dictionary<string, GridColumnWidth>? ColumnWidths = null,
    string? SortColumn = null,
    bool SortDescending = false)
{
    public static GridLayout Empty { get; } = new();

    public IReadOnlyDictionary<string, GridColumnWidth> Widths => ColumnWidths ?? [];
}

/// <summary>
/// Reads and writes the per-kind grid layouts in <c>workspace.json</c>.
///
/// <para>
/// Session state rather than a preference, by this repo's own test (CLAUDE.md,
/// "Settings, and what belongs in which file"): deleting the workspace should lose how
/// the window looked and nothing else, and a column width is exactly that. It sits
/// beside the open tabs rather than in <c>settings.json</c> for the same reason.
/// </para>
///
/// <para>
/// Every write is a read-modify-write of the file, never of a cached snapshot — the
/// same rule <c>App.Update</c> follows and for the same reason: the shell writes the
/// workspace too (tabs, pins, environment overrides), and two writers holding separate
/// snapshots silently revert each other. There are two writers <em>within</em> this
/// feature as well, which is why <see cref="Update"/> takes a function over the layout
/// rather than a replacement: the view owns the widths (they are pixels, and only the
/// grid knows them) and the view model owns the sort (it is what orders the rows), and
/// each must be able to change its own half without carrying the other's.
/// </para>
/// </summary>
public static class GridLayoutStore
{
    /// <summary>
    /// The key a kind's layout is remembered under: its API group and Kind, which is
    /// what identifies a kind everywhere else in this app. The <em>version</em> is
    /// deliberately not in it — a cluster upgrading a CRD from v1beta1 to v1 is still
    /// the same list to the person who widened its Name column — and neither is the
    /// cluster, so a width chosen for Pods holds across every cluster in the window.
    /// </summary>
    public static string KeyFor(ResourceDescriptor descriptor) =>
        $"{descriptor.Group}/{descriptor.Kind}";

    public static GridLayout Load(string kindKey)
    {
        var layouts = WorkspaceStore.Load().GridLayouts;
        return layouts is not null && layouts.TryGetValue(kindKey, out var layout) && layout is not null
            ? layout
            : GridLayout.Empty;
    }

    public static void Update(string kindKey, Func<GridLayout, GridLayout> change)
    {
        var settings = WorkspaceStore.Load();
        var layouts = settings.GridLayouts is null
            ? []
            : new Dictionary<string, GridLayout>(settings.GridLayouts, StringComparer.Ordinal);

        var current = layouts.TryGetValue(kindKey, out var existing) && existing is not null
            ? existing
            : GridLayout.Empty;

        var updated = change(current);

        // A layout that is back to its defaults is removed rather than stored as an
        // empty record: the file is a list of the choices somebody made, and a kind
        // whose sort was cleared and whose columns were never dragged has made none.
        if (updated.SortColumn is null && (updated.ColumnWidths is null || updated.ColumnWidths.Count == 0))
        {
            layouts.Remove(kindKey);
        }
        else
        {
            layouts[kindKey] = updated;
        }

        WorkspaceStore.Save(settings with { GridLayouts = layouts });
    }
}
