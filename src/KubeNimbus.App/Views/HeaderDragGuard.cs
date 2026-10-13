using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace KubeNimbus.App.Views;

/// <summary>
/// A press on a column header that moves before it is released is a drag, not a click, and
/// must not sort (#320, QA check P-4). A drag that starts a pixel off a header's resize grip
/// lands on the header itself, and the grid, with column reordering off, sorts on any release
/// over the header however far the pointer went: the reader meant to widen the column and
/// the list reordered under them.
/// </summary>
/// <remarks>
/// Class handlers, installed once from <c>App.Initialize</c> like <see cref="AutomationNames"/>,
/// so every grid gets it. The grid's own <c>Sorting</c> handlers ask
/// <see cref="IsDrag"/> before they sort; the flag is set by a move past
/// <see cref="Threshold"/> while the header is pressed, and cleared by the next press (a
/// reset posted from the release ran before the grid's <c>Sorting</c> in the harness).
/// </remarks>
internal static class HeaderDragGuard
{
    /// <summary>How far a press may wander and still be a click, in DIPs.</summary>
    internal const double Threshold = 4;

    private static bool _installed;
    private static Point? _pressedAt;
    private static bool _isDrag;

    /// <summary>True while the header press now ending moved past the threshold.</summary>
    internal static bool IsDrag => _isDrag;

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.PointerPressedEvent.AddClassHandler<DataGridColumnHeader>(OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<DataGridColumnHeader>(OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<DataGridColumnHeader>(OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPressed(DataGridColumnHeader header, PointerPressedEventArgs e)
    {
        _isDrag = false;
        _pressedAt = e.GetCurrentPoint(null).Properties.IsLeftButtonPressed ? e.GetPosition(null) : null;
    }

    private static void OnMoved(DataGridColumnHeader header, PointerEventArgs e)
    {
        if (_pressedAt is { } start && !_isDrag)
        {
            var at = e.GetPosition(null);
            _isDrag = Math.Abs(at.X - start.X) > Threshold || Math.Abs(at.Y - start.Y) > Threshold;
        }
    }

    private static void OnReleased(DataGridColumnHeader header, PointerReleasedEventArgs e) =>
        // The flag stays until the next press: the grid raises Sorting after the release has
        // been routed (a reset posted from here ran first and the drag sorted anyway, seen
        // in the harness), and the only way to sort again is another press, which clears it.
        _pressedAt = null;
}
