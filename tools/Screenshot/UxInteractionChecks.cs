using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

internal static class UxInteractionChecks
{
    internal static void NamespacePicker(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        for (var i = 0; i < 300; i++) vm.NamespaceOptions.Add($"team-{i:D3}");
        var button = view.FindControl<Button>("NamespaceButton")!;
        var search = view.FindControl<TextBox>("NamespaceSearch")!;
        button.Focus();
        window.KeyPress(Key.N, (RawInputModifiers)(Hotkeys.Primary | KeyModifiers.Shift), PhysicalKey.N, "n");
        Dispatcher.UIThread.RunJobs();
        if (!search.IsFocused) throw new InvalidOperationException("Namespace shortcut did not focus search.");
        window.KeyTextInput("team-299");
        Dispatcher.UIThread.RunJobs();
        if (vm.FilteredNamespaces.Count != 1 || vm.SelectedNamespace == "team-299")
            throw new InvalidOperationException("Namespace filtering selected a namespace or failed to narrow the list.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        if (vm.SelectedNamespace != "team-299") throw new InvalidOperationException("Enter did not select the namespace.");
        button.Focus();
        window.KeyPress(Key.N, (RawInputModifiers)(Hotkeys.Primary | KeyModifiers.Shift), PhysicalKey.N, "n");
        Dispatcher.UIThread.RunJobs();
        if (vm.FilteredNamespaces[1].Name != "team-299" || !vm.FilteredNamespaces[1].IsRecent)
            throw new InvalidOperationException("Recent namespace did not move to the top.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (!button.IsFocused) throw new InvalidOperationException("Escape did not restore picker focus.");
        Console.WriteLine("Namespace keyboard interaction passed (300 namespaces).");
    }

    /// <summary>
    /// Unhealthy only, through all three entry points against a real rendered view:
    /// Ctrl+Z on the focused grid, a pointer click on the chip, and the palette row.
    /// The chip is the one worth driving for real — a ToggleButton wired with both a
    /// two-way IsChecked and a command is a no-op that renders perfectly (UI rule 8b),
    /// and only a click can tell the two apart.
    /// </summary>
    internal static void UnhealthyToggle(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var shell = (MainWindowViewModel)window.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        var chip = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("UnhealthyToggle")!;
        var total = vm.Rows.Count;

        if (vm.IsUnhealthyOnly || !vm.CanFilterUnhealthy || !chip.IsEnabled)
            throw new InvalidOperationException("Unhealthy-only should start off and available on Pods.");

        grid.Focus();
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Dispatcher.UIThread.RunJobs();
        if (!vm.IsUnhealthyOnly || chip.IsChecked != true)
            throw new InvalidOperationException("Ctrl+Z on the grid did not turn unhealthy-only on.");
        if (vm.VisibleRows.Count == 0 || vm.VisibleRows.Count >= total || vm.VisibleRows.Any(r => !r.IsUnhealthy))
            throw new InvalidOperationException("Unhealthy-only did not narrow the demo list to its unhealthy rows.");
        if (vm.Rows.Count != total)
            throw new InvalidOperationException("Unhealthy-only changed Rows (UI rule 13).");

        Click(window, chip);
        if (vm.IsUnhealthyOnly || chip.IsChecked == true || vm.VisibleRows.Count != total)
            throw new InvalidOperationException("Clicking the checked chip did not turn unhealthy-only off.");

        Click(window, chip);
        if (!vm.IsUnhealthyOnly)
            throw new InvalidOperationException("Clicking the chip did not turn unhealthy-only on.");

        shell.Palette.Open();
        shell.Palette.Query = "every row";
        var off = shell.Palette.FilteredItems.FirstOrDefault(i => i.Title.StartsWith("Show every row", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The palette did not offer the way out of unhealthy-only.");
        shell.Palette.SelectedItem = off;
        shell.Palette.ExecuteSelected();
        Dispatcher.UIThread.RunJobs();
        if (vm.IsUnhealthyOnly) throw new InvalidOperationException("The palette row did not turn unhealthy-only off.");

        shell.Palette.Open();
        shell.Palette.Query = "unhealthy";
        if (!shell.Palette.FilteredItems.Any(i => i.Title == "Show only unhealthy rows"))
            throw new InvalidOperationException("The palette did not offer unhealthy-only.");
        shell.Palette.Close();

        Console.WriteLine($"Unhealthy-only interaction passed ({vm.Rows.Count} rows; key, chip click, palette).");
    }

    /// <summary>
    /// L1, driven through the real window: Ctrl/Cmd+Shift+L opens the palette on the logs
    /// prefix with focus in its box, typing narrows it to the demo cluster's pods and
    /// workloads, Enter opens the same pane the list's L key would, and a click lands on
    /// the row it hit (the tap handler is on the list, not the row's text — UI rule 8).
    /// </summary>
    internal static void LogsPalette(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var shell = (MainWindowViewModel)window.DataContext!;
        var box = window.FindControl<TextBox>("PaletteQueryBox")!;

        view.FindControl<DataGrid>("ResourceGrid")!.Focus();
        window.KeyPress(Key.L, (RawInputModifiers)(Hotkeys.Primary | KeyModifiers.Shift), PhysicalKey.L, "l");
        Dispatcher.UIThread.RunJobs();
        if (!shell.Palette.IsOpen || shell.Palette.Query != CommandPaletteViewModel.LogsPrefix || !box.IsFocused)
            throw new InvalidOperationException("The logs gesture did not open a focused palette on the logs prefix.");
        if (box.CaretIndex != CommandPaletteViewModel.LogsPrefix.Length)
            throw new InvalidOperationException("The caret was not at the end of the logs prefix.");
        if (shell.Palette.FilteredItems.Count == 0 || shell.Palette.FilteredItems.Any(i => i.Scope != PaletteScope.Logs))
            throw new InvalidOperationException("The logs prefix did not narrow the palette to log rows.");

        window.KeyTextInput("checkout");
        Dispatcher.UIThread.RunJobs();
        var titles = shell.Palette.FilteredItems.Select(i => i.Title).ToList();
        if (titles.Count != 2 || titles[0] != "Logs: Deployment/checkout-worker" || !titles[1].StartsWith("Logs: checkout-worker-", StringComparison.Ordinal))
            throw new InvalidOperationException($"Typing did not narrow to the workload and its pod: {string.Join(", ", titles)}");

        // Down to the pod, Enter: pod detail on its Logs tab, as L on the row opens it.
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        if (shell.Palette.IsOpen || vm.SelectedInspectorTab is not PodDetailTabViewModel { SelectedDetailTabIndex: 0 } detail
            || !detail.PodName.StartsWith("checkout-worker-", StringComparison.Ordinal))
            throw new InvalidOperationException("Enter on a pod row did not open that pod's logs.");

        // The workload row, by a click on its subtitle's far edge — a spot no text covers.
        window.KeyPress(Key.L, (RawInputModifiers)(Hotkeys.Primary | KeyModifiers.Shift), PhysicalKey.L, "l");
        window.KeyTextInput("checkout");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("PaletteList")!;
        var first = list.ContainerFromIndex(0) as Control
            ?? throw new InvalidOperationException("The palette's first row was not realized.");
        var edge = first.TranslatePoint(new Point(first.Bounds.Width - 6, first.Bounds.Height / 2), window)!.Value;
        window.MouseDown(edge, MouseButton.Left);
        window.MouseUp(edge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        if (shell.Palette.IsOpen || vm.SelectedInspectorTab is not WorkloadLogsTabViewModel)
            throw new InvalidOperationException("A click at the row's edge did not open the workload's logs.");

        var tabs = vm.InspectorTabs.Count;
        Console.WriteLine($"Logs palette interaction passed (gesture, typing, Enter on a pod, click on a workload; {tabs} tabs).");
    }

    private static void Click(Window window, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{target.Name} is not in the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
