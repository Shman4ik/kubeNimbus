using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// L3 — logs from everywhere a pod is named, driven for real in each list: the row's logs
/// icon is hidden at rest and drawn on the selected and hovered row, a click at its far edge
/// (where the glyph is not — UI rule 8) opens that pod's logs in a tab of their own, a
/// Shift+L on the pane's own grid opens them maximized, and a plain L opens them in the split.
/// Each throws on a regression, which fails the harness run; the screenshot is the end state.
/// </summary>
internal static class PaneLogsChecks
{
    internal static void WorkloadDetail(Window window)
    {
        var tab = TabOf(window);
        var detail = (WorkloadDetailTabViewModel)tab.SelectedInspectorTab!;
        if (detail.Pods.Count < 2) throw new InvalidOperationException("Workload detail needs two pods for this check.");
        detail.SelectedPod = detail.Pods[0];
        Settle();

        var grid = window.GetVisualDescendants().OfType<WorkloadDetailView>().First().FindControl<DataGrid>("PodsGrid")!;
        CheckRest(grid, detail.Pods[0], detail.Pods[1], "workload detail");
        RightClickSelects(window, grid, detail.Pods[0], detail.Pods[1], () => detail.SelectedPod, "workload detail");

        var target = detail.Pods[^1];
        ClickIconAtEdge(window, grid, target, RawInputModifiers.None);
        if (tab.SelectedInspectorTab is not PodDetailTabViewModel { SelectedDetailTabIndex: 0 } logs || logs.PodName != target.Name)
            throw new InvalidOperationException("Workload detail: a click on a pod's logs icon did not open that pod's logs.");
        if (tab.IsInspectorMaximized || tab.InspectorTabs.Count != 2)
            throw new InvalidOperationException("Workload detail: the logs icon replaced the pane or opened maximized with the preference off.");
        // Not asserted here: that the icon selected its row. It does (PaneLogsTests pins it),
        // but switching the inspector to the new logs tab tears the pane's view down, and the
        // grid's two-way SelectedItem writes null back as it goes — so by now it reads null.

        // Back to the pane: Shift+L on another pod, then plain L.
        tab.SelectedInspectorTab = detail;
        Settle();
        grid = window.GetVisualDescendants().OfType<WorkloadDetailView>().First().FindControl<DataGrid>("PodsGrid")!;
        detail.SelectedPod = detail.Pods[0];
        grid.Focus();
        window.KeyPress(Key.L, RawInputModifiers.Shift, PhysicalKey.L, "L");
        Dispatcher.UIThread.RunJobs();
        if (!tab.IsInspectorMaximized || tab.SelectedInspectorTab is not PodDetailTabViewModel shifted || shifted.PodName != detail.Pods[0].Name)
            throw new InvalidOperationException("Workload detail: Shift+L did not open the selected pod's logs maximized.");

        tab.IsInspectorMaximized = false;
        tab.SelectedInspectorTab = detail;
        Settle();
        grid = window.GetVisualDescendants().OfType<WorkloadDetailView>().First().FindControl<DataGrid>("PodsGrid")!;
        detail.SelectedPod = detail.Pods[1];
        grid.Focus();
        window.KeyPress(Key.L, RawInputModifiers.None, PhysicalKey.L, "l");
        Dispatcher.UIThread.RunJobs();
        if (tab.IsInspectorMaximized || tab.SelectedInspectorTab is not PodDetailTabViewModel plain || plain.PodName != detail.Pods[1].Name)
            throw new InvalidOperationException("Workload detail: L did not open the selected pod's logs in the split.");

        Park(window);
        Console.WriteLine($"Workload detail pane logs passed (icon rest/selected/hover, edge click, Shift+L, L; {tab.InspectorTabs.Count} tabs).");
    }

