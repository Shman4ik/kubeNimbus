using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Views;

/// <summary>
/// The view half of "logs from a row", shared by every list that names pods — the resource
/// list, workload detail's and node detail's pod lists, an Argo Application's resources —
/// so the keys and the Shift+click are decided once rather than per view (L3's stated
/// risk: duplicated key handling per view).
///
/// <para>
/// <b>The keys come from the catalog.</b> <see cref="MatchLogsKey"/> matches L and Shift+L
/// through <see cref="CommandBindings.Matches"/> against the same <c>CommandScope.List</c>
/// rows the resource list uses, so a pane can never answer to a key the cheat sheet does not
/// name, and <c>Matches</c> compares modifiers exactly, so Shift+L is never also L.
/// </para>
///
/// <para>
/// <b>The Shift+click is read off the release.</b> A Button's <c>Click</c> carries no
/// modifiers, and the release is what raises it, so <see cref="Track"/> records the
/// modifiers in the Tunnel phase — before the button's own class handler runs — and
/// <see cref="TakeShift"/> reads and clears them in the click handler.
/// </para>
///
/// <para>
/// <b>A right click selects the row under it.</b> A DataGrid selects on left-click only,
/// so without this a row's context menu acts on whatever was selected <em>before</em> the
/// right click — Logs opening a different pod than the one the menu opened over, and on
/// the resource list, Delete. Every list that tracks this gesture has such a menu, which
/// is why it lives here rather than once per view (the resource list had its own copy,
/// and the panes' pod lists had none).
/// </para>
/// </summary>
internal sealed class RowLogsGesture
{
    private KeyModifiers _modifiers;

    private RowLogsGesture()
    {
    }

    /// <summary>Starts recording pointer-release modifiers for every row icon under <paramref name="host"/>.</summary>
    public static RowLogsGesture Track(Interactive host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var gesture = new RowLogsGesture();
        host.AddHandler(InputElement.PointerReleasedEvent, gesture.OnPointerReleased, RoutingStrategies.Tunnel);
        if (host is DataGrid grid)
        {
            grid.AddHandler(InputElement.PointerPressedEvent, SelectRowUnderRightClick, RoutingStrategies.Tunnel);
        }

        return gesture;
    }

    /// <summary>
    /// Selects the row a right click landed on. Not handled, so the context menu still
    /// opens normally; this only fixes which row it is about. DataGrid 12 already selects
    /// on a right click over a <em>cell</em>; what it misses is the rest of the row — the
    /// gaps a row's padding leaves, and the strip past the last column, where nothing
    /// hit-tests and the event's source is the grid itself (UI rule 8). So the row is
    /// resolved from the source when the source is inside one, and otherwise by which
    /// realized row spans the pointer's height.
    /// </summary>
    private static void SelectRowUnderRightClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
        {
            return;
        }

        for (var element = e.Source as Visual; element is not null; element = element.GetVisualParent())
        {
            if (element is DataGridRow row)
            {
                grid.SelectedItem = row.DataContext;
                return;
            }

            if (ReferenceEquals(element, grid))
            {
                break;
            }
        }

        // Only inside the rows area: a row scrolled half under the column headers still
        // spans a header's height, and a right click on a header is not about that row.
        if (grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault() is not { } rows
            || !new Rect(rows.Bounds.Size).Contains(e.GetPosition(rows)))
        {
            return;
        }

        var y = e.GetPosition(grid).Y;
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.IsEffectivelyVisible && row.TranslatePoint(default, grid) is { } top
                && y >= top.Y && y < top.Y + row.Bounds.Height)
            {
                grid.SelectedItem = row.DataContext;
                return;
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _modifiers = e.KeyModifiers;

    /// <summary>Whether Shift was held on the release that raised this click; clears it.</summary>
    public bool TakeShift()
    {
        var shift = _modifiers.HasFlag(KeyModifiers.Shift);
        _modifiers = KeyModifiers.None;
        return shift;
    }

    /// <summary>
    /// True for Shift+L (full-size), false for L, null for any other key — which the caller
    /// then leaves unhandled.
    /// </summary>
    public static bool? MatchLogsKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (CommandBindings.Matches(CommandId.PodLogsMaximized, e))
        {
            return true;
        }

        return CommandBindings.Matches(CommandId.PodLogs, e) ? false : null;
    }

    /// <summary>
    /// Puts focus back on the list a row icon was clicked in, so L, Shift+L and the arrow
    /// keys keep working without a second click — the icon itself is not focusable. A
    /// no-op in a plain ItemsControl, which has no keyboard selection to return to.
    /// </summary>
    public static void FocusOwningList(object? source)
    {
        for (var element = source as Visual; element is not null; element = element.GetVisualParent())
        {
            if (element is DataGrid or ListBox)
            {
                ((InputElement)element).Focus();
                return;
            }
        }
    }
}
