using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class ClusterTabView : UserControl
{
    public ClusterTabView()
    {
        InitializeComponent();

        // DataGrid's own class handler consumes Enter (commit-edit / move-down)
        // before a bubble-routed instance handler on the same element would ever
        // see it, so this has to run in the Tunnel phase to win.
        ResourceGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
    }

    private ClusterTabViewModel? Vm => DataContext as ClusterTabViewModel;

    private void OnSidebarKindTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SidebarKindViewModel kind } && Vm is { } vm)
        {
            vm.SelectKindCommand.Execute(kind);
        }
    }

    private void OnSidebarSectionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: SidebarSectionViewModel section })
        {
            section.ToggleExpandedCommand.Execute(null);
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => Vm?.OpenSelectedCommand.Execute(null);

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            vm.PeekSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnInspectorTabTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: InspectorTabViewModelBase tab } && Vm is { } vm)
        {
            vm.SelectInspectorTabCommand.Execute(tab);
        }
    }
}
