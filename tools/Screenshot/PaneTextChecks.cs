using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using Nimbus.Ui.Converters;

namespace KubeNimbus.Screenshot;

/// <summary>#320: what the panes say and how their grids read, at the sizes the QA run met them.</summary>
internal static class PaneTextChecks
{
    /// <summary>
    /// <c>cluster-tab-crd-printer-columns-960</c>: no column header is cut without an
    /// ellipsis, and a cut one carries its whole name on a tooltip. Then a press on a header
    /// that moves past the drag threshold before it is released does not sort, and a click
    /// still does.
    /// </summary>
    internal static void CrdHeadersAt960(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;

        // The QA run met the cut on a Widgets grid at 960px; the demo's Certificates fit, so
        // one column is made too narrow for its header the way a drag would leave it.
        var narrowed = grid.Columns.First(c => c.Header is string text && text.StartsWith("Namespace", StringComparison.Ordinal));
        narrowed.Width = new DataGridLength(52);
        Settle();
        var cut = 0;
        foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Content is string { Length: > 0 }))
        {
            var block = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == (string)header.Content!)
                ?? throw new InvalidOperationException($"The {header.Content} header draws no text block of its own.");
            if (!CutTextTip.IsCut(block) && block.TextLayout.TextLines.Sum(l => l.Width) > block.Bounds.Width + 0.5)
                throw new InvalidOperationException($"The {header.Content} header is clipped at {block.Bounds.Width:0}px without an ellipsis (classes [{string.Join(",", block.Classes)}], header {header.Bounds.Width:0}px, text {block.TextLayout.Width:0}px, desired {block.DesiredSize.Width:0}).");
            if (CutTextTip.IsCut(block))
            {
                cut++;
                if (ToolTip.GetTip(block) as string != (string)header.Content!)
                    throw new InvalidOperationException($"The cut {header.Content} header has no tooltip with its whole name.");
            }
        }

        if (cut == 0)
            throw new InvalidOperationException("The Namespace header narrowed to 52px is not cut, so the ellipsis was not checked.");

        // A drag a pixel off the grip: the press lands on the header and moves away.
        var name = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .First(h => h.Content is string text && text.StartsWith("Name", StringComparison.Ordinal));
        var start = name.TranslatePoint(new Point(name.Bounds.Width / 2, name.Bounds.Height / 2), window)!.Value;
        var before = (vm.SortColumnId, vm.SortDescending);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(6, 0));
        window.MouseMove(start + new Point(30, 0));
        window.MouseUp(start + new Point(30, 0), MouseButton.Left);
        Settle();
        if ((vm.SortColumnId, vm.SortDescending) != before)
            throw new InvalidOperationException("A press on the Name header that was dragged 30px sorted the list; a drag is not a click (#320).");

        Thread.Sleep(700);
        window.MouseDown(start, MouseButton.Left);
        window.MouseUp(start, MouseButton.Left);
        Settle();
        if ((vm.SortColumnId, vm.SortDescending) == before)
            throw new InvalidOperationException("A click on the Name header no longer sorts.");

        Console.WriteLine($"CRD headers at 960px: {cut} cut with an ellipsis and a tooltip, none clipped bare; a dragged press does not sort.");
    }

    /// <summary>
    /// <c>cluster-tab-pod-detail-usage-docked</c>: at the default dock height the first
    /// container's figures are on screen without a scroll.
    /// </summary>
    internal static void UsageFirstContainerVisible(Window window)
    {
        var view = window.GetVisualDescendants().OfType<PodDetailView>().First();
        var detail = (PodDetailTabViewModel)view.DataContext!;
        var first = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.DataContext is ContainerViewModel && t.Text == ((ContainerViewModel)t.DataContext).CpuResourceText)
            ?? throw new InvalidOperationException("The Usage tab draws no container's requests and limits.");
        var viewer = first.FindAncestorOfType<ScrollViewer>()!;
        var bottom = first.TranslatePoint(new Point(0, first.Bounds.Height), viewer)!.Value.Y;
        if (bottom > viewer.Viewport.Height + 0.5)
            throw new InvalidOperationException(
                $"At the default dock height the first container's requests and limits end at y={bottom:0}, below the {viewer.Viewport.Height:0}px the Usage tab shows (#320).");
        Console.WriteLine($"Usage tab docked: the first container's figures end at y={bottom:0} of {viewer.Viewport.Height:0}px ({detail.Containers.Count} containers).");
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