    internal static void NodeDetail(Window window)
    {
        var tab = TabOf(window);
        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
        if (detail.Pods.Count < 2) throw new InvalidOperationException("Node detail needs two pods for this check.");
        detail.SelectedPod = detail.Pods[0];
        Settle();

        var grid = window.GetVisualDescendants().OfType<NodeDetailView>().First().FindControl<DataGrid>("PodsGrid")!;
        CheckRest(grid, detail.Pods[0], detail.Pods[1], "node detail");
        RightClickSelects(window, grid, detail.Pods[0], detail.Pods[1], () => detail.SelectedPod, "node detail");

        // The second row rather than the last: the node's list is longer than the dock, and
        // a row the DataGrid has not realized has no icon to click.
        var target = detail.Pods[1];
        ClickIconAtEdge(window, grid, target, RawInputModifiers.Shift);
        if (tab.SelectedInspectorTab is not PodDetailTabViewModel { SelectedDetailTabIndex: 0 } logs || logs.PodName != target.Name)
            throw new InvalidOperationException("Node detail: a Shift+click on a pod's logs icon did not open that pod's logs.");
        if (!tab.IsInspectorMaximized)
            throw new InvalidOperationException("Node detail: a Shift+click did not open the logs maximized.");

        tab.IsInspectorMaximized = false;
        tab.SelectedInspectorTab = detail;
        Settle();
        grid = window.GetVisualDescendants().OfType<NodeDetailView>().First().FindControl<DataGrid>("PodsGrid")!;
        detail.SelectedPod = detail.Pods[0];
        grid.Focus();
        window.KeyPress(Key.L, RawInputModifiers.None, PhysicalKey.L, "l");
        Dispatcher.UIThread.RunJobs();
        if (tab.IsInspectorMaximized || tab.SelectedInspectorTab is not PodDetailTabViewModel plain || plain.PodName != detail.Pods[0].Name)
            throw new InvalidOperationException("Node detail: L did not open the selected pod's logs in the split.");

        Park(window);
        Console.WriteLine($"Node detail pane logs passed (icon rest/selected/hover, Shift+click at edge, L; {tab.InspectorTabs.Count} tabs).");
    }

    internal static void Events(Window window)
    {
        var tab = TabOf(window);
        var grid = window.GetVisualDescendants().OfType<ClusterTabView>().First().FindControl<DataGrid>("ResourceGrid")!;
        var aboutPods = tab.VisibleRows.Where(r => r.Resource.InvolvedObject() is { Kind: "Pod" }).ToList();
        var aboutOther = tab.VisibleRows.FirstOrDefault(r => r.Resource.InvolvedObject() is not { Kind: "Pod" });
        if (aboutPods.Count < 2 || aboutOther is null)
            throw new InvalidOperationException("The demo Events list needs two events about pods and one about something else.");

        tab.SelectedRow = aboutPods[0];
        Settle();
        if (Icon(grid, aboutPods[0]) is not { IsEffectivelyVisible: true })
            throw new InvalidOperationException("Events: the selected pod event's logs icon is not showing.");

        // An event about a ReplicaSet or a Node has no icon even when selected.
        tab.SelectedRow = aboutOther;
        Settle();
        if (Icon(grid, aboutOther) is { IsEffectivelyVisible: true })
            throw new InvalidOperationException("Events: an event about something other than a pod shows a logs icon.");

        tab.SelectedRow = aboutPods[0];
        grid.Focus();
        window.KeyPress(Key.L, RawInputModifiers.None, PhysicalKey.L, "l");
        Dispatcher.UIThread.RunJobs();
        var podName = aboutPods[0].Resource.InvolvedObject()!.Name;
        if (tab.SelectedInspectorTab is not PodDetailTabViewModel { SelectedDetailTabIndex: 0 } logs || logs.PodName != podName)
            throw new InvalidOperationException("Events: L on an event about a pod did not open that pod's logs.");

        tab.SelectedRow = aboutPods[1];
        Settle();
        var button = Icon(grid, aboutPods[1]) ?? throw new InvalidOperationException("Events: no icon on the second pod event.");
        var edge = button.TranslatePoint(new Point(button.Bounds.Width - 1.5, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(edge);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(edge, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(edge, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        if (!tab.IsInspectorMaximized || tab.SelectedInspectorTab is not PodDetailTabViewModel)
            throw new InvalidOperationException("Events: a Shift+click on a pod event's logs icon did not open the pod's logs maximized.");

        tab.IsInspectorMaximized = false;
        Park(window);
        Console.WriteLine($"Events list pod logs passed (icon only on pod events, L, Shift+click at edge; {tab.InspectorTabs.Count} tabs).");
    }

    internal static void Argo(Window window)
    {
        var tab = TabOf(window);
        var detail = (ArgoApplicationTabViewModel)tab.SelectedInspectorTab!;
        var view = window.GetVisualDescendants().OfType<ArgoApplicationView>().First();
        var deployment = detail.Resources.First(r => r.Kind == "Deployment");
        var service = detail.Resources.First(r => r.Kind == "Service");

        var row = RowOf(view, deployment);
        var button = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("rowAction"));
        if (button.IsEffectivelyVisible)
            throw new InvalidOperationException("Argo: a resource row's logs icon shows at rest.");

        // The row is an ItemsControl item, so :pointerover on the row Grid is the reveal —
        // which needs the Transparent background to hit-test where no text is (UI rule 8).
        window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width * 0.5, row.Bounds.Height / 2), window)!.Value);
        Settle();
        if (!button.IsEffectivelyVisible || button.Bounds.Width <= 0)
            throw new InvalidOperationException("Argo: hovering a workload row did not show its logs icon.");

        var serviceRow = RowOf(view, service);
        window.MouseMove(serviceRow.TranslatePoint(new Point(serviceRow.Bounds.Width * 0.5, serviceRow.Bounds.Height / 2), window)!.Value);
        Settle();
        if (serviceRow.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("rowAction") && b.IsEffectivelyVisible))
            throw new InvalidOperationException("Argo: a Service row shows a logs icon.");

