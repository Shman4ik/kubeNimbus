using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Views;

public partial class ClusterTabView : UserControl
{
    // Remembered pixel height of the bottom dock, so toggling maximize off (or
    // reopening the dock) restores the height the user last dragged it to.
    private double _dockHeight = 300;

    private ClusterTabViewModel? _subscribed;

    public ClusterTabView()
    {
        InitializeComponent();

        // DataGrid's own class handler consumes Enter (commit-edit / move-down)
        // before a bubble-routed instance handler on the same element would ever
        // see it, so this has to run in the Tunnel phase to win.
        ResourceGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
    }

    private ClusterTabViewModel? Vm => DataContext as ClusterTabViewModel;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
            _subscribed.InspectorTabs.CollectionChanged -= OnInspectorTabsChanged;
        }

        _subscribed = Vm;

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnVmPropertyChanged;
            _subscribed.InspectorTabs.CollectionChanged += OnInspectorTabsChanged;
        }

        ApplyDockState();
        ApplyMetricsColumns();
        ApplyFleetColumn();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClusterTabViewModel.IsInspectorMaximized))
        {
            ApplyDockState();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.AreMetricsVisible))
        {
            ApplyMetricsColumns();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.IsFleetView))
        {
            ApplyFleetColumn();
        }
    }

    /// <summary>
    /// Shows the CPU/Memory columns only for kinds metrics.k8s.io reports on, on
    /// clusters that actually run metrics-server. Code-behind because a
    /// DataGridColumn isn't part of the visual tree: it never inherits the
    /// DataContext, so <c>IsVisible="{Binding …}"</c> on a column can't work.
    /// Matched on header text rather than index so reordering the columns in
    /// XAML doesn't silently hide the wrong one.
    /// </summary>
    private void ApplyMetricsColumns()
    {
        var visible = Vm?.AreMetricsVisible == true;
        foreach (var column in ResourceGrid.Columns)
        {
            if (column.Header is "CPU" or "Memory")
            {
                column.IsVisible = visible;
            }
        }
    }

    /// <summary>
    /// Shows the Cluster column only while the list is aggregating across clusters —
    /// same code-behind reason as the usage columns above.
    /// </summary>
    private void ApplyFleetColumn()
    {
        var visible = Vm?.IsFleetView == true;
        foreach (var column in ResourceGrid.Columns)
        {
            if (column.Header is "Cluster")
            {
                column.IsVisible = visible;
            }
        }
    }

    private void OnInspectorTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyDockState();

    /// <summary>
    /// Drives the three states of the bottom dock via the content grid's row heights:
    /// no inspector tabs (list fills, dock + splitter hidden), a normal split (draggable
    /// splitter, dock at its remembered pixel height), and maximized (list collapsed,
    /// dock fills). Done in code-behind rather than binding because a GridSplitter mutates
    /// the RowDefinition heights directly and would fight a one-way height binding.
    /// </summary>
    private void ApplyDockState()
    {
        var listRow = ContentRows.RowDefinitions[1];
        var dockRow = ContentRows.RowDefinitions[3];

        // Preserve whatever the user last dragged the dock to before we overwrite it.
        if (dockRow.Height.IsAbsolute && dockRow.Height.Value > 0)
        {
            _dockHeight = dockRow.Height.Value;
        }

        var hasTabs = Vm?.InspectorTabs.Count > 0;
        var maximized = Vm?.IsInspectorMaximized == true;

        DockRegion.IsVisible = hasTabs;

        if (!hasTabs)
        {
            DockSplitter.IsVisible = false;
            listRow.MinHeight = 90;
            listRow.Height = new GridLength(1, GridUnitType.Star);
            dockRow.MinHeight = 0;
            dockRow.Height = new GridLength(0);
        }
        else if (maximized)
        {
            DockSplitter.IsVisible = false;
            // Drop the list's MinHeight so the dock truly fills the content area
            // (otherwise a ~90px sliver of the list keeps peeking through).
            listRow.MinHeight = 0;
            listRow.Height = new GridLength(0);
            dockRow.MinHeight = 0;
            dockRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            DockSplitter.IsVisible = true;
            listRow.MinHeight = 90;
            listRow.Height = new GridLength(1, GridUnitType.Star);
            // Floor the dock so the splitter can't drag it down to an unusable sliver.
            dockRow.MinHeight = 140;
            dockRow.Height = new GridLength(Math.Max(_dockHeight, 140));
        }
    }

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

    private void OnHelmRowDoubleTapped(object? sender, TappedEventArgs e) =>
        Vm?.OpenSelectedHelmReleaseCommand.Execute(null);

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
