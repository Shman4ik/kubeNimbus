using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

        // A DataGrid selects on left-click only, so without this the row context menu
        // would act on whatever was selected *before* the right-click — i.e. usually
        // not the row the menu opened over, which is the worst possible behaviour for
        // a menu whose last item is Delete.
        ResourceGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);

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
        ApplySummaryColumns();
    }

    /// <summary>
    /// One timer for the whole list, driving every row's Age and the "(43m ago)" on
    /// Restarts. Both are functions of wall-clock rather than of the object, so no
    /// watch event ever makes them change — a five-minute-old pod would read "5m"
    /// until something else about it happened. A timer per row would mean thousands of
    /// them on a busy cluster; a slower tick than this would let the seconds column of
    /// a young pod visibly lag.
    /// </summary>
    private static readonly TimeSpan AgeTickInterval = TimeSpan.FromSeconds(5);

    private DispatcherTimer? _ageTimer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ageTimer ??= CreateAgeTimer();
        _ageTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Stopped off-screen: a background tab's rows are not being looked at, and a
        // DispatcherTimer keeps running (and keeps this view alive) until it is told not to.
        _ageTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private DispatcherTimer CreateAgeTimer()
    {
        var timer = new DispatcherTimer { Interval = AgeTickInterval };
        timer.Tick += (_, _) =>
        {
            if (Vm is not { } vm)
            {
                return;
            }

            // Each RefreshTimes assignment is a no-op when the rendered string hasn't
            // changed (SetProperty compares first), so a tick over a list of day-old
            // pods raises no change notifications at all.
            foreach (var row in vm.Rows)
            {
                row.RefreshTimes();
            }
        };

        return timer;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClusterTabViewModel.IsInspectorMaximized))
        {
            ApplyDockState();
        }
        else if (e.PropertyName is nameof(ClusterTabViewModel.AreMetricsVisible)
                 or nameof(ClusterTabViewModel.AreUsageColumnsVisible)
                 or nameof(ClusterTabViewModel.IsAdvancedView))
        {
            ApplyMetricsColumns();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.IsFleetView))
        {
            ApplyFleetColumn();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.SelectedKind))
        {
            ApplySummaryColumns();
        }
    }

    /// <summary>
    /// Shows the CPU/Memory columns only for kinds metrics.k8s.io reports on, on
    /// clusters that actually run metrics-server, and only in the advanced view —
    /// which is what <c>AreUsageColumnsVisible</c> folds together. Code-behind because
    /// a DataGridColumn isn't part of the visual tree: it never inherits the
    /// DataContext, so <c>IsVisible="{Binding …}"</c> on a column can't work.
    /// Matched on header text rather than index so reordering the columns in
    /// XAML doesn't silently hide the wrong one.
    /// </summary>
    private void ApplyMetricsColumns()
    {
        var visible = Vm?.AreUsageColumnsVisible == true;
        foreach (var column in ResourceGrid.Columns)
        {
            if (column.Header is "CPU" or "Memory")
            {
                column.IsVisible = visible;
            }
        }
    }

    /// <summary>
    /// Shows Ready / Status / Restarts / Details according to what the selected kind
    /// actually reports — <see cref="ResourceStatusSummary"/> owns those answers, since
    /// they are a property of the kind and not of the view. Without this every
    /// ConfigMap list carried an empty 150px Status column and a grey dot, and no pod
    /// list carried READY or RESTARTS at all, which are two of the five columns anyone
    /// running <c>kubectl get pods</c> is actually looking at.
    /// Same code-behind reason as the usage columns above.
    /// </summary>
    private void ApplySummaryColumns()
    {
        var descriptor = Vm?.SelectedKind?.Descriptor;
        foreach (var column in ResourceGrid.Columns)
        {
            column.IsVisible = column.Header switch
            {
                "Ready" => ResourceStatusSummary.ShowsReady(descriptor),
                "Restarts" => ResourceStatusSummary.ShowsRestarts(descriptor),
                "Details" => ResourceStatusSummary.ShowsDetails(descriptor),
                "Status" or "" => ResourceStatusSummary.ShowsStatus(descriptor),
                _ => column.IsVisible,
            };
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

    /// <summary>
    /// Makes a right-click select the row under the cursor before the context flyout
    /// opens. Not handled (<c>e.Handled</c> stays false) so the flyout still opens
    /// normally — this only fixes which row it is about.
    /// </summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResourceGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        // Resolved from the event source rather than from a handler on the row
        // template: a DataGridRow's own padding lies outside its cell content, so a
        // handler in the template misses the gaps between cells entirely.
        for (var element = e.Source as Visual; element is not null; element = element.GetVisualParent())
        {
            if (element is DataGridRow { DataContext: ResourceRowViewModel row })
            {
                ResourceGrid.SelectedItem = row;
                return;
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
