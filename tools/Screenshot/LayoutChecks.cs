using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Layout claims the narrow scenarios exist to show, asserted rather than left to someone
/// looking at the PNG: a control pushed off the right edge renders perfectly — it is just
/// not there — which is how ENG-32 went unnoticed across several trains.
/// </summary>
internal static class LayoutChecks
{
    /// <summary>ENG-32: search, the unhealthy chip and Refresh all end inside the window.</summary>
    internal static void ListHeaderFits(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        foreach (var name in new[] { "RowFilterBox", "UnhealthyToggle", "RefreshButton" })
        {
            var control = view.FindControl<Control>(name)
                ?? throw new InvalidOperationException($"No control named {name} in the list header.");
            var right = RightEdge(control, window);
            if (!control.IsEffectivelyVisible || right > window.Bounds.Width + 0.5)
                throw new InvalidOperationException(
                    $"{name} ends at x={right:0} in a {window.Bounds.Width:0}px window — pushed off the list header (ENG-32).");
        }

        Console.WriteLine($"List header fits at {window.Bounds.Width:0}px (search, unhealthy chip, Refresh).");
    }

    /// <summary>ENG-35: the palette keeps a 16px gutter each side when the window is narrower than it.</summary>
    internal static void PaletteFollowsWindow(Window window)
    {
        var box = window.FindControl<TextBox>("PaletteQueryBox")
            ?? throw new InvalidOperationException("No palette query box.");
        var panel = box.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("overlayCard"));
        var left = panel.TranslatePoint(default, window)?.X ?? double.NaN;
        var right = RightEdge(panel, window);
        if (left < 16 - 0.5 || right > window.Bounds.Width - 16 + 0.5)
            throw new InvalidOperationException(
                $"The palette spans x={left:0}..{right:0} in a {window.Bounds.Width:0}px window; it should keep a 16px gutter (ENG-35).");

