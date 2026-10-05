using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The inspector grids' header sort, clicked on the rendered header. A view-model test cannot
/// show the one thing that breaks here silently: a template column without
/// <c>CanUserSort="True"</c> never raises <c>Sorting</c>, so the header renders, takes the
/// click and does nothing (resource-grid-resize-sort.md, rule 3).
/// </summary>
internal static class GridSortChecks
{
    internal static void WorkloadPods(Window window)
    {
        var view = window.GetVisualDescendants().OfType<WorkloadDetailView>().First(v => v.IsEffectivelyVisible);
        var vm = (WorkloadDetailTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("PodsGrid")!;
        if (vm.Pods.Count < 2)
            throw new InvalidOperationException("The workload's Pods grid has fewer than two pods to sort.");

        // A quick double-click on a header used to open the selected pod: the grid's
        // DoubleTapped did not ask whether it landed on a row.
        var inspectorTabs = InspectorTabs(window);
        ClickHeader(window, grid, "Restarts");
        ClickHeader(window, grid, "Restarts");
        if (InspectorTabs(window) != inspectorTabs || !view.IsEffectivelyVisible)
            throw new InvalidOperationException("A double-click on workload detail's Restarts header opened a pod.");

        // Back to the default order, then two separate clicks: ascending, descending.
        while (vm.PodSort.Column is { } sorted)
        {
            vm.PodSort.Toggle(sorted);
        }

        Pause();
        ClickHeader(window, grid, "Restarts");
        Pause();
        ClickHeader(window, grid, "Restarts");
        if (vm.PodSort is not { Column: ResourceColumn.Restarts, Descending: true }
            || vm.Pods.Zip(vm.Pods.Skip(1)).Any(p => p.First.Restarts < p.Second.Restarts)
            || !HeaderTexts(grid).Contains("Restarts ↓"))
            throw new InvalidOperationException("Two clicks on workload detail's Restarts header did not sort its pods by restarts, most first.");

        Console.WriteLine($"Workload pods sort passed ({vm.Pods.Count} pods by restarts, most first).");
    }

    internal static void NodePods(Window window)
    {
        var view = window.GetVisualDescendants().OfType<NodeDetailView>().First(v => v.IsEffectivelyVisible);
        var vm = (NodeDetailTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("PodsGrid")!;
        if (vm.Pods.Count < 2)
            throw new InvalidOperationException("The node's Pods grid has fewer than two pods to sort.");

        ClickHeader(window, grid, "CPU req");
        Pause();
        ClickHeader(window, grid, "CPU req");
        if (vm.PodSort is not { Column: NodePodComparer.CpuRequest, Descending: true }
            || vm.Pods.Zip(vm.Pods.Skip(1)).Any(p => p.First.CpuRequest < p.Second.CpuRequest)
            || !HeaderTexts(grid).Contains("CPU req ↓"))
            throw new InvalidOperationException("Two clicks on node detail's CPU req header did not sort its pods by CPU request, most first.");

        Console.WriteLine($"Node pods sort passed ({vm.Pods.Count} pods by CPU request, most first).");
    }

    /// <summary>
    /// The Helm browser or the Argo dashboard: two separate clicks on a header sort it
    /// descending. The double-click guard these grids share with workload detail is not
    /// exercised here: two headless clicks on these headers arrive as two sort clicks, never
    /// as a DoubleTapped, so a check would pass with or without it (tried).
    /// </summary>
    internal static void ClusterTabGrid(Window window, string gridName, string label, string column)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First(v => v.IsEffectivelyVisible);
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>(gridName)!;
        var sort = gridName == "HelmGrid" ? (IGridSort)vm.HelmSort : vm.ArgoSort;

        Pause();
        ClickHeader(window, grid, label);
        Pause();
        ClickHeader(window, grid, label);
        if (sort.Column != column || !sort.Descending || !HeaderTexts(grid).Contains(label + " ↓"))
            throw new InvalidOperationException($"Two clicks on the {gridName} {label} header did not sort it descending.");

        Console.WriteLine($"{gridName} sort passed ({label}, descending).");
    }

    private static int InspectorTabs(Window window) =>
        window.GetVisualDescendants().OfType<ClusterTabView>().Where(v => v.IsEffectivelyVisible)
            .Select(v => ((ClusterTabViewModel)v.DataContext!).InspectorTabs.Count).Sum();

    // Past the platform's double-click time, so the next press is a click of its own.
    private static void Pause() => Thread.Sleep(700);

    private static List<string> HeaderTexts(DataGrid grid) =>
        [.. grid.Columns.Select(c => c.Header as string ?? "")];

    private static void ClickHeader(Window window, DataGrid grid, string label)
    {
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .FirstOrDefault(h => h.Content is string text && text.StartsWith(label, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No {label} header in the grid.");
        var centre = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
