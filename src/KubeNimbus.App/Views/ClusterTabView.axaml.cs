using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Views;

public partial class ClusterTabView : UserControl
{
    // Remembered pixel height of the bottom dock, so toggling maximize off (or
    // reopening the dock) restores the height the user last dragged it to.
    private double _dockHeight = 300;

    private ClusterTabViewModel? _subscribed;

    /// <summary>
    /// The <c>Tag</c> the XAML gives every CRD printer-column slot. A sentinel, not an
    /// id: the slots are collected by it once, in the constructor, and each one is then
    /// addressed by the name of whatever CRD column it is currently drawing
    /// (<see cref="_slotIds"/>).
    /// </summary>
    private const string PrinterSlotTag = "crd-slot";

    private readonly List<DataGridColumn> _printerSlots = [];

    /// <summary>
    /// The id each printer slot is currently drawing — <c>crd:&lt;the CRD's own column
    /// name&gt;</c>, or null for a slot that is drawing nothing. Refreshed by
    /// <see cref="ApplyPrinterColumns"/>, which is the only thing that decides what a
    /// slot holds.
    /// </summary>
    private readonly string?[] _slotIds;

    /// <summary>The header text a column shows before the sort indicator is added.</summary>
    private readonly Dictionary<DataGridColumn, string> _labels = [];

    /// <summary>
    /// The width each column is declared with in XAML, so a kind with no remembered
    /// width gets the layout it shipped with rather than the previous kind's.
    /// </summary>
    private readonly Dictionary<DataGridColumn, DataGridLength> _declaredWidths = [];

    /// <summary>
    /// The grid's own columns — everything except the CRD printer slots.
    ///
    /// <para>
    /// Every <c>Apply*Columns</c> method below finds its columns by <c>Tag</c>. It used
    /// to find them by header text, and that was the wrong identifier twice over. A
    /// printer slot's header is a <em>CRD author's</em> string: cert-manager calls one
    /// of its Certificate columns <b>Ready</b>, which the first cut of this matched as
    /// the grid's own Ready column and promptly hid, so the CRD's most important column
    /// was silently missing from the very list that feature exists to fix. And the
    /// header is no longer a constant for the app's own columns either — the sort
    /// indicator is drawn into it. The slots are still excluded from the fixed set,
    /// because only <see cref="ApplyPrinterColumns"/> decides what they show.
    /// </para>
    /// </summary>
    private IEnumerable<DataGridColumn> FixedColumns =>
        ResourceGrid.Columns.Where(c => !_printerSlots.Contains(c));

    public ClusterTabView()
    {
        InitializeComponent();

        _printerSlots.AddRange(ResourceGrid.Columns.Where(c => c.Tag as string == PrinterSlotTag));
        _slotIds = new string?[_printerSlots.Count];

        foreach (var column in ResourceGrid.Columns)
        {
            _labels[column] = column.Header as string ?? "";
            _declaredWidths[column] = column.Width;
        }

        // A column resize has no event of its own — Avalonia's DataGrid changes the
        // column's width from inside the header's pointer handling and tells nobody —
        // so the gesture is bracketed instead: snapshot on press, compare on release.
        // Both handlers are on the grid rather than on the headers, which are created
        // and recycled by the template.
        ResourceGrid.AddHandler(PointerPressedEvent, OnGridPointerPressedForResize, RoutingStrategies.Tunnel);
        ResourceGrid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel);

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
        ApplyPrinterColumns();
        ApplySummaryColumns();
        ApplySidebarVisibility();
        ApplyColumnLayout();
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

