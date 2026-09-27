using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Node detail pane — declarative apart from the Pods tab's gestures: the logs keys and
/// icon are the resource list's own (<see cref="RowLogsGesture"/>), Enter and double-click
/// open the pod, and the selection is synced by <see cref="GridSelectionSync{TViewModel, TItem}"/>
/// so it survives the inspector switching tabs. The mutating actions (cordon / uncordon /
/// drain) are deliberately not here: they land on the cluster tab's shared confirm strip,
/// as every other mutating action does (UI rule 17).
/// </summary>
public partial class NodeDetailView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public NodeDetailView()
    {
        InitializeComponent();

        // Tunnel, not the grid's bubble KeyDown: DataGrid's class handler consumes Enter
        // before a bubble handler on the same element sees it.
        PodsGrid.AddHandler(KeyDownEvent, OnPodKeyDown, RoutingStrategies.Tunnel);
        _rowLogs = RowLogsGesture.Track(PodsGrid);
        GridSelectionSync<NodeDetailTabViewModel, NodePodViewModel>.Track(
            this, PodsGrid, vm => vm.Pods, vm => vm.SelectedPod, (vm, pod) => vm.SelectedPod = pod,
            nameof(NodeDetailTabViewModel.SelectedPod));
    }

    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NodeDetailTabViewModel vm)
        {
            return;
        }

        if (RowLogsGesture.MatchLogsKey(e) is { } maximized)
        {
            var command = maximized ? vm.PodLogsMaximizedCommand : vm.PodLogsCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter && vm.SelectedPod is { } pod)
        {
            vm.OpenPodCommand.Execute(pod);
            e.Handled = true;
        }
    }

    private void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double-click on the header or on empty grid below the rows opens nothing; one
        // on a row has already selected it.
        if (DataContext is NodeDetailTabViewModel { SelectedPod: { } pod } vm
            && e.Source is Control { DataContext: NodePodViewModel })
        {
            vm.OpenPodCommand.Execute(pod);
        }
    }

    private void OnPodLogsClick(object? sender, RoutedEventArgs e)
    {
        var shift = _rowLogs.TakeShift();
        e.Handled = true;
        if (sender is not Button { DataContext: NodePodViewModel pod } || DataContext is not NodeDetailTabViewModel vm)
        {
            return;
        }

        _ = vm.OpenPodLogsAsync(pod, shift);
        RowLogsGesture.FocusOwningList(sender);
    }
}
