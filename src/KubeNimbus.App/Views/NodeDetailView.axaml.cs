using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// Node detail pane — purely declarative, all state lives in
/// <see cref="ViewModels.NodeDetailTabViewModel"/>. The mutating actions
/// (cordon / uncordon / drain) are deliberately not here: they land on the cluster tab's
/// shared confirm strip, as every other mutating action does (UI rule 17).
/// </summary>
public partial class NodeDetailView : UserControl
{
    public NodeDetailView() => InitializeComponent();

    private void OnPodKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.None
            && DataContext is NodeDetailTabViewModel vm && vm.OpenPodLogsCommand.CanExecute(null))
        {
            vm.OpenPodLogsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPodLogsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodeDetailTabViewModel vm && sender is Button { DataContext: NodePodViewModel pod })
        {
            vm.SelectedPod = pod;
            _ = vm.OpenPodLogsAsync(pod);
        }
    }
}
