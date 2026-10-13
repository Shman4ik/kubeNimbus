using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #362: a CRD column widened by a drag on its header's edge keeps that width when the reader
/// switches to another kind and back (resource-grid-resize-sort.md, rule 11). The QA VM run
/// widened Widgets' <c>SKU</c> from 111 to 151 px with a real mouse and saw 111 px again after
/// the round trip. The drag here goes through the header's own pointer handling, the same path
/// a mouse takes, so a width that is never written and one that is written and then reset both
/// fail it.
/// </summary>
internal static class ColumnWidthChecks
{
    internal static void CrdWidthKeptAcrossKinds(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First(v => v.IsEffectivelyVisible);
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        var crdKind = vm.SelectedKind ?? throw new InvalidOperationException("No kind is selected.");
        if (vm.VisiblePrinterColumns.Count == 0)
            throw new InvalidOperationException($"{crdKind.Descriptor.Kind} shows no printer columns to drag.");

        var label = vm.VisiblePrinterColumns[0].Name;
        Settle();
        var before = Header(grid, label).Bounds.Width;

        // A press on the header's right edge, where the resize grip is, dragged 40px right.
        var header = Header(grid, label);
        var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(edge);
        window.MouseDown(edge, MouseButton.Left);
        window.MouseMove(edge + new Point(10, 0));
        window.MouseMove(edge + new Point(40, 0));
        window.MouseUp(edge + new Point(40, 0), MouseButton.Left);
        Settle();

        var dragged = Header(grid, label).Bounds.Width;
        if (dragged < before + 20)
            throw new InvalidOperationException($"The drag on the {label} header's edge did not widen it ({before:0} to {dragged:0} px); the check moved nothing.");

        var key = vm.GridLayoutKey!;
        if (!GridLayoutStore.Load(key).Widths.ContainsKey(ResourceColumn.Printer(label)))
            throw new InvalidOperationException($"The drag widened {label} to {dragged:0} px but nothing was stored for it under {key}.");

        var pods = vm.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" });
        vm.SelectKindCommand.Execute(pods);
        Settle();
        vm.SelectKindCommand.Execute(crdKind);
        Settle();

        var after = Header(grid, label).Bounds.Width;
        if (Math.Abs(after - dragged) > 1)
            throw new InvalidOperationException($"{label} was dragged to {dragged:0} px, but after Pods and back it is {after:0} px (was {before:0}); rule 11 (#362).");

        Console.WriteLine($"CRD column width kept across kinds: {label} {before:0} to {dragged:0} px, {after:0} px after Pods and back.");
    }

    private static DataGridColumnHeader Header(DataGrid grid, string label) =>
        grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .FirstOrDefault(h => h.IsEffectivelyVisible && h.Content is string text && text.StartsWith(label, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No visible {label} header in the grid.");

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