        Console.WriteLine($"Palette follows the window ({panel.Bounds.Width:0}px in {window.Bounds.Width:0}px).");
    }

    /// <summary>
    /// ENG-6: when the list's columns are wider than the grid, the grid scrolls sideways —
    /// the rightmost column is reachable rather than cut off.
    /// </summary>
    internal static void GridReachesLastColumn(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        var columns = grid.Columns.Where(c => c.IsVisible).Sum(c => c.ActualWidth);
        var bar = grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var viewport = grid.Bounds.Width;
        Console.WriteLine(
            $"Grid columns {columns:0}px in a {viewport:0}px grid; horizontal bar visible={bar?.IsVisible}, max={bar?.Maximum:0}.");
        if (columns > viewport + 0.5 && (bar is null || !bar.IsVisible || bar.Maximum <= 0))
            throw new InvalidOperationException(
                $"The list's columns need {columns:0}px of a {viewport:0}px grid and it does not scroll sideways (ENG-6).");
    }

    /// <summary>
    /// A log pane scrolled to its end, which is where Follow keeps it, leaves its last line
    /// clear of the horizontal scroll bar. Fluent's scroll bars hide themselves and are drawn
    /// over the content rather than beside it, so the last line sat under the bar until the
    /// panes gave their content a bottom margin. Left scrolled to the end, so the PNG shows it.
    /// </summary>
    internal static void LogEndClearsScrollBar(Window window)
    {
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(s => s.Name == "LogScroll" && s.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("No visible log pane.");
        var items = scroll.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "LogItems");

        // A log shorter than its pane has no end to scroll to, and would pass without
        // having measured anything.
        if (scroll.Extent.Height <= scroll.Bounds.Height + 0.5)
            throw new InvalidOperationException(
                $"The log pane's {items.ItemCount} lines fit its {scroll.Bounds.Height:0}px, so there was nothing to scroll and nothing was checked.");

        // Twice: the list is virtualized, and the first pass can land on an estimated extent.
        for (var i = 0; i < 2; i++)
        {
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var last = items.ContainerFromIndex(items.ItemCount - 1)
            ?? throw new InvalidOperationException("The log's last line has no row after scrolling to the end.");
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)?.Y ?? double.PositiveInfinity;
        var bar = scroll.TryFindResource("ScrollBarSize", out var size) && size is double d ? d : 10;
        var gap = scroll.Bounds.Height - bottom;
        if (gap < bar - 0.5)
            throw new InvalidOperationException(
                $"Scrolled to its end, the log pane's last line ends {gap:0.#}px above the pane's bottom edge, under the {bar:0}px horizontal scroll bar.");

        Console.WriteLine($"Log pane's last line ends {gap:0}px above its bottom edge (scroll bar {bar:0}px).");
    }

    /// <summary>
    /// ENG-51: a log pane's bar keeps every one of its tools inside the pane at a narrow
    /// window, and its search box wide enough to type in. Below about 1150px the right-hand
    /// tools used to run off the pane's edge, the ⋯ menu (Clear, Save) first.
    /// </summary>
    internal static void LogBarFits(Window window)
    {
        var checkedBars = 0;
        foreach (var bar in window.GetVisualDescendants().OfType<StackPanel>()
                     .Where(p => p.Name == "LogBarTools" && p.IsEffectivelyVisible))
        {
            var pane = bar.GetVisualAncestors().OfType<UserControl>().First();
            var paneRight = RightEdge(pane, window);
            var limit = Math.Min(paneRight, window.Bounds.Width);
            var problems = new List<string>();
            foreach (var control in bar.Children.Where(c => c.IsVisible))
            {
                var right = RightEdge(control, window);
                if (right > limit + 0.5)
                    problems.Add($"{Describe(control)} ends at x={right:0}, past the pane's edge at {limit:0}");
            }

            var search = pane.FindControl<TextBox>("LogSearchBox")
                ?? throw new InvalidOperationException("No LogSearchBox in a log pane.");
            var searchRight = RightEdge(search, window);
            var barLeft = bar.TranslatePoint(default, window)?.X ?? double.NaN;
            if (search.Bounds.Width < MinLogSearchWidth - 0.5)
                problems.Add($"the search box is {search.Bounds.Width:0}px, narrower than the {MinLogSearchWidth}px it needs to be typed in");
            if (searchRight > barLeft + 0.5)
                problems.Add($"the search box ends at x={searchRight:0}, under the tools that start at x={barLeft:0}");

            // Whatever left the bar is in the ⋯ menu instead, and nothing is in both places.
            var moved = new List<string>();
            foreach (var (slotName, menuName) in new[] { ("RangeSlot", "MenuRange"), ("LevelsSlot", "MenuLevels"), ("ContextSlot", "MenuContext"), ("CopySlot", "MenuCopy") })
            {
                var slot = pane.FindControl<Control>(slotName) ?? throw new InvalidOperationException($"No {slotName}.");
                var menu = pane.FindControl<Control>(menuName) ?? throw new InvalidOperationException($"No {menuName}.");
                if (slot.IsVisible == menu.IsVisible)
                    problems.Add($"{slotName} and {menuName} are both {(slot.IsVisible ? "shown" : "hidden")}");
                if (!slot.IsVisible) moved.Add(slotName.Replace("Slot", "", StringComparison.Ordinal));
            }

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"{pane.GetType().Name}'s log bar does not fit a {window.Bounds.Width:0}px window (ENG-51): {string.Join("; ", problems)}.");

            Console.WriteLine(
                $"{pane.GetType().Name}'s log bar fits {window.Bounds.Width:0}px: search {search.Bounds.Width:0}px, tools end at {RightEdge(bar, window):0} of {limit:0}, "
                + $"in the menu: {(moved.Count == 0 ? "nothing" : string.Join(", ", moved))}.");
            checkedBars++;
        }

        if (checkedBars == 0)
            throw new InvalidOperationException("No visible log bar, so nothing was checked.");
    }

    /// <summary>Room for the placeholder and a few typed characters beside the box's own buttons.</summary>
    private const double MinLogSearchWidth = 160;

    private static string Describe(Control control) =>
        control.Name is { Length: > 0 } name ? name
        : ToolTip.GetTip(control) is string tip ? $"\"{tip}\""
        : control.GetType().Name;

    private static double RightEdge(Visual control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width, 0), root)?.X ?? double.PositiveInfinity;
}
