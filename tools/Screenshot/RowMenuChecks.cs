using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The row menus of three lists that had row actions and no menu (2026-10-21): the
/// port-forwards list, the Helm releases list and an Argo CD Application's managed resources.
/// Driven through real right-clicks, because each can render perfectly and be wrong: a menu
/// whose binding to the list's own commands resolves to nothing, a right-click that also runs
/// the row's default action, or a menu that opens over empty space and acts on whatever was
/// selected before. Each check closes its menu, so the capture is the list as it renders.
/// </summary>
internal static class RowMenuChecks
{
    private static readonly string[] PortForwardItems = ["Show pane", "Copy URL", "Open in browser", "Stop"];

    /// <summary>
    /// A forward's card menu: its four items, enabled and bound to that forward's commands,
    /// and a right-click that neither opens the pane nor stops anything.
    /// </summary>
    internal static void PortForwards(Window window)
    {
        var view = window.GetVisualDescendants().OfType<PortForwardsView>().First(v => v.IsEffectivelyVisible);
        var list = (PortForwardsTabViewModel)view.DataContext!;
        var tab = window.GetVisualDescendants().OfType<ClusterTabView>().First(v => v.IsEffectivelyVisible);
        var tabVm = (ClusterTabViewModel)tab.DataContext!;
        var forward = list.Registry.Forwards[1];
        var count = list.Registry.Forwards.Count;

        var card = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, forward));
        var menu = card.ContextMenu ?? throw new InvalidOperationException("A forward's card has no menu.");

        // In the card's left padding, which is the card's own background: a right-click on its
        // address is the SelectableTextBlock's, whose menu copies the selection.
        RightClick(window, card, new Point(4, card.Bounds.Height / 2));
        if (!menu.IsOpen)
            throw new InvalidOperationException("A right-click on a forward's card should open its menu.");
        if (list.Registry.Forwards.Count != count || !ReferenceEquals(tabVm.SelectedInspectorTab, list))
            throw new InvalidOperationException("A right-click on a forward's card should not stop it or open its pane.");

        var items = menu.Items.OfType<MenuItem>().ToList();
        var headers = items.Select(i => i.Header as string).ToArray();
        if (!headers.SequenceEqual(PortForwardItems))
            throw new InvalidOperationException($"The forward's menu reads [{string.Join(", ", headers)}], not [{string.Join(", ", PortForwardItems)}].");
        if (items.FirstOrDefault(i => !i.IsEffectivelyEnabled) is { } disabled)
            throw new InvalidOperationException($"{disabled.Header} is disabled on a running forward.");

        MenuItem Item(string header) => items.First(i => (i.Header as string) == header);
        if (!ReferenceEquals(Item("Show pane").Command, forward.ShowPaneCommand)
            || !ReferenceEquals(Item("Stop").Command, forward.StopFromListCommand)
            || !ReferenceEquals(Item("Copy URL").Command, forward.CopyUrlCommand)
            || !ReferenceEquals(Item("Open in browser").Command, forward.OpenInBrowserCommand))
            throw new InvalidOperationException("The forward's menu items are not bound to the commands of the forward it opened over.");

        menu.Close();
        Dispatcher.UIThread.RunJobs();

        // Below the last card: no card's menu.
        var last = view.GetVisualDescendants().OfType<Border>()
            .Last(b => b.Classes.Contains("card") && b.DataContext is PortForwardTabViewModel);
        var below = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height + 30), window)!.Value;
        window.MouseDown(below, MouseButton.Right);
        window.MouseUp(below, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        if (view.GetVisualDescendants().OfType<Border>().Any(b => b.ContextMenu is { IsOpen: true }))
            throw new InvalidOperationException("A right-click below the last forward opened a forward's menu.");

        Console.WriteLine("Port-forwards row menu passed (four items bound to that forward, right-click runs nothing, none below the list).");
    }

    /// <summary>
    /// The Helm list's menu: a right-click selects the release it lands on without opening it,
    /// the menu offers Open and Refresh, and a right-click on a header or below the last
    /// release opens nothing.
    /// </summary>
    internal static void HelmReleases(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First(v => v.IsEffectivelyVisible);
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("HelmGrid")!;
        var flyout = (MenuFlyout)grid.ContextFlyout!;
        var tabs = vm.InspectorTabs.Count;

        var target = vm.Helm.Releases[2];
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, target));
        RightClick(window, row, new Point(row.Bounds.Width / 2, row.Bounds.Height / 2));
        if (!ReferenceEquals(vm.Helm.SelectedRelease, target) || !flyout.IsOpen || vm.InspectorTabs.Count != tabs)
            throw new InvalidOperationException(
                $"A right-click on {target.Name} should select it and open the menu, not the release (selected {vm.Helm.SelectedRelease?.Name}, menu open {flyout.IsOpen}).");

        var items = flyout.Items.OfType<MenuItem>().ToList();
        var headers = items.Select(i => i.Header as string).ToArray();
        if (!headers.SequenceEqual(["Open", "Refresh"]) || items.Any(i => !i.IsEffectivelyEnabled))
            throw new InvalidOperationException($"The Helm menu reads [{string.Join(", ", headers)}], or an item is disabled.");
        if (!ReferenceEquals(items[0].Command, vm.Helm.OpenSelectedCommand) || !ReferenceEquals(items[1].Command, vm.Helm.RefreshCommand))
            throw new InvalidOperationException("The Helm menu's items are not the double-click's Open and the list's Refresh.");

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.IsEffectivelyVisible && h.Content is string { Length: > 0 });
        RightClick(window, header, new Point(header.Bounds.Width / 2, header.Bounds.Height / 2));
        if (flyout.IsOpen)
            throw new InvalidOperationException("A right-click on a Helm column header opened the row menu.");

        var lastRow = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible)
            .MaxBy(r => r.TranslatePoint(default, grid)!.Value.Y)!;
        var below = lastRow.TranslatePoint(new Point(lastRow.Bounds.Width / 2, lastRow.Bounds.Height + 40), grid)!.Value;
        if (below.Y >= grid.Bounds.Height)
            throw new InvalidOperationException("The Helm fixture fills the grid, so there is no space below the last release to right-click.");
        RightClick(window, grid, below);
        if (flyout.IsOpen || !ReferenceEquals(vm.Helm.SelectedRelease, target))
            throw new InvalidOperationException("A right-click below the last Helm release opened the row menu or moved the selection.");

        Console.WriteLine("Helm row menu passed (right-click selects without opening, Open and Refresh, none on a header or below the list).");
    }

    /// <summary>
    /// An Argo Application's managed resources: a row's menu offers Open, and Logs only where
    /// the row has its logs icon, both bound to that row; the right-click opens nothing.
    /// </summary>
    internal static void ArgoResources(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ArgoApplicationView>().First(v => v.IsEffectivelyVisible);
        var detail = (ArgoApplicationTabViewModel)view.DataContext!;
        var tab = window.GetVisualDescendants().OfType<ClusterTabView>().First(v => v.IsEffectivelyVisible);
        var tabVm = (ClusterTabViewModel)tab.DataContext!;
        var rows = view.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.Classes.Contains("argoResourceRow") && g.IsEffectivelyVisible).ToList();

        void Check(Func<ArgoResourceRowViewModel, bool> pick, bool logs)
        {
            var row = rows.FirstOrDefault(g => g.DataContext is ArgoResourceRowViewModel r && pick(r))
                ?? throw new InvalidOperationException($"The Argo fixture has no visible resource row {(logs ? "with" : "without")} logs.");
            var resource = (ArgoResourceRowViewModel)row.DataContext!;
            var menu = row.ContextMenu ?? throw new InvalidOperationException("An Argo resource row has no menu.");

            RightClick(window, row, new Point(row.Bounds.Width / 3, row.Bounds.Height / 2));
            if (!menu.IsOpen || !ReferenceEquals(tabVm.SelectedInspectorTab, detail))
                throw new InvalidOperationException($"A right-click on {resource.Kind}/{resource.Name} should open its menu and nothing else.");

            var open = menu.Items.OfType<MenuItem>().First(i => (i.Header as string) == "Open");
            var logsItem = menu.Items.OfType<MenuItem>().First(i => (i.Header as string) == "Logs");
            if (!ReferenceEquals(open.Command, resource.OpenCommand) || !open.IsEffectivelyEnabled)
                throw new InvalidOperationException($"{resource.Kind}/{resource.Name}'s Open is not the row's own.");
            if (logsItem.IsVisible != logs || (logs && (!ReferenceEquals(logsItem.Command, resource.LogsCommand) || !logsItem.IsEffectivelyEnabled)))
                throw new InvalidOperationException($"{resource.Kind}/{resource.Name}'s menu {(logs ? "should" : "should not")} offer an enabled Logs.");

            menu.Close();
            Dispatcher.UIThread.RunJobs();
        }

        Check(r => r.HasLogs, logs: true);
        Check(r => !r.HasLogs, logs: false);

        Console.WriteLine("Argo resources row menu passed (Open on every row, Logs only beside the logs icon, right-click opens nothing).");
    }

    private static void RightClick(Window window, Visual target, Point at)
    {
        var point = target.TranslatePoint(at, window)
            ?? throw new InvalidOperationException($"{target} is not in the window.");
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }
}