            // The advanced view is also this app's `-o wide`: it adds and removes a
            // CRD's own priority-1 columns, so the printer slots (and, through them,
            // whether the generic Status column is suppressed) have to follow it.
            ApplyPrinterColumns();
            ApplySummaryColumns();
            ApplyColumnLayout();
        }
        else if (e.PropertyName is nameof(ClusterTabViewModel.PrinterColumns)
                 or nameof(ClusterTabViewModel.VisiblePrinterColumns))
        {
            // The set arrives asynchronously the first time a CRD kind is opened (one
            // GET of its CustomResourceDefinition), so this is the moment the list stops
            // showing the generic Status column and starts showing the CRD's own.
            ApplyPrinterColumns();
            ApplySummaryColumns();
            ApplyColumnLayout();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.IsFleetView))
        {
            ApplyFleetColumn();
            ApplyColumnLayout();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.SelectedKind))
        {
            ApplyPrinterColumns();
            ApplySummaryColumns();

            // The kind's own remembered widths, and the sort the view model has just
            // restored for it — the two halves of one stored layout, applied from the
            // one notification that says which kind is in front of the reader.
            ApplyColumnLayout();
        }
        else if (e.PropertyName == nameof(ClusterTabViewModel.IsSidebarVisible))
        {
            ApplySidebarVisibility();
        }
    }

    /// <summary>
    /// Shows or hides the resource-catalog sidebar by collapsing its column, not just
    /// the panel: a hidden Grid child leaves its column at full width, so hiding the
    /// sidebar alone would leave a third of the content area blank and the list exactly
    /// as narrow as before — which is the one thing the toggle exists to fix.
    ///
    /// <para>
    /// The star width is restored to the literal the XAML declares rather than
    /// remembered from before the hide. There is no splitter on this column, so the
    /// value can never have been dragged away from it, and reading it back would only
    /// create a way for "hidden" to be captured as the restore width.
    /// </para>
    /// </summary>
    private void ApplySidebarVisibility()
    {
        var visible = Vm?.IsSidebarVisible ?? true;

        Sidebar.IsVisible = visible;
        ContentColumns.ColumnDefinitions[0].Width = visible
            ? new GridLength(0.65, GridUnitType.Star)
            : new GridLength(0);
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
        foreach (var column in FixedColumns)
        {
            if (column.Tag is ResourceColumn.Cpu or ResourceColumn.Memory)
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

        // A CRD that declares its own printer columns has already answered the question
        // Status and Details exist to answer, and kubectl shows no generic status
        // beside them — so the two step aside rather than doubling up in a list that is
        // already tight on width (UI rule 14). Built-in kinds never reach this branch —
        // they are not CRDs, so VisiblePrinterColumns is always empty for them.
        var hasPrinterColumns = Vm?.VisiblePrinterColumns.Count > 0;

        foreach (var column in FixedColumns)
        {
            column.IsVisible = column.Tag switch
            {
                ResourceColumn.Ready => ResourceStatusSummary.ShowsReady(descriptor),
                ResourceColumn.Restarts => ResourceStatusSummary.ShowsRestarts(descriptor),
                ResourceColumn.Details => !hasPrinterColumns && ResourceStatusSummary.ShowsDetails(descriptor),
                ResourceColumn.Status => !hasPrinterColumns && ResourceStatusSummary.ShowsStatus(descriptor),
                // The 28px health dot, and it now shows *only* where the Status column
                // has stepped aside for a CRD's own printer columns. Beside a Status
                // pill it was the same fact twice in the same row — the pill is already
                // colour-coded by the same classification and also spells the word, so
                // the dot added a second encoding of it and no more. Where printer
                // columns replace Status the dot is the last thing carrying
                // ResourceStatusSummary's verdict at all, which is exactly why it stays
                // there and only there.
                ResourceColumn.Health => hasPrinterColumns && ResourceStatusSummary.ShowsStatus(descriptor),
                _ => column.IsVisible,
            };
        }
    }

    /// <summary>
    /// Labels and shows the grid's CRD printer-column slots from
    /// <see cref="ClusterTabViewModel.VisiblePrinterColumns"/>, and hides the rest.
    /// Same code-behind reason as every other column here — a DataGridColumn is outside
    /// the visual tree, so it inherits no DataContext and cannot bind its own header or
    /// visibility.
    ///
    /// <para>
    /// Note the ordering constraint: this must run before <see cref="ApplySummaryColumns"/>
    /// on every path, because that method reads the same set to decide whether the
    /// generic Status column steps aside. They are called as a pair for that reason.
    /// </para>
    /// </summary>
    private void ApplyPrinterColumns()
    {
        var columns = Vm?.VisiblePrinterColumns ?? [];

        for (var i = 0; i < _printerSlots.Count; i++)
        {
            var slot = _printerSlots[i];
            if (i < columns.Count)
            {
                // A plain string, not a TextBlock carrying the CRD's `description` as a
                // tooltip: DataGridColumn is not a Control, so ToolTip.SetTip does not
                // apply to it, and a Control header would opt the cell out of Fluent's
                // own column-header template. The descriptions are short and mostly
                // restate the name; the column header is not worth a styling regression.
                _labels[slot] = columns[i].Name;
                _slotIds[i] = ResourceColumn.Printer(columns[i].Name);
                slot.Header = columns[i].Name;
                slot.IsVisible = true;
            }
            else
            {
                _labels[slot] = "";
                _slotIds[i] = null;
                slot.IsVisible = false;
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
        foreach (var column in FixedColumns)
        {
            if (column.Tag is ResourceColumn.Cluster)
            {
                column.IsVisible = visible;
            }
        }
    }

    // ------------------------------------------------------------------ the grid
    // Column widths and sort order, per kind. A fixed re-cut of the column widths
    // trades one column's truncation for another's — the audit measured two pods of
    // one ReplicaSet rendering identically because the Name ellipsis fell exactly on
    // the discriminating suffix, while the Namespace column beside it spent 110px on
    // the same value repeated down every row — and no single set of numbers survives a
    // CRD that declares eleven columns of its own. So the widths are the reader's to
    // set, and what they set is remembered for that kind.

    /// <summary>
    /// The id a column is addressed by: its <c>Tag</c>, except for a printer slot,
    /// which is addressed by the CRD column it is currently drawing (a slot number
    /// would move under the advanced-view switch — see <see cref="ResourceColumn.Printer"/>).
    /// Null for a column that identifies nothing right now: an empty printer slot.
    /// </summary>
    private string? ColumnId(DataGridColumn column)
    {
        var slot = _printerSlots.IndexOf(column);
        return slot >= 0 ? _slotIds[slot] : column.Tag as string;
    }

    /// <summary>
    /// A header click. The DataGrid's own sorting is deliberately not used — it orders
    /// the collection view behind <c>ItemsSource</c>, and this list's items are the
    /// informer's rows, whose order UI rule 13 pins as the watch's own. Handling the
    /// event stops that and hands the click to
    /// <see cref="ClusterTabViewModel.ToggleSort"/>, which orders <c>VisibleRows</c>
    /// (the rendered projection) and nothing else.
    ///
    /// <para>
    /// The event only fires at all because every column carries
    /// <c>CanUserSort="True"</c>: these are template columns with no
    /// <c>SortMemberPath</c>, and Avalonia's <c>ProcessSort</c> returns before raising
    /// <c>Sorting</c> for a column whose <c>CanUserSort</c> is false — which is the
    /// default for a template column, and is why clicking a header did nothing at all
    /// before this.
    /// </para>
    /// </summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;

        if (Vm is { } vm && ColumnId(e.Column) is { } id && id != ResourceColumn.Health)
        {
            vm.ToggleSort(id);
            ApplySortIndicator();
        }
    }

    /// <summary>
    /// Draws the sort arrow into the sorted column's header.
    ///
    /// <para>
    /// Fluent's own <c>:sortascending</c>/<c>:sortdescending</c> header pseudo-classes
    /// are set from the collection view's sort descriptions, which stay empty here
    /// precisely because the sorting is ours — so the indicator has to be drawn rather
    /// than styled. Into the header <em>text</em>, rather than through a header
    /// template: a templated header is a <c>Control</c> in place of a string, which
    /// opts the cell out of Fluent's own column-header template (the same reason a
    /// printer column's description is not a header tooltip).
    /// </para>
    /// </summary>
    private void ApplySortIndicator()
    {
        var sorted = Vm?.SortColumnId;
        var arrow = Vm?.SortDescending == true ? " \u2193" : " \u2191";

        foreach (var column in ResourceGrid.Columns)
        {
            if (!_labels.TryGetValue(column, out var label) || label.Length == 0)
            {
                continue;
            }

            column.Header = sorted is not null && ColumnId(column) == sorted ? label + arrow : label;
        }
    }

    /// <summary>
    /// The widths of every identifiable column, as they are right now. Star columns are
    /// read as their star value and everything else as its rendered pixels, because
    /// that is what each kind of column keeps across a drag: Avalonia re-derives a star
    /// column's ratio (2* becomes 2.52*) and leaves an Auto column's declared width
    /// alone, changing only what it displays.
    /// </summary>
    private Dictionary<string, GridColumnWidth> CurrentWidths()
    {
        var widths = new Dictionary<string, GridColumnWidth>(StringComparer.Ordinal);
        foreach (var column in ResourceGrid.Columns)
        {
            if (!column.IsVisible || ColumnId(column) is not { } id || id == ResourceColumn.Health)
            {
                continue;
            }

            widths[id] = column.Width.IsStar
                ? new GridColumnWidth(GridColumnWidth.Star, Math.Round(column.Width.Value, 4))
                : new GridColumnWidth(GridColumnWidth.Pixels, Math.Round(column.ActualWidth, 1));
        }

        return widths;
    }

    private Dictionary<string, GridColumnWidth>? _widthsAtPress;

    private void OnGridPointerPressedForResize(object? sender, PointerPressedEventArgs e) =>
        _widthsAtPress = e.GetCurrentPoint(ResourceGrid).Properties.IsLeftButtonPressed ? CurrentWidths() : null;

    /// <summary>
    /// The other half of the drag bracket: whatever changed width between the press and
    /// the release was dragged, and only that is remembered. Saving every column
    /// instead would pin the Auto columns to whatever their content happened to measure
    /// at that moment, which is a choice nobody made.
    /// </summary>
    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var before = _widthsAtPress;
        _widthsAtPress = null;

        if (before is null || Vm?.GridLayoutKey is not { } key)
        {
            return;
        }

        var changed = new Dictionary<string, GridColumnWidth>(StringComparer.Ordinal);
        foreach (var (id, width) in CurrentWidths())
        {
            if (!before.TryGetValue(id, out var was) || was.Unit != width.Unit
                || Math.Abs(was.Value - width.Value) > 0.5)
            {
                changed[id] = width;
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        GridLayoutStore.Update(key, layout =>
        {
            var widths = new Dictionary<string, GridColumnWidth>(layout.Widths, StringComparer.Ordinal);
            foreach (var (id, width) in changed)
            {
                widths[id] = width;
            }

            return layout with { ColumnWidths = widths };
        });
    }

    /// <summary>
    /// Puts the selected kind's remembered widths back, and resets every other column to
    /// the width the XAML declares — a kind nobody has dragged must show its own layout,
    /// not the one left behind by the kind before it. Runs after the visibility passes,
    /// since a hidden column's width is not a width anyone chose.
    /// </summary>
    private void ApplyColumnLayout()
    {
        var widths = Vm?.GridLayoutKey is { } key ? GridLayoutStore.Load(key).Widths : null;

        foreach (var column in ResourceGrid.Columns)
        {
            var stored = widths is not null && ColumnId(column) is { } id && widths.TryGetValue(id, out var width)
                ? width
                : null;

            column.Width = stored switch
            {
                { Unit: GridColumnWidth.Star } => new DataGridLength(stored.Value, DataGridLengthUnitType.Star),
                { Unit: GridColumnWidth.Pixels } => new DataGridLength(stored.Value, DataGridLengthUnitType.Pixel),
                _ => _declaredWidths.TryGetValue(column, out var declared) ? declared : column.Width,
            };
        }

        ApplySortIndicator();
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

    /// <summary>
    /// Ctrl/Cmd+F, routed here by <see cref="MainWindow"/> — the gesture is registered
    /// on the window because the key can arrive with focus anywhere in the shell, and a
    /// KeyBinding on this control only sees events that already reach it.
    /// </summary>
    public void FocusRowFilter()
    {
        if (Vm is not { IsHelmView: false })
        {
            return; // the box isn't in the Helm browser, and focusing a hidden TextBox is a dead keystroke
        }

        RowFilterBox.Focus();
        RowFilterBox.SelectAll();
    }

    /// <summary>
    /// Esc and Enter in the search box. Esc clears a filter and, when there is nothing
    /// left to clear, hands focus back to the list — so the key always does something
    /// rather than being swallowed. Enter/Down move to the rows, which is where you
    /// were heading after typing the name.
    /// </summary>
    private void OnRowFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.IsRowFiltering)
            {
                vm.ClearRowFilterCommand.Execute(null);
            }
            else
            {
                ResourceGrid.Focus();
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            vm.SelectedRow ??= vm.VisibleRows.FirstOrDefault();
            ResourceGrid.Focus();
            e.Handled = true;
        }
    }

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
