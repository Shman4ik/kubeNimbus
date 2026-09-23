using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class WorkloadDetailView : UserControl
{
    public WorkloadDetailView()
    {
        InitializeComponent();
        PodGrid.AddHandler(PointerPressedEvent, PodRowContextSelection.OnPointerPressed, RoutingStrategies.Tunnel);
    }
    private void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is WorkloadDetailTabViewModel vm) vm.OpenPodCommand.Execute(null);
    }
    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || DataContext is not WorkloadDetailTabViewModel vm) return;
        if (e.Key == Key.L) { vm.OpenPodLogsCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Enter) { vm.OpenPodCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.S) { vm.ShellCommand.Execute(null); e.Handled = true; }
    }
    private void OnPodLogsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkloadDetailTabViewModel vm && sender is Button { DataContext: ResourceRowViewModel pod })
        {
            vm.SelectedPod = pod;
            _ = vm.OpenPodLogsAsync(pod);
        }
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.E && DataContext is WorkloadDetailTabViewModel vm)
        { vm.OpenYamlCommand.Execute(null); e.Handled = true; }
    }
}