        window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width * 0.5, row.Bounds.Height / 2), window)!.Value);
        Settle();
        var edge = button.TranslatePoint(new Point(button.Bounds.Width - 1.5, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(edge);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(edge, MouseButton.Left);
        window.MouseUp(edge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        if (tab.SelectedInspectorTab is not WorkloadLogsTabViewModel)
            throw new InvalidOperationException(
                $"Argo: a click on a Deployment's logs icon did not open its pods' logs ({detail.LogsNotice ?? "no notice"}).");

        Park(window);
        Console.WriteLine("Argo resource logs passed (hidden at rest, hover reveal on workloads only, edge click).");
    }

    /// <summary>Puts the pointer on an Argo resource row, for the shot of its hover-revealed logs icon.</summary>
    internal static void HoverArgoRow(Window window, string kind)
    {
        var view = window.GetVisualDescendants().OfType<ArgoApplicationView>().First();
        var detail = (ArgoApplicationTabViewModel)view.DataContext!;
        var row = RowOf(view, detail.Resources.First(r => r.Kind == kind));
        window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width * 0.5, row.Bounds.Height / 2), window)!.Value);
        Settle();
    }

    private static ClusterTabViewModel TabOf(Window window) =>
        (ClusterTabViewModel)window.GetVisualDescendants().OfType<ClusterTabView>().First().DataContext!;

    private static void CheckRest(DataGrid grid, object selected, object idle, string where)
    {
        if (Icon(grid, idle) is { IsEffectivelyVisible: true })
            throw new InvalidOperationException($"{where}: an idle row's logs icon is showing.");
        if (Icon(grid, selected) is not { IsEffectivelyVisible: true })
            throw new InvalidOperationException($"{where}: the selected row's logs icon is not showing.");
    }

    private static void ClickIconAtEdge(Window window, DataGrid grid, object item, RawInputModifiers modifiers)
    {
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));
        window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width * 0.7, row.Bounds.Height / 2), window)!.Value);
        Settle();
        var button = Icon(grid, item);
        if (button is not { IsEffectivelyVisible: true } || button.Bounds.Width <= 0)
            throw new InvalidOperationException("Hovering a pod row did not show its logs icon.");
        var edge = button.TranslatePoint(new Point(button.Bounds.Width - 1.5, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(edge);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(edge, MouseButton.Left, modifiers);
        window.MouseUp(edge, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The row's logs icon, whichever cell carries it (Name, or Object on an event).</summary>
    private static Button? Icon(DataGrid grid, object item) =>
        grid.GetVisualDescendants().OfType<DataGridRow>()
            .First(r => ReferenceEquals(r.DataContext, item))
            .GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("rowAction"))
            .OrderByDescending(b => b.IsEffectivelyVisible)
            .FirstOrDefault();

    private static Grid RowOf(ArgoApplicationView view, ArgoResourceRowViewModel item) =>
        view.GetVisualDescendants().OfType<Grid>()
            .First(g => g.Classes.Contains("argoResourceRow") && ReferenceEquals(g.DataContext, item));

    /// <summary>
    /// A right click on a pod row that is not selected must select it before the context
    /// menu opens — otherwise the menu's Logs acts on the previously selected pod. DataGrid
    /// 12 already selects on a right click over a <em>cell</em>, so this clicks the row's far
    /// edge, past the last cell, which is where node detail's list failed without the shared
    /// handler in RowLogsGesture (UI rule 8). The menu is dismissed afterwards.
    /// </summary>
    private static void RightClickSelects(
        Window window, DataGrid grid, object selected, object clicked, Func<object?> selectedPod, string where)
    {
        grid.SelectedItem = selected;
        Settle();
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, clicked));
        var point = row.TranslatePoint(new Point(row.Bounds.Width - 1, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        if (!ReferenceEquals(grid.SelectedItem, clicked) || !ReferenceEquals(selectedPod(), clicked))
            throw new InvalidOperationException($"{char.ToUpperInvariant(where[0])}{where[1..]}: a right click did not select the pod under it, so the menu's Logs would open the previously selected pod.");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        grid.SelectedItem = selected;
        Settle();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Park(Window window)
    {
        window.MouseMove(new Point(2, 2));
        Settle();
    }
}
