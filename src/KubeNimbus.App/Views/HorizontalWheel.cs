using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace KubeNimbus.App.Views;

/// <summary>
/// Turns a vertical mouse wheel into horizontal scrolling over a strip that only scrolls
/// sideways: the dock's tab strip and the command bar's cluster tabs (#313). Most mice have
/// only a vertical wheel, and a <see cref="ScrollViewer"/> whose vertical bar is disabled
/// ignores it, so with ten tabs open the strip moved only through its thin overlay bar or
/// the keyboard. A horizontal wheel (or a touchpad's sideways swipe) is left to the
/// scroll viewer, which already handles it.
/// </summary>
internal static class HorizontalWheel
{
    /// <summary>Pixels per wheel notch, the same step a <see cref="ScrollViewer"/> takes.</summary>
    internal const double Step = 50;

    /// <summary>
    /// Handles the wheel on <paramref name="host"/> before its scroll viewer sees it. The
    /// viewer is the first one inside the host, or the host itself.
    /// </summary>
    internal static void Install(Control host) =>
        host.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control host || e.Delta.Y == 0 || e.Delta.X != 0 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var viewer = host as ScrollViewer ?? host.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        var room = viewer.Extent.Width - viewer.Viewport.Width;
        if (room <= 0)
        {
            return;
        }

        // Wheel down (a negative delta) moves on to the tabs at the right, as it moves down
        // a page.
        var x = Math.Clamp(viewer.Offset.X - e.Delta.Y * Step, 0, room);
        viewer.Offset = viewer.Offset.WithX(x);
        e.Handled = true;
    }
}
