using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Node detail pane — declarative apart from the Pods tab's logs gestures, which are the
/// resource list's own (<see cref="RowLogsGesture"/>). The mutating actions (cordon /
/// uncordon / drain) are deliberately not here: they land on the cluster tab's shared
/// confirm strip, as every other mutating action does (UI rule 17).
/// </summary>
public partial class NodeDetailView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public NodeDetailView()
    {
        InitializeComponent();
        PodsGrid.AddHandler(KeyDownEvent, OnPodKeyDown, RoutingStrategies.Tunnel);
        _rowLogs = RowLogsGesture.Track(PodsGrid);
    }

    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NodeDetailTabViewModel vm || RowLogsGesture.MatchLogsKey(e) is not { } maximized)
        {
            return;
        }

        var command = maximized ? vm.PodLogsMaximizedCommand : vm.PodLogsCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
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
