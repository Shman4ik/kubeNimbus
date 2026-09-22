using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class WorkloadDetailView : UserControl
{
    public WorkloadDetailView() => InitializeComponent();
    private void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is WorkloadDetailTabViewModel vm) vm.OpenPodCommand.Execute(null);
    }
    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || DataContext is not WorkloadDetailTabViewModel vm) return;
        if (e.Key is Key.L or Key.Enter) { vm.OpenPodCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.S) { vm.ShellCommand.Execute(null); e.Handled = true; }
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.E && DataContext is WorkloadDetailTabViewModel vm)
        { vm.OpenYamlCommand.Execute(null); e.Handled = true; }
    }
}
