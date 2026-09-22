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
}
