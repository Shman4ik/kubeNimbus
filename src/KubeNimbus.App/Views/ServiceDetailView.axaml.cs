using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Service detail pane — declarative apart from the backends list's gestures, which are the
/// resource list's own: L / Shift+L and the row icon's Shift+click through
/// <see cref="RowLogsGesture"/>, Enter and double-click to open the pod.
/// </summary>
public partial class ServiceDetailView : UserControl
{
    private readonly RowLogsGesture _rowLogs;

    public ServiceDetailView()
    {
        InitializeComponent();

        // Tunnel: DataGrid's own class handler consumes Enter before a bubble handler sees it.
        BackendsGrid.AddHandler(KeyDownEvent, OnBackendKeyDown, RoutingStrategies.Tunnel);
        _rowLogs = RowLogsGesture.Track(BackendsGrid);
    }

    private void OnBackendKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ServiceDetailTabViewModel vm)
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

    private void OnBackendDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServiceDetailTabViewModel vm)
        {
            vm.OpenPodCommand.Execute(null);
        }
    }

    private void OnBackendLogsClick(object? sender, RoutedEventArgs e)
    {
        var shift = _rowLogs.TakeShift();
        e.Handled = true;
        if (sender is not Button { DataContext: ServiceBackendViewModel backend } || DataContext is not ServiceDetailTabViewModel vm)
        {
            return;
        }

        _ = vm.OpenPodLogsAsync(backend, shift);
        RowLogsGesture.FocusOwningList(sender);
    }
}
