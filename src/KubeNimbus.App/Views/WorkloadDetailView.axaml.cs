using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class WorkloadDetailView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public WorkloadDetailView()
    {
        InitializeComponent();

        // Tunnel, not the grid's bubble KeyDown: DataGrid's class handler consumes Enter
        // before a bubble handler on the same element sees it. The logs keys and the
        // Shift+click are the resource list's own (RowLogsGesture).
        PodsGrid.AddHandler(KeyDownEvent, OnPodKeyDown, RoutingStrategies.Tunnel);
        _rowLogs = RowLogsGesture.Track(PodsGrid);

        // Not a two-way SelectedItem binding: that wrote the grid's teardown null back into
        // the view model whenever the inspector switched tabs, and the pane came back with
        // no pod selected (ENG-43).
        GridSelectionSync<WorkloadDetailTabViewModel, ResourceRowViewModel>.Track(
            this, PodsGrid, vm => vm.Pods, vm => vm.SelectedPod, (vm, pod) => vm.SelectedPod = pod,
            nameof(WorkloadDetailTabViewModel.SelectedPod));
        GridSortHeaders.Track(this, PodsGrid, dataContext => (dataContext as WorkloadDetailTabViewModel)?.PodSort);
    }

    private void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on a row opens its pod. One on a header is two sort clicks
        // (ascending, then descending), and used to open whichever pod was selected.
        if (DataContext is WorkloadDetailTabViewModel vm && e.Source is Control { DataContext: ResourceRowViewModel })
            vm.OpenPodCommand.Execute(null);
    }

    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkloadDetailTabViewModel vm) return;
        if (RowLogsGesture.MatchLogsKey(e) is { } maximized)
        {
            var command = maximized ? vm.PodLogsMaximizedCommand : vm.PodLogsCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Enter) { vm.OpenPodCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.S) { vm.ShellCommand.Execute(null); e.Handled = true; }
    }

    private void OnPodLogsClick(object? sender, RoutedEventArgs e)
    {
        var shift = _rowLogs.TakeShift();
        e.Handled = true;
        if (sender is not Button { DataContext: ResourceRowViewModel pod } || DataContext is not WorkloadDetailTabViewModel vm) return;
        _ = vm.OpenPodLogsAsync(pod, shift);
        RowLogsGesture.FocusOwningList(sender);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.E && DataContext is WorkloadDetailTabViewModel vm)
        { vm.OpenYamlCommand.Execute(null); e.Handled = true; }
    }
}
