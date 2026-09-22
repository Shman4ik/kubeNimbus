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

    private static void Click(Window window, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{target.Name} is not in the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
