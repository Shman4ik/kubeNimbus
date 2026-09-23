using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

/// <summary>
/// One Argo CD Application's detail pane: what Git says it should be, what Argo made of
/// that, the objects it manages, its conditions and its deployment history. Pure XAML —
/// unlike the Helm pane there is no AvaloniaEdit here to push text into, so there is nothing
/// for code-behind to do beyond loading the markup.
/// </summary>
public partial class ArgoApplicationView : UserControl
{
    public ArgoApplicationView() => InitializeComponent();

    private void OnResourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.None
            && DataContext is ArgoApplicationTabViewModel vm
            && vm.OpenSelectedResourceLogsCommand.CanExecute(null))
        {
            vm.OpenSelectedResourceLogsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResourceLogsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ArgoApplicationTabViewModel vm
            && sender is Button { DataContext: ArgoResourceRowViewModel row })
        {
            vm.SelectedResource = row;
            row.OpenLogsCommand.Execute(null);
        }
    }
}
