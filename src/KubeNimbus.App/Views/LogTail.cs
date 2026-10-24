using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace KubeNimbus.App.Views;

/// <summary>
/// The log panes' scroll lock, shared by the pod log pane and the workload log pane. Following
/// pins the view to the last line only while the view is already there, which is what every
/// terminal pager and log viewer does: scrolling up to read on a chatty pod must not be yanked
/// back by the next line.
/// </summary>
internal static class LogTail
{
    /// <summary>
    /// How close to the bottom still counts as "at the bottom". A couple of lines of slack, so
    /// a stray wheel notch doesn't silently detach the follow.
    /// </summary>
    public const double Slack = 24;

    public static bool IsAtBottom(double offsetY, double extentHeight, double viewportHeight) =>
        offsetY >= extentHeight - viewportHeight - Slack;

    public static bool IsAtBottom(ScrollViewer scroll) =>
        IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height);

    /// <summary>
    /// True when the pane was on the last line before this scroll change and a change of its
    /// own height, not a scroll, took it off (#403). A <see cref="ScrollViewer"/> keeps its top
    /// offset when its viewport shrinks, so a following pane made shorter — the dock going back
    /// from maximized (Shift+L, then Esc) to the split — showed the lines that used to sit at
    /// the top of the tall pane, and the next line then found it "scrolled up" and stopped
    /// following for good.
    /// </summary>
    public static bool ResizeLeftTheTail(
        Vector offset, Size extent, Size viewport, Vector offsetDelta, Vector extentDelta, Vector viewportDelta)
    {
        if (viewportDelta.Y == 0)
        {
            return false;
        }

        var wasAtBottom = IsAtBottom(
            offset.Y - offsetDelta.Y, extent.Height - extentDelta.Y, viewport.Height - viewportDelta.Y);
        return wasAtBottom && !IsAtBottom(offset.Y, extent.Height, viewport.Height);
    }

    /// <summary>
    /// Keeps a following pane on its last line through a change of the pane's height. The
    /// new-line half of the lock stays with each view, which knows its own collection.
    /// </summary>
    public static void KeepOnResize(ScrollViewer scroll, Func<bool> isFollowing)
    {
        scroll.ScrollChanged += (_, e) =>
        {
            if (isFollowing()
                && ResizeLeftTheTail(scroll.Offset, scroll.Extent, scroll.Viewport, e.OffsetDelta, e.ExtentDelta, e.ViewportDelta))
            {
                // Posted for the same reason as the new-line case: this runs inside layout.
                Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
            }
        };
    }
}
