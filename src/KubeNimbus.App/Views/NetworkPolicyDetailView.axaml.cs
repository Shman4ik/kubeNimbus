using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// NetworkPolicy detail pane — declarative apart from the Pods tab's gestures, which are the
/// resource list's own (<see cref="RowLogsGesture"/>): L / Shift+L, the row icon, Enter and
/// double-click to open the pod.
/// </summary>
public partial class NetworkPolicyDetailView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public NetworkPolicyDetailView()
    {
        InitializeComponent();
        PodsGrid.AddHandler(KeyDownEvent, OnPodKeyDown, RoutingStrategies.Tunnel);
        _rowLogs = RowLogsGesture.Track(PodsGrid);
    }

    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NetworkPolicyDetailTabViewModel vm)
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

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter)
        {
            vm.OpenPodCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is NetworkPolicyDetailTabViewModel vm)
        {
            vm.OpenPodCommand.Execute(null);
        }
    }

    private void OnPodLogsClick(object? sender, RoutedEventArgs e)
    {
        var shift = _rowLogs.TakeShift();
        e.Handled = true;
        if (sender is not Button { DataContext: MatchedPodViewModel pod } || DataContext is not NetworkPolicyDetailTabViewModel vm)
        {
            return;
        }

        _ = vm.OpenPodLogsAsync(pod, shift);
        RowLogsGesture.FocusOwningList(sender);
    }
}
