using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One connected cluster (one kubeconfig context) — owns its ClusterClient,
/// discovery-built sidebar, the live list for whichever resource kind/namespace
/// is selected, and the inspector tab strip (pod detail / YAML / exec /
/// port-forward). Tabs are multi-cluster because there's one of these per tab.
/// </summary>
public sealed partial class ClusterTabViewModel : ObservableObject, IAsyncDisposable
{
    public const string AllNamespaces = "All namespaces";

    /// <summary>
    /// How often metrics are re-read, from settings. metrics.k8s.io aggregates over a
    /// ~30s window, so polling faster than the default buys no resolution; the setting
    /// exists for the other direction — a large cluster or a metered link, where this
    /// being the app's only poll makes it the only thing worth turning down.
    /// </summary>
    private static TimeSpan MetricsPollInterval =>
        TimeSpan.FromSeconds(App.LoadSettings().MetricsPollSeconds);

    private readonly Dictionary<string, ResourceRowViewModel> _rowsByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Which cluster served each row, in fleet mode — a row's client and descriptor
    /// have to come from its own cluster, not from this tab's, or opening a row from
    /// cluster B would apply YAML to cluster A. Empty outside fleet mode.
    /// </summary>
    private readonly Dictionary<string, FleetTarget> _fleetTargets = new(StringComparer.Ordinal);

    /// <summary>
    /// The core/v1 Pod descriptor per cluster (the empty key is this tab's own), as that
    /// cluster's discovery reported it. Cached rather than fetched on demand because the
    /// capability checks it feeds — can this node be drained, i.e. does this server serve
    /// <c>pods/eviction</c> — are synchronous <c>CanExecute</c> answers, and because a
    /// drain in an aggregated list has to evict through the row's <em>own</em> cluster.
    /// </summary>
    private readonly Dictionary<string, ResourceDescriptor> _podDescriptors = new(StringComparer.Ordinal);

    private CancellationTokenSource? _watchCts;
    private bool _metricsApiAvailable;

    public ClusterContext Context { get; }

    public string Header => Context.Name;

    /// <summary>
    /// True for the built-in demo cluster: a normal tab over a dataset that ships with
    /// the app, with no <see cref="ClusterClient"/> behind it. <see cref="Client"/>
    /// stays null for the tab's whole life, which is what makes "a demo tab never
    /// connects, never watches and never touches the network" structural rather than a
    /// rule to remember — the branches below are the only places that fill in for it.
    /// See CLAUDE.md's "Demo cluster" section.
    /// </summary>
    public bool IsDemo => Context.IsDemo;

    /// <summary>
    /// The banner the content area carries for the whole life of a demo tab. This is a
    /// deliberate exception to UI rule 1 ("justify anything always-visible"), and the
    /// justification is the alternative: a user believing a screen full of invented
    /// pods is their own cluster.
    /// </summary>
    public const string DemoBanner =
        "Demo cluster — sample data that ships with kubeNimbus. Nothing is connected and none of these objects exist.";

    /// <summary>
    /// What the *status bar* says for a demo tab, and deliberately not
    /// <see cref="DemoBanner"/>. The banner above the content area and the status bar
    /// are simultaneously visible for the whole life of the tab, and both were printing
    /// the identical 100-character sentence — the same sentence twice on one screen, and
    /// three times on any screen that also arms the confirm strip.
    ///
    /// The banner is the copy that has to stay: demo rule 6 makes it the exception to
    /// UI rule 1 precisely because it must be unmissable and persistent. So the status
    /// bar reverts to its own register instead — the slot that reads
    /// "Connected — Kubernetes v1.31.2." on a real tab says what this tab is instead of
    /// a connection.
    /// </summary>
    public const string DemoStatus = "Demo cluster — sample data, no connection.";

    /// <summary>
    /// Which environment this cluster is treated as — set by
    /// <see cref="MainWindowViewModel"/>, which owns the user's overrides. Drives the
    /// tab's colour and the production band under the command bar; the whole point
    /// is that a production cluster is distinguishable from a sandbox at a glance,
    /// before anything is clicked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentLabel))]
    [NotifyPropertyChangedFor(nameof(HasEnvironmentLabel))]
    [NotifyPropertyChangedFor(nameof(IsProduction))]
    private ClusterEnvironment _environment;

    public string? EnvironmentLabel => Environment.Label();

    public bool HasEnvironmentLabel => EnvironmentLabel is not null;

    /// <summary>Drives the one piece of always-visible chrome the colour scheme adds.</summary>
    public bool IsProduction => Environment == ClusterEnvironment.Production;

    /// <summary>
    /// True while this is the shell's selected tab. Kept on the tab rather than
    /// compared in the view because the strip is an ItemsControl, not a Selector —
    /// there is no built-in selected state to style against.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public ClusterClient? Client { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isConnected;

    /// <summary>
    /// Neither connected nor connecting. The tab's status dot needs three states, not
    /// two: opening a cluster that is still dialling looked identical to one that
    /// failed, so picking a cluster appeared to do nothing until it finished.
    /// </summary>
    public bool IsIdle => !IsConnected && !IsConnecting;

    [ObservableProperty]
    private string _status = "Not connected.";

    [ObservableProperty]
    private string? _connectionWarning;

    public ObservableCollection<SidebarSectionViewModel> SidebarSections { get; } = [];

    [ObservableProperty]
    private string _sidebarFilter = "";

    public ObservableCollection<string> NamespaceOptions { get; } = [AllNamespaces];

    [ObservableProperty]
    private string _selectedNamespace = AllNamespaces;

    [ObservableProperty]
    private SidebarKindViewModel? _selectedKind;

    /// <summary>
    /// Restores the sort this kind was last left in, and nothing else. Column widths are
    /// the view's own half of the same record — pixels are not view-model state — and
    /// <c>ClusterTabView</c> applies them from the same key off the same notification.
    ///
    /// <para>
    /// In the property's own changed hook rather than in <see cref="SelectKindCommand"/>
    /// because the sidebar is not the only thing that sets the kind: the palette, the
    /// Recent section and the screenshot harness all assign it directly, and a
    /// restore that only happened on one of those paths would look like the choice
    /// being forgotten at random.
    /// </para>
    /// </summary>
    partial void OnSelectedKindChanged(SidebarKindViewModel? value)
    {
        var layout = value is { IsHelmReleases: false, IsArgoDashboard: false, Descriptor: { } descriptor }
            ? GridLayoutStore.Load(GridLayoutStore.KeyFor(descriptor))
            : GridLayout.Empty;

        // persist: false — this is reading a choice back, not making one, and writing it
        // straight back would turn every kind ever opened into a stored layout.
        SetSort(layout.SortColumn, layout.SortDescending, persist: false);
    }

    /// <summary>
    /// Every row the watch knows about — the informer's own view of the cluster.
    /// Added/Modified/Deleted are applied against this by key, so nothing may be
    /// removed from it for display reasons: a row filtered out of sight has to stay
    /// here, or the next watch event for that object would look like a fresh add.
    /// </summary>
    public ObservableCollection<ResourceRowViewModel> Rows { get; } = [];

    /// <summary>
    /// What the list actually renders: <see cref="Rows"/> minus whatever
    /// <see cref="RowFilter"/> excludes. Kept in sync from <see cref="Rows"/>'s own
    /// <c>CollectionChanged</c>, so every producer — the watch, the fleet merge, the
    /// demo dataset, the screenshot fixtures — keeps writing to <c>Rows</c> and
    /// exactly one place in the app knows the filter exists.
    /// </summary>
    public ObservableCollection<ResourceRowViewModel> VisibleRows { get; } = [];

    /// <summary>
    /// Free-text filter over the list, matched against the columns that identify an
    /// object (see <see cref="ResourceRowViewModel.Matches"/>). The sidebar filters
    /// <em>kinds</em>; nothing filtered the objects, so finding one pod in a namespace
    /// of two hundred meant scrolling — which is the one thing <c>kubectl get | grep</c>
    /// has always been for. Cleared when the selected kind changes: it is a question
    /// about the list it was typed into.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowFiltering))]
    private string _rowFilter = "";

    /// <summary>Trimmed <see cref="RowFilter"/>, cached so matching a 5000-row list
    /// doesn't re-trim the same string once per row.</summary>
    private string _rowQuery = "";

    public bool IsRowFiltering => _rowQuery.Length > 0;

    /// <summary>"12 of 87" — shown beside the box whenever a filter is on, because a
    /// list that is short and doesn't say why is indistinguishable from a small
    /// cluster.</summary>
    [ObservableProperty]
    private string _rowFilterSummary = "";

    partial void OnRowFilterChanged(string value)
    {
        // The filter TextBox's two-way binding can round-trip null on control
        // (re)creation — same reasoning as SelectedNamespace and SidebarFilter.
        _rowQuery = (value ?? "").Trim();
        RebuildVisibleRows();
    }

    [RelayCommand]
    private void ClearRowFilter() => RowFilter = "";

    private bool MatchesRowFilter(ResourceRowViewModel row) => _rowQuery.Length == 0 || row.Matches(_rowQuery);

    /// <summary>
    /// Which column the list is ordered by (a <see cref="ResourceColumn"/> id), or null
    /// for the watch's own arrival order — which is the default and a real third state,
    /// not "unsorted by accident": it is the order the informer holds the objects in,
    /// and clicking a header a third time comes back to it.
    /// </summary>
    [ObservableProperty]
    private string? _sortColumnId;

    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>
    /// The header-click cycle: this column ascending, this column descending, then off.
    /// Three states rather than the two a DataGrid gives by default, because the third
    /// is the only way back to arrival order — and on a live list that order is
    /// information, not noise (it is what puts a newly created object where the watch
    /// put it).
    /// </summary>
    internal void ToggleSort(string columnId)
    {
        if (!string.Equals(SortColumnId, columnId, StringComparison.Ordinal))
        {
            SetSort(columnId, descending: false);
        }
        else if (!SortDescending)
        {
            SetSort(columnId, descending: true);
        }
        else
        {
            SetSort(null, descending: false);
        }
    }

    /// <summary>
    /// Sets the sort and re-orders what is on screen. Persisted per kind, so the choice
    /// survives switching to another kind and back (and a restart) — see
    /// <see cref="GridLayoutStore"/>.
    /// </summary>
    internal void SetSort(string? columnId, bool descending, bool persist = true)
    {
        SortColumnId = columnId;
        SortDescending = descending;
        RebuildVisibleRows();

        if (persist && GridLayoutKey is { } key)
        {
            GridLayoutStore.Update(key, layout => layout with
            {
                SortColumn = columnId,
                SortDescending = descending,
            });
        }
    }

    /// <summary>
    /// The key this kind's column widths and sort are remembered under, or null where
    /// there is nothing to remember: no kind selected, or one of the two browsers that
    /// replace the resource list entirely — Helm and the Argo dashboard — each of which is a
    /// different grid with its own columns.
    /// </summary>
    internal string? GridLayoutKey =>
        !IsHelmView && !IsArgoView && SelectedKind?.Descriptor is { } descriptor
            ? GridLayoutStore.KeyFor(descriptor)
            : null;

    /// <summary>
    /// The comparer for the current sort, or null when the list is in arrival order.
    /// Rebuilt per sort pass rather than cached: it closes over the printer columns,
    /// which arrive asynchronously the first time a CRD kind is opened.
    /// </summary>
    private ResourceRowComparer? RowComparer =>
        SortColumnId is { } columnId && ResourceRowComparer.CanSort(columnId, VisiblePrinterColumns)
            ? new ResourceRowComparer(columnId, SortDescending, VisiblePrinterColumns)
            : null;

    private void RebuildVisibleRows()
    {
        // The selection is restored rather than left to the DataGrid, which clears it on
        // a Clear() — losing the row someone had selected because they typed into the
        // search box (or sorted the list they were reading) is a small bug with a
        // disproportionate cost: the row actions and the inspector all hang off it.
        var selected = SelectedRow;

        VisibleRows.Clear();

        var comparer = RowComparer;
        if (comparer is null)
        {
            foreach (var row in Rows)
            {
                if (MatchesRowFilter(row))
                {
                    VisibleRows.Add(row);
                }
            }
        }
        else
        {
            var sorted = Rows.Where(MatchesRowFilter).ToList();
            sorted.Sort(comparer);
            foreach (var row in sorted)
            {
                VisibleRows.Add(row);
            }
        }

        if (selected is not null && VisibleRows.Contains(selected))
        {
            SelectedRow = selected;
        }

        RecomputeListEmpty();
    }

    /// <summary>
    /// Where a row belongs in the sorted list — a binary search, so a watch tick on a
    /// sorted 5000-row list costs a handful of comparisons rather than a re-sort.
    /// </summary>
    private int SortedIndexFor(ResourceRowViewModel row, ResourceRowComparer comparer, int limit = -1)
    {
        var low = 0;
        var high = limit < 0 ? VisibleRows.Count : limit;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (comparer.Compare(VisibleRows[middle], row) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Re-orders the rendered list in place, without clearing it. An insertion pass
    /// rather than a sort: each out-of-order row is moved to where it belongs among the
    /// rows above it, so a nearly-sorted list costs one comparison per row and a list
    /// that has not moved at all costs nothing but the comparisons.
    ///
    /// <para>
    /// The point is what it does <em>not</em> raise. Rebuilding raises a Reset, and a
    /// DataGrid answers a Reset by dropping the selection and the scroll position — which
    /// is fine for a header click (the reader just asked for a new order) and unusable
    /// for the metrics poll, which would otherwise throw a CPU-sorted list back to the
    /// top every fifteen seconds.
    /// </para>
    /// </summary>
    internal void ResortVisibleRows()
    {
        if (RowComparer is not { } comparer)
        {
            return;
        }

        for (var i = 1; i < VisibleRows.Count; i++)
        {
            var row = VisibleRows[i];
            if (comparer.Compare(VisibleRows[i - 1], row) <= 0)
            {
                continue;
            }

            VisibleRows.RemoveAt(i);
            VisibleRows.Insert(SortedIndexFor(row, comparer, i), row);
        }
    }

    /// <summary>
    /// Moves a row that a watch event has just changed back to where the sort says it
    /// belongs — a Modified can change the very value the list is ordered by (a pod
    /// going CrashLoopBackOff while the list is sorted by Status is the case that
    /// matters), and a sorted list that quietly stops being sorted is worse than an
    /// unsorted one. Does nothing while the row is still in order, so a status refresh
    /// that changes nothing relevant moves nothing on screen.
    /// </summary>
    private void RepositionRow(ResourceRowViewModel row)
    {
        if (RowComparer is not { } comparer)
        {
            return;
        }

        var index = VisibleRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var inOrder = (index == 0 || comparer.Compare(VisibleRows[index - 1], row) <= 0)
            && (index == VisibleRows.Count - 1 || comparer.Compare(row, VisibleRows[index + 1]) <= 0);
        if (inOrder)
        {
            return;
        }

        VisibleRows.RemoveAt(index);
        VisibleRows.Insert(Math.Min(SortedIndexFor(row, comparer), VisibleRows.Count), row);
    }

    /// <summary>
    /// Mirrors <see cref="Rows"/> into <see cref="VisibleRows"/> through the filter and
    /// the sort.
    /// Rows only ever appends (watch, fleet merge, demo dataset) or removes by object,
    /// so those two cases are handled incrementally and a watch tick on a filtered
    /// list costs one match, not a rescan; anything else — a Clear, an insert in the
    /// middle — falls back to a rebuild rather than guessing at an index.
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add
                when e.NewItems is { } added && e.NewStartingIndex + added.Count == Rows.Count:
                var comparer = RowComparer;
                foreach (var row in added.OfType<ResourceRowViewModel>())
                {
                    if (!MatchesRowFilter(row))
                    {
                        continue;
                    }

                    // Appended in arrival order, or inserted where the sort puts it —
                    // a sorted list that appends new objects at the bottom is a list
                    // that stops being sorted the moment anything is created.
                    if (comparer is null)
                    {
                        VisibleRows.Add(row);
                    }
                    else
                    {
                        VisibleRows.Insert(SortedIndexFor(row, comparer), row);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed:
                foreach (var row in removed.OfType<ResourceRowViewModel>())
                {
                    VisibleRows.Remove(row);
                }

                break;

            default:
                RebuildVisibleRows();
                return; // already recomputed the counters
        }

        RecomputeListEmpty();
    }

    /// <summary>True from the moment a watch (re)starts until its first event
    /// arrives — distinguishes "still loading" from "genuinely empty" so the
    /// list doesn't flash an empty state while the initial list is in flight.</summary>
    [ObservableProperty]
    private bool _isListLoading;

    /// <summary>True once the list has genuinely settled on zero rows (not
    /// merely mid-load) — drives the "No <kind> found" empty state.</summary>
    [ObservableProperty]
    private bool _isListEmpty;

    /// <summary>True when the kind has rows but the filter matches none of them. A
    /// distinct state from <see cref="IsListEmpty"/> on purpose (UI rule 9): "this
    /// namespace has no pods" and "no pod here is called that" send you looking for
    /// two completely different problems.</summary>
    [ObservableProperty]
    private bool _isFilterEmpty;

    partial void OnIsListLoadingChanged(bool value) => RecomputeListEmpty();

    private void RecomputeListEmpty()
    {
        IsListEmpty = Rows.Count == 0 && !IsListLoading;
        IsFilterEmpty = Rows.Count > 0 && VisibleRows.Count == 0 && !IsListLoading;
        RowFilterSummary = IsRowFiltering ? $"{VisibleRows.Count} of {Rows.Count}" : "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPodRowSelected))]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanScaleSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanRestartSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanAggregateLogsForSelectedRow))]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkloadLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenPreviousLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecIntoSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(PortForwardSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedYamlCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScaleSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(CanCordonSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanUncordonSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanDrainSelectedRow))]
    [NotifyCanExecuteChangedFor(nameof(CordonSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UncordonSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(CanSyncSelectedArgoApplication))]
    [NotifyCanExecuteChangedFor(nameof(SyncArgoApplicationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshArgoApplicationCommand))]
    private ResourceRowViewModel? _selectedRow;

    /// <summary>
    /// True when the CPU/Memory columns have anything to say: the cluster runs
    /// metrics-server *and* the selected kind is one metrics.k8s.io reports on
    /// (pods, nodes). The columns are shown/hidden from
    /// <see cref="Views.ClusterTabView"/>'s code-behind — DataGridColumn lives
    /// outside the visual tree, so it can't bind to the DataContext.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreUsageColumnsVisible))]
    private bool _areMetricsVisible;

    /// <summary>
    /// The one global "advanced view" switch, mirrored onto every tab by
    /// <see cref="MainWindowViewModel"/> (which owns it and persists it). <b>On by
    /// default.</b> It governs one thing: whether the sidebar shows the sections most
    /// sessions never open — Cluster and CRDs, see
    /// <see cref="SidebarGrouping.IsAdvancedSection"/>. Off gives a sidebar of the
    /// kinds people actually browse; on shows the whole catalog.
    ///
    /// <para>
    /// It is a *display* switch and nothing more: flipping it must never restart a
    /// watch, refetch anything, or lose list/inspector state. It is also confined to
    /// the sidebar — it used to strip the list's usage columns, pod detail's Usage tab,
    /// the fleet toggle, both log toolbars, YAML force-apply, the Helm and RBAC palette
    /// entries and a CRD's own priority-1 columns, which answered a complaint about the
    /// sidebar by hiding things all over the content area. Nothing new may be gated on
    /// it outside the sidebar.
    /// </para>
    ///
    /// <para>
    /// Bind this two-way and nothing else. A <c>ToggleButton</c> given BOTH an
    /// <c>IsChecked</c> binding and a toggling <c>Command</c> flips the property in
    /// <c>OnClick()</c> before the command runs, so an inverting command lands back
    /// on the original value — a guaranteed no-op, and a bug this repo has shipped
    /// before.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isAdvancedView = true;

    /// <summary>
    /// Write-back for the sidebar's advanced-view chip, set by
    /// <see cref="MainWindowViewModel"/> as each tab enters the strip. The switch is
    /// global but is toggled from a per-tab control, so the tab has to tell the shell
    /// — which then persists it and mirrors it onto the other tabs. Same shape as
    /// <see cref="FleetMembersProvider"/>: a tab still knows nothing about its
    /// siblings.
    /// </summary>
    public Action<bool>? AdvancedViewChanged { get; set; }

    partial void OnIsAdvancedViewChanged(bool value)
    {
        // The sidebar is the whole of what this switch does now. It used to also strip
        // the list's usage columns, pod detail's Usage tab, the fleet toggle, the log
        // toolbars, YAML force-apply, the Helm and RBAC palette entries and a CRD's own
        // priority-1 columns — i.e. it answered "too much in the sidebar" by hiding
        // things all over the content area, where nothing was crowded and where what it
        // hid was mostly what somebody had gone looking for.
        ApplySidebarChrome();

        AdvancedViewChanged?.Invoke(value);
    }

    /// <summary>
    /// The shell's sidebar-visibility switch, mirrored here so the view can bind it
    /// with a compiled binding against its own DataContext. Same arrangement as
    /// <see cref="IsAdvancedView"/> and for the same reason; the shell owns the value
    /// and persists it, this is the tab's copy.
    ///
    /// <para>
    /// The view acts on this from code-behind rather than binding a
    /// <c>ColumnDefinition.Width</c>: hiding a Grid child does not collapse the column
    /// it sits in, so the width itself has to move, and the width is a star value the
    /// layout owns — the same reason <c>ApplyDockState</c> mutates row heights instead
    /// of binding them.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarVisible = true;

    /// <summary>
    /// The shell's sidebar width, mirrored here for the same reason
    /// <see cref="IsSidebarVisible"/> is. Read by <c>ClusterTabView</c>'s code-behind,
    /// which owns the column: a GridSplitter writes <c>ColumnDefinition.Width</c>
    /// directly and would fight a one-way binding, the same conflict
    /// <c>ApplyDockState</c> has with the dock's rows.
    /// </summary>
    [ObservableProperty]
    private double _sidebarWidth = AppSettings.DefaultSidebarWidth;

    /// <summary>
    /// Write-back for a splitter drag, set by <see cref="MainWindowViewModel"/> as each
    /// tab enters the strip — same shape as <see cref="AdvancedViewChanged"/> and for
    /// the same reason. The width is global but the splitter is per tab, so the tab has
    /// to tell the shell, which persists it and mirrors it onto the others.
    ///
    /// <para>
    /// Without this the drag looks entirely correct and is lost: the column resizes,
    /// this property follows it, and neither the other tabs nor <c>settings.json</c>
    /// ever hear about it. That is what a headless drag probe reported, and it is not
    /// something a screenshot can show.
    /// </para>
    /// </summary>
    public Action<double>? SidebarWidthChanged { get; set; }

    partial void OnSidebarWidthChanged(double value) => SidebarWidthChanged?.Invoke(value);

    /// <summary>
    /// The list's CPU/Memory columns (number + sparkline). One condition now: the
    /// cluster has to actually serve metrics.k8s.io for the metered kind
    /// (<see cref="AreMetricsVisible"/>). It used to also require the advanced view,
    /// which meant the cluster's own usage numbers — the thing people open a resource
    /// list to see — were off until a switch about sidebar clutter was found. Read by
    /// <see cref="Views.ClusterTabView"/>'s code-behind: a DataGridColumn is outside
    /// the visual tree and can't bind.
    /// </summary>
    public bool AreUsageColumnsVisible => AreMetricsVisible;

    // ------------------------------------------------- CRD printer columns
    //
    // A CustomResourceDefinition declares the columns it wants a list of its objects to
    // have, and kubectl honours them — `kubectl get certificates` prints cert-manager's
    // READY / SECRET / ISSUER, not a generic status. This app printed the same generic
    // Status column for all ~70 CRD kinds on a real cluster, which is the weakest
    // surface in a client that sells CRDs as first-class. Built-in kinds are untouched:
    // they are not CRDs, so there is nothing to read for them and ResourceStatusSummary
    // still owns every column they show.

    /// <summary>
    /// Everything the selected kind's CRD declares, unfiltered — the advanced-view and
    /// width decisions are made by <see cref="VisiblePrinterColumns"/>, so that a
    /// display switch never has to refetch. Empty for a built-in kind, for an
    /// aggregated API, for a CRD that declares nothing, and for a user who cannot read
    /// <c>apiextensions.k8s.io</c>; all four then render exactly the list they did
    /// before this existed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisiblePrinterColumns))]
    private IReadOnlyList<PrinterColumn> _printerColumns = [];

    /// <summary>
    /// The columns the grid actually draws: every column the CRD declares, priority-1
    /// included, with a declared Age folded into the list's own live Age column and the
    /// whole thing capped at the number of printer slots the grid declares. See
    /// <see cref="PrinterColumns.Visible"/>.
    ///
    /// <para>
    /// The priority-1 half used to ride the advanced view as this app's <c>-o wide</c>.
    /// That switch is the sidebar's alone now, and a column a CRD's author declared is
    /// exactly the content-area detail it may no longer withhold; a list wider than the
    /// window is the reader's to re-cut, since every column is draggable (FEAT-66).
    /// </para>
    /// </summary>
    public IReadOnlyList<PrinterColumn> VisiblePrinterColumns =>
        // Every declared column, priority included. The advanced view used to act as
        // kubectl's `-o wide` here; it governs the sidebar only now, and a column the
        // CRD's author declared is exactly the kind of content-area detail this switch
        // is no longer allowed to withhold. A list wider than the window is the reader's
        // to re-cut — every column is draggable (FEAT-66).
        KubeNimbus.Core.PrinterColumns.Visible(PrinterColumns, includeLowPriority: true, ResourceRowViewModel.PrinterCellCount);

    partial void OnPrinterColumnsChanged(IReadOnlyList<PrinterColumn> value) => PushPrinterColumnsToRows();

    /// <summary>
    /// Per-kind cache, for this tab's lifetime. Keyed by group/version/kind because a
    /// CRD serves different columns per version, and a cluster can be upgraded under a
    /// running tab. The negative answer is cached too — a built-in kind must not cost a
    /// 404 every time it is reselected.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<PrinterColumn>> _printerColumnCache = new(StringComparer.Ordinal);

    private static string PrinterCacheKey(ResourceDescriptor descriptor) =>
        $"{descriptor.Group}/{descriptor.Version}/{descriptor.Kind}";

    /// <summary>
    /// Points the list at whatever printer columns the newly-selected kind has. The
    /// cached (or demo) answer lands synchronously so the grid is never briefly wrong;
    /// a cache miss on a live cluster clears the columns first and fills them in when
    /// the GET returns, which is one small request the first time a kind is opened.
    /// </summary>
    private void UpdatePrinterColumns(ResourceDescriptor descriptor)
    {
        if (Client is null)
        {
            // Demo cluster (or a tab with no client at all): the dataset answers for
            // its own CRD, through the same PrinterColumns.Parse a live cluster uses.
            PrinterColumns = IsDemo ? Demo.DemoData.PrinterColumnsFor(descriptor) : [];
            return;
        }

        var key = PrinterCacheKey(descriptor);
        if (_printerColumnCache.TryGetValue(key, out var cached))
        {
            PrinterColumns = cached;
            return;
        }

        PrinterColumns = [];

        var client = Client;
        var token = _watchCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var columns = await client.GetPrinterColumnsAsync(descriptor, token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _printerColumnCache[key] = columns;

                    // The user can have moved on while this was in flight; the columns
                    // belong to the kind that asked for them and to no other.
                    if (SelectedKind?.Descriptor is { } current && PrinterCacheKey(current) == key)
                    {
                        PrinterColumns = columns;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Normal: the kind or namespace changed while this was in flight. Not
                // cached either — the next selection of this kind should ask again.
            }
        }, token);
    }

    /// <summary>
    /// Hands the current column set to every row. Rows are created one at a time by the
    /// watch, so they take the set on creation too; this is for the two moments the set
    /// itself changes — the fetch landing, and the advanced-view switch.
    /// </summary>
    private void PushPrinterColumnsToRows()
    {
        var columns = VisiblePrinterColumns;
        foreach (var row in Rows)
        {
            row.SetPrinterColumns(columns);
        }

        // A sort by one of those columns has to be re-run against the new set: the
        // cells it orders by have just been re-evaluated, and the column may have
        // stopped being declared at all — in which case the list falls back to arrival
        // order rather than pretending to be sorted by something that is not there.
        if (SortColumnId is { } columnId && ResourceColumn.PrinterName(columnId) is not null)
        {
            RebuildVisibleRows();
        }
    }

    /// <summary>
    /// True while the Helm entry is selected: the content area swaps the generic
    /// resource list for the release browser. Helm releases aren't an API kind,
    /// so there's nothing to watch — they're read from their storage Secrets.
    /// </summary>
    [ObservableProperty]
    private bool _isHelmView;

    /// <summary>
    /// Supplies every connected cluster for the aggregated (fleet) view — set by
    /// <see cref="MainWindowViewModel"/>, which owns the tab list. A tab doesn't
    /// know about its siblings otherwise, and shouldn't.
    /// </summary>
    public Func<IReadOnlyList<FleetMember>>? FleetMembersProvider { get; set; }

    /// <summary>
    /// True when there is more than one connected cluster, i.e. when aggregating
    /// would actually show something a single tab doesn't. The toggle stays out of
    /// the way entirely otherwise (UI rule 1) — a "fleet" of one is just this tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFleetToggleVisible))]
    private bool _isFleetViewAvailable;

    /// <summary>
    /// True while the list aggregates the selected kind across every connected
    /// cluster instead of just this tab's. The sidebar, namespace picker, filter and
    /// inspector are unchanged — only the source of rows and one extra column differ,
    /// which is why this is a toggle on the existing list rather than a new view.
    /// </summary>
    [ObservableProperty]
    private bool _isFleetView;

    /// <summary>
    /// Hidden for Helm (releases aren't an API kind, so there's nothing to fan out)
    /// and for the Argo dashboard. It used to also require the advanced view, with a
    /// carve-out so that turning the switch off could not strand a tab in fleet mode
    /// with no control to leave it; the switch is the sidebar's alone now, so both the
    /// gate and the carve-out are gone and the toggle appears exactly when aggregating
    /// would show something a single tab does not.
    /// </summary>
    public bool IsFleetToggleVisible =>
        IsFleetViewAvailable && !IsHelmView && !IsArgoView;

    partial void OnIsHelmViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFleetToggleVisible));
        OnPropertyChanged(nameof(IsResourceListVisible));
    }

    /// <summary>
    /// Whether the generic resource list is the thing in the content area. Two browsers can
    /// replace it — Helm and the Argo dashboard — and a single computed property is what
    /// keeps that from being a pair of negated bindings on every element in the list's half
    /// of the view, which is the shape that silently gets one of the two wrong when a third
    /// is added.
    /// </summary>
    public bool IsResourceListVisible => !IsHelmView && !IsArgoView;

    /// <summary>
    /// True while the Argo CD dashboard is selected: the content area swaps the generic
    /// resource list for every Application on the cluster, counted and ordered by what needs
    /// attention. Applications <em>are</em> an API kind, so unlike the Helm browser this is
    /// not standing in for something that cannot be watched — it is a different question
    /// about the same objects, and the ordinary Applications list is still one row below it
    /// in the sidebar.
    /// </summary>
    [ObservableProperty]
    private bool _isArgoView;

    partial void OnIsArgoViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFleetToggleVisible));
        OnPropertyChanged(nameof(IsResourceListVisible));
    }

    /// <summary>
    /// "4 of 5 clusters · payments" — how many clusters are actually behind the rows
    /// on screen. A partial fleet is the normal state (a kind can be missing from a
    /// cluster, a cluster can be unreachable), so the count is always stated rather
    /// than left for the user to infer from the rows.
    /// </summary>
    [ObservableProperty]
    private string? _fleetSummary;

    partial void OnIsFleetViewChanged(bool value)
    {
        // The toggle's own visibility depends on this (see IsFleetToggleVisible) —
        // it has to survive the advanced view being switched off mid-aggregation.
        OnPropertyChanged(nameof(IsFleetToggleVisible));
        FleetSummary = null;
        RestartWatch();
    }

    /// <summary>
    /// Re-fans the aggregated watch after a cluster tab is opened or closed. Called by
    /// <see cref="MainWindowViewModel"/>; a no-op unless this tab is aggregating, since
    /// otherwise its own watch is unaffected by what the other tabs are doing.
    /// </summary>
    public void RefreshFleetMembership()
    {
        if (IsFleetView)
        {
            RestartWatch();
        }
    }

    public ObservableCollection<HelmReleaseRowViewModel> HelmReleases { get; } = [];

    [ObservableProperty]
    private HelmReleaseRowViewModel? _selectedHelmRelease;

    [ObservableProperty]
    private bool _isHelmLoading;

    [ObservableProperty]
    private bool _isHelmEmpty;

    // ------------------------------------------------------------ Argo CD dashboard
    //
    // Every Application on the cluster, with the sync and health Argo itself reports, the
    // seven counts across all of them, and the two actions Argo's own UI leads with. It is
    // read from the Kubernetes API and nothing else — see ClusterClient.ArgoCd.cs.

    public ObservableCollection<ArgoApplicationRowViewModel> ArgoApplications { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncSelectedArgoApplication))]
    [NotifyCanExecuteChangedFor(nameof(SyncArgoApplicationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshArgoApplicationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenArgoApplicationCommand))]
    private ArgoApplicationRowViewModel? _selectedArgoApplication;

    [ObservableProperty]
    private bool _isArgoLoading;

    [ObservableProperty]
    private bool _isArgoEmpty;

    /// <summary>
    /// The seven counts across every Application on the cluster — the numbers Argo CD's own
    /// dashboard opens on. Null until the first read completes, which is what keeps the
    /// summary strip off screen rather than showing seven zeros while the list loads.
    /// </summary>
    /// <remarks>
    /// Named for the numbers rather than for the type it holds, because a property called
    /// <c>ArgoSummary</c> would shadow <see cref="Core.ArgoSummary"/> inside this class and
    /// make <c>ArgoSummary.Of(…)</c> stop compiling.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArgoAttentionSummary))]
    private ArgoSummary? _argoCounts;

    /// <summary>
    /// "2 need attention" — or the all-clear. Stated rather than left to be counted off the
    /// pills, because "is anything wrong right now" is the single question this whole view
    /// exists to answer, and on a cluster with sixty Applications it is not answerable by
    /// looking.
    /// </summary>
    public string ArgoAttentionSummary
    {
        get
        {
            if (ArgoCounts is not { } summary)
            {
                return "";
            }

            if (summary.Total == 0)
            {
                return "";
            }

            var attention = ArgoApplications.Count(a => a.NeedsAttention);
            return attention == 0
                ? $"All {summary.Total} Applications are synced and healthy."
                : $"{attention} of {summary.Total} need attention — degraded, missing or out of sync.";
        }
    }

    public ObservableCollection<InspectorTabViewModelBase> InspectorTabs { get; } = [];

    [ObservableProperty]
    private InspectorTabViewModelBase? _selectedInspectorTab;

    partial void OnSelectedInspectorTabChanged(InspectorTabViewModelBase? oldValue, InspectorTabViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
        }
    }

    /// <summary>Expands the inspector to fill the content area (list hidden) — the
    /// fixed ~440px sidecar is too cramped for YAML editing or an exec terminal.</summary>
    [ObservableProperty]
    private bool _isInspectorMaximized;

    [RelayCommand]
    private void ToggleInspectorMaximized() => IsInspectorMaximized = !IsInspectorMaximized;

    public ClusterTabViewModel(ClusterContext context)
    {
        Context = context;

        // The list renders VisibleRows; everything that produces rows writes to Rows.
        // Subscribing here rather than filtering at each producer is what keeps the
        // watch, the fleet merge, the demo dataset and the screenshot fixtures all
        // unaware that a filter exists.
        Rows.CollectionChanged += OnRowsChanged;
    }

    private bool CanConnect => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsDemo)
        {
            ConnectDemo();
            return;
        }

        IsConnecting = true;
        ConnectionWarning = null;
        Status = $"Connecting to {Context.Name}…";
        try
        {
            var client = ClusterClient.Connect(Context);
            var version = await client.GetServerVersionAsync();
            Client = client;
            IsConnected = true;
            Status = $"Connected — Kubernetes {version.GitVersion}.";

            await BuildSidebarAsync();
            await RefreshNamespacesAsync();
            await DetectMetricsApiAsync();

            var defaultKind = SidebarSections
                .FirstOrDefault(s => s.Title == "Workloads")?.Kinds
                .FirstOrDefault(k => k.Descriptor.Kind == "Pod")
                ?? SidebarSections.SelectMany(s => s.Kinds).FirstOrDefault();
            if (defaultKind is not null)
            {
                SelectKind(defaultKind);
            }
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// The demo counterpart of <see cref="ConnectAsync"/>. It fills in for discovery,
    /// the namespace list and the metrics probe from the shipped dataset, and
    /// deliberately leaves <see cref="Client"/> null — everything downstream branches
    /// on that, so nothing here can accidentally acquire a connection later.
    ///
    /// Synchronous, because none of it waits on anything: the "connecting…" state
    /// exists to explain a round trip, and there isn't one.
    /// </summary>
    private void ConnectDemo()
    {
        IsConnected = true;
        Status = DemoStatus;

        var catalog = Demo.DemoData.BuildCatalog();
        RecordPodDescriptor("", catalog);
        SidebarSections.Clear();
        _recentKinds.Clear();
        foreach (var section in Demo.DemoData.BuildSidebarSections(catalog))
        {
            SidebarSections.Add(section);
        }

        // The demo cluster stores Helm releases, so the section appears — same
        // condition AddHelmSectionIfPresentAsync applies to a real cluster.
        var helm = new SidebarSectionViewModel(SidebarGrouping.HelmSection);
        helm.Kinds.Add(new SidebarKindViewModel(
            SidebarGrouping.HelmReleaseDescriptor, SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection)));
        SidebarSections.Add(helm);

        SidebarGrouping.LabelAmbiguousKinds(SidebarSections);
        ApplySidebarChrome();
        ApplySidebarFilter();

        NamespaceOptions.Clear();
        NamespaceOptions.Add(AllNamespaces);
        foreach (var ns in Demo.DemoData.Namespaces)
        {
            NamespaceOptions.Add(ns);
        }

        // Set before the kind, so the single RestartWatch that SelectKind triggers is
        // the one that populates the rows — assigning it afterwards would clear them
        // and latch IsListEmpty, the ordering gotcha CLAUDE.md documents for the
        // screenshot fixtures.
        SelectedNamespace = "payments";

        _metricsApiAvailable = true;

        var defaultKind = SidebarSections
            .FirstOrDefault(s => s.Title == "Workloads")?.Kinds
            .FirstOrDefault(k => k.Descriptor.Kind == "Pod")
            ?? SidebarSections.SelectMany(s => s.Kinds).FirstOrDefault();
        if (defaultKind is not null)
        {
            SelectKind(defaultKind);
        }
    }

    private async Task BuildSidebarAsync()
    {
        if (Client is null)
        {
            return;
        }

        var catalog = await Client.GetResourceCatalogAsync();
        RecordPodDescriptor("", catalog);
        var sections = new Dictionary<string, SidebarSectionViewModel>(StringComparer.Ordinal);
        foreach (var title in SidebarGrouping.SectionOrder)
        {
            sections[title] = new SidebarSectionViewModel(title);
        }

        foreach (var descriptor in catalog.OrderBy(d => d.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var title = SidebarGrouping.SectionFor(descriptor);
            sections[title].Kinds.Add(new SidebarKindViewModel(descriptor, SidebarGrouping.IconKeyFor(descriptor, title)));
        }

        SidebarSections.Clear();

        // The Recent entries hold descriptor instances from the catalog being replaced,
        // so a reconnect starts the history over rather than pointing at stale ones.
        _recentKinds.Clear();

        foreach (var title in SidebarGrouping.SectionOrder)
        {
            if (sections[title].Kinds.Count > 0)
            {
                SidebarSections.Add(sections[title]);
            }
        }

        SidebarGrouping.LabelAmbiguousKinds(SidebarSections);
        SidebarGrouping.AddArgoDashboard(SidebarSections, catalog);
        await AddHelmSectionIfPresentAsync();
        ApplySidebarChrome();
        ApplySidebarFilter();
    }

    /// <summary>
    /// Re-derives the per-section display state that comes from the tab rather than
    /// from discovery — today just the kind-count badge. Called wherever the set of
    /// sections changes (sidebar rebuild, Recent rebuild) and when the advanced view
    /// is toggled, because a freshly constructed section defaults to the plain layout.
    /// </summary>
    internal void ApplySidebarChrome()
    {
        // A filter is a deliberate search for one thing, so it reaches into the
        // sections the advanced view hides: a query that matches a kind and then shows
        // nothing is the "worse than no match" failure the palette's own rules name.
        var filtering = (SidebarFilter ?? "").Trim().Length > 0;

        foreach (var section in SidebarSections)
        {
            // The badge stays on the switch. "How much is hiding in here?" is a question
            // about the catalog, so it belongs to the one control that governs the
            // catalog — unlike everything else the switch used to hide, which was in the
            // content area and had nothing to do with sidebar clutter.
            section.ShowKindCount = IsAdvancedView;

            section.IsHiddenByBasicView =
                !IsAdvancedView && !filtering && SidebarGrouping.IsAdvancedSection(section.Title);

            // Wired here rather than at construction because this runs after every
            // rebuild, and a section built during one must not record its state while
            // the list is still half-assembled.
            section.ExpansionChanged = PersistExpandedSections;
        }
    }

    /// <summary>
    /// Remembers which sidebar sections are open, so the choice survives a restart.
    /// Stores the whole set rather than a per-section flag: the list is read as "these
    /// are expanded, everything else is not", and an empty set means "nobody has said"
    /// so the built-in defaults still apply on a fresh install.
    ///
    /// <para>
    /// A filter's force-expansion is deliberately not recorded — <c>IsForceExpanded</c>
    /// is a separate property precisely so that typing in the filter box does not
    /// rewrite what someone chose to have open.
    /// </para>
    /// </summary>
    private void PersistExpandedSections()
    {
        var expanded = SidebarSections
            .Where(s => s.IsExpanded)
            .Select(s => s.Title)
            .ToList();

        App.Update(s => s with { ExpandedSidebarSections = expanded });
    }

    /// <summary>
    /// Adds the Helm section only when the cluster actually stores releases —
    /// a Helm entry on a cluster that has never seen Helm is exactly the kind of
    /// always-visible control the UI rules say to default to "no". The probe is
    /// one field-selected Secret page of one item at connect time (never a full
    /// decode); a release installed later in the session shows up on the next
    /// connect/reconnect.
    /// </summary>
    private async Task AddHelmSectionIfPresentAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            if (!await Client.HasHelmReleasesAsync())
            {
                return;
            }

            var section = new SidebarSectionViewModel(SidebarGrouping.HelmSection);
            section.Kinds.Add(new SidebarKindViewModel(
                SidebarGrouping.HelmReleaseDescriptor, SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection)));
            SidebarSections.Add(section);
        }
        catch (Exception)
        {
            // No permission to list Secrets (a perfectly normal RBAC setup) —
            // then there's no Helm browsing to offer, and that's not an error.
        }
    }

    /// <summary>
    /// How many kinds the Recent section keeps. Small on purpose: it's a shortcut back
    /// to what you're working on right now, and a long list is just the sidebar again.
    /// </summary>
    private const int MaxRecentKinds = 5;

    /// <summary>Most-recent-first, deduplicated by (group, kind). Session-scoped — not persisted.</summary>
    private readonly List<ResourceDescriptor> _recentKinds = [];

    /// <summary>
    /// Pushes a kind to the top of the Recent section. Selecting a recent entry itself
    /// is ignored: reordering the list under the pointer that just clicked it makes the
    /// section unusable.
    /// </summary>
    private void RecordRecentKind(SidebarKindViewModel kind)
    {
        if (kind.IsRecentEntry)
        {
            return;
        }

        _recentKinds.RemoveAll(d =>
            string.Equals(d.Group, kind.Descriptor.Group, StringComparison.Ordinal)
            && string.Equals(d.Kind, kind.Descriptor.Kind, StringComparison.Ordinal));
        _recentKinds.Insert(0, kind.Descriptor);
        while (_recentKinds.Count > MaxRecentKinds)
        {
            _recentKinds.RemoveAt(_recentKinds.Count - 1);
        }

        RebuildRecentSection();
    }

    /// <summary>
    /// Rebuilds the pinned Recent section from <see cref="_recentKinds"/>. The entries
    /// are second <see cref="SidebarKindViewModel"/> instances over the same descriptors
    /// — including the synthetic Helm one, whose <c>IsHelmReleases</c> check is by
    /// descriptor reference and so keeps working from a copy.
    /// </summary>
    private void RebuildRecentSection()
    {
        var section = SidebarSections.FirstOrDefault(s => s.Title == SidebarGrouping.RecentSection);
        if (section is null)
        {
            section = new SidebarSectionViewModel(SidebarGrouping.RecentSection);
            SidebarSections.Insert(0, section);
        }

        section.Kinds.Clear();
        foreach (var descriptor in _recentKinds)
        {
            var iconKey = ReferenceEquals(descriptor, SidebarGrouping.HelmReleaseDescriptor)
                ? SidebarGrouping.IconKeyFor(SidebarGrouping.HelmSection)
                : SidebarGrouping.IconKeyFor(descriptor, SidebarGrouping.SectionFor(descriptor));

            section.Kinds.Add(new SidebarKindViewModel(descriptor, iconKey)
            {
                IsRecentEntry = true,
                // Same-named kinds from different groups are exactly what this section
                // is most likely to hold two of, so always carry the group here.
                GroupLabel = descriptor.Group.Length > 0 ? descriptor.Group : "core",
            });
        }

        // A rebuild replaces the instances the filter had already classified — and,
        // the first time round, inserts a section that has never seen the tab's
        // display state.
        ApplySidebarChrome();
        ApplySidebarFilter();
    }

    partial void OnSidebarFilterChanged(string value) => ApplySidebarFilter();

    [RelayCommand]
    private void ClearSidebarFilter() => SidebarFilter = "";

    /// <summary>
    /// Filters sidebar kinds by substring match on display name, live as the user
    /// types. A section with at least one match force-expands (without touching
    /// the user's own collapse choice, restored once the filter is cleared) so
    /// filtering never hides a result inside a collapsed section.
    /// </summary>
    private void ApplySidebarFilter()
    {
        // The filter TextBox's two-way binding can round-trip null on
        // control (re)creation — same reasoning as SelectedNamespace above.
        var query = (SidebarFilter ?? "").Trim();
        var filtering = query.Length > 0;

        foreach (var section in SidebarSections)
        {
            var anyMatch = false;
            foreach (var kind in section.Kinds)
            {
                var match = !filtering || kind.Matches(query);
                kind.IsVisible = match;
                anyMatch |= match;
            }

            section.HasVisibleKinds = anyMatch;
            section.IsForceExpanded = filtering && anyMatch;

            // The filter is an input to the advanced-view gate (see ApplySidebarChrome),
            // so it is re-derived here rather than only on a rebuild — otherwise typing
            // a query would leave the hidden sections hidden and the match unreachable.
            section.IsHiddenByBasicView =
                !IsAdvancedView && !filtering && SidebarGrouping.IsAdvancedSection(section.Title);
        }
    }

    [RelayCommand]
    private async Task RefreshNamespacesAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            var namespaces = await Client.ListResourceOnceAsync(ResourceDescriptor.Namespaces);
            var previousSelection = SelectedNamespace;
            NamespaceOptions.Clear();
            NamespaceOptions.Add(AllNamespaces);
            foreach (var ns in namespaces.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                NamespaceOptions.Add(ns.Name);
            }

            // Clearing/repopulating the bound collection can round-trip the ComboBox's
            // SelectedItem through null via the two-way binding; re-assert a valid
            // selection so the selector never ends up showing blank.
            SelectedNamespace = NamespaceOptions.Contains(previousSelection) ? previousSelection : AllNamespaces;
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"Could not list namespaces: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectKind(SidebarKindViewModel kind)
    {
        if (SelectedKind == kind)
        {
            return;
        }

        RecordRecentKind(kind);

        // A name filter is a question about the list it was typed into: carrying
        // "nginx" from Pods over to ConfigMaps lands on an empty list that looks
        // like a broken watch.
        RowFilter = "";

        foreach (var section in SidebarSections)
        {
            foreach (var k in section.Kinds)
            {
                k.IsSelected = k == kind;
            }
        }

        SelectedKind = kind;

        if (kind.IsHelmReleases)
        {
            StopWatch();
            IsHelmView = true;
            IsArgoView = false;
            AreMetricsVisible = false;
            PrinterColumns = []; // Helm releases are not an API kind and have no CRD behind them.
            _ = RefreshHelmReleasesAsync();
            return;
        }

        if (kind.IsArgoDashboard)
        {
            StopWatch();
            IsHelmView = false;
            IsArgoView = true;
            AreMetricsVisible = false;

            // The dashboard's columns are its own — Argo's sync and health, not a CRD's
            // printer columns — so the printer slots are cleared exactly as the Helm
            // browser clears them.
            PrinterColumns = [];
            _ = ReloadArgoAsync();
            return;
        }

        IsHelmView = false;
        IsArgoView = false;
        RestartWatch();
    }

    /// <summary>
    /// The Application kind's descriptor on this cluster, or null when Argo CD is not
    /// installed. Read from the discovery catalog the sidebar was built from, so the version
    /// is whatever this server serves.
    /// </summary>
    private ResourceDescriptor? ArgoApplicationDescriptor()
    {
        var catalog = SidebarSections
            .SelectMany(s => s.Kinds)
            .Select(k => k.Descriptor);

        return ArgoCd.ApplicationDescriptor(catalog);
    }

    /// <summary>
    /// Reloads the Argo dashboard. Cluster-wide rather than namespace-scoped, deliberately:
    /// Applications almost always live in one namespace (<c>argocd</c>) while the workloads
    /// they manage are spread across the rest, so a dashboard that followed the namespace
    /// picker would be empty everywhere except the one place nobody browses.
    /// </summary>
    [RelayCommand]
    private async Task ReloadArgoAsync()
    {
        if (Client is null && !IsDemo)
        {
            return;
        }

        if (ArgoApplicationDescriptor() is not { } descriptor)
        {
            IsArgoEmpty = true;
            return;
        }

        IsArgoLoading = true;
        IsArgoEmpty = false;
        var previous = SelectedArgoApplication?.Application.Key;
        ArgoApplications.Clear();
        try
        {
            var applications = Client is null
                ? Demo.DemoData.ArgoApplications
                : await Client.ListArgoApplicationsAsync(descriptor);

            // No cluster name: the dashboard is this tab's own cluster. The fleet toggle is
            // hidden here for the same reason it is hidden in the Helm browser — aggregating
            // GitOps state across clusters is a different (and much bigger) question than
            // aggregating one kind's rows.
            foreach (var row in applications
                .Select(a => new ArgoApplicationRowViewModel(a, descriptor))
                .OrderBy(ArgoApplicationRowViewModel.Rank)
                .ThenBy(r => r.Application.Key, StringComparer.Ordinal))
            {
                ArgoApplications.Add(row);
            }

            ArgoCounts = ArgoSummary.Of(applications);

            // A refresh must not move the selection: somebody reading one Application's
            // detail while the list reloads has not asked to be moved to the worst one.
            SelectedArgoApplication =
                ArgoApplications.FirstOrDefault(a => a.Application.Key == previous)
                ?? ArgoApplications.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"Could not read Argo CD Applications: {ex.Message}";
        }
        finally
        {
            IsArgoLoading = false;
            IsArgoEmpty = ArgoApplications.Count == 0;
            OnPropertyChanged(nameof(ArgoAttentionSummary));
        }
    }

    private bool HasSelectedArgoApplication => SelectedArgoApplication is not null;

    /// <summary>Double-click / Enter on an Application row: opens its resources, conditions and history.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedArgoApplication))]
    private void OpenArgoApplication()
    {
        if (SelectedArgoApplication is not { } row || (Client is null && !IsDemo))
        {
            return;
        }

        var key = ArgoApplicationTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(
            new ArgoApplicationTabViewModel(
                Client,
                row.Descriptor,
                row.Application,
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName),
                row.ClusterName),
            replacePreview: false);
    }

    /// <summary>
    /// The Application a sync or refresh would act on: the dashboard's selected row while the
    /// dashboard is showing, and otherwise the selected row of an ordinary Applications list.
    /// Two entry points to one action, because the ordinary list is still there and a menu
    /// item that worked in one of them and not the other would be the harder thing to explain.
    /// </summary>
    private (ClusterClient? Client, ResourceDescriptor Descriptor, string Namespace, string Name, string Cluster)?
        ArgoActionTarget()
    {
        if (IsArgoView)
        {
            return SelectedArgoApplication is { } row
                ? (ClientForCluster(row.ClusterName), row.Descriptor, row.Namespace, row.Name, row.ClusterName)
                : null;
        }

        if (SelectedRow is { } selected
            && DescriptorFor(selected) is { } descriptor
            && ArgoCd.SupportsSync(descriptor))
        {
            return (ClientFor(selected), descriptor, selected.Namespace, selected.Name, selected.ClusterName);
        }

        return null;
    }

    /// <summary>
    /// Whether a sync or refresh is offered at all. The kind is named inside
    /// <see cref="ArgoCd.SupportsSync"/>, and that is the honest exception argued there:
    /// <c>operation</c> is a field of Argo's own schema, so neither discovery nor the object
    /// can answer this the way a <c>scale</c> subresource or a pod template can.
    /// </summary>
    public bool CanSyncSelectedArgoApplication => ArgoActionTarget() is not null;

    /// <summary>
    /// "argocd/checkout" — what a sync or refresh would act on, for the palette's subtitle.
    /// Null when neither surface has an Application selected, which is the same condition
    /// <see cref="CanSyncSelectedArgoApplication"/> reports and is why the palette can gate
    /// on either one.
    /// </summary>
    public string? ArgoActionLabel =>
        ArgoActionTarget() is { } target ? $"{target.Namespace}/{target.Name}" : null;

    [RelayCommand(CanExecute = nameof(CanSyncSelectedArgoApplication))]
    private void SyncArgoApplication() => ArmArgoAction(RowActionKind.ArgoSync);

    [RelayCommand(CanExecute = nameof(CanSyncSelectedArgoApplication))]
    private void RefreshArgoApplication() => ArmArgoAction(RowActionKind.ArgoRefresh);

    private void ArmArgoAction(RowActionKind kind)
    {
        if (ArgoActionTarget() is not { } target)
        {
            return;
        }

        // Null only on the demo cluster, where the strip renders its refusal in place rather
        // than the action silently doing nothing (demo rule 5).
        if (target.Client is null && !IsDemo)
        {
            return;
        }

        if (PendingRowAction is { IsBusy: true } or { IsDraining: true })
        {
            return;
        }

        var action = new RowActionViewModel(
            kind, target.Client, target.Descriptor, target.Namespace, target.Name, target.Cluster);

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingRowAction, action))
            {
                PendingRowAction = null;
            }
        };

        PendingRowAction = action;
    }

    /// <summary>Reloads the Helm release list for the selected namespace.</summary>
    [RelayCommand]
    private async Task RefreshHelmReleasesAsync()
    {
        if (Client is null && !IsDemo)
        {
            return;
        }

        IsHelmLoading = true;
        IsHelmEmpty = false;
        HelmReleases.Clear();
        try
        {
            var @namespace = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;
            var releases = Client is null
                ? Demo.DemoData.HelmReleases.Where(r => @namespace is null || r.Namespace == @namespace)
                : await Client.ListHelmReleasesAsync(@namespace);
            foreach (var release in releases)
            {
                HelmReleases.Add(new HelmReleaseRowViewModel(release));
            }

            SelectedHelmRelease = HelmReleases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"Could not read Helm releases: {ex.Message}";
        }
        finally
        {
            IsHelmLoading = false;
            IsHelmEmpty = HelmReleases.Count == 0;
        }
    }

    /// <summary>Double-click / Enter on a release row: opens its values/manifest/notes/history tab.</summary>
    [RelayCommand]
    private void OpenSelectedHelmRelease()
    {
        if (SelectedHelmRelease is not { } row || (Client is null && !IsDemo))
        {
            return;
        }

        var key = $"helm:{row.Namespace}/{row.Name}";
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new HelmReleaseTabViewModel(Client, row.Release), replacePreview: false);
    }

    /// <summary>
    /// Owner-chip and event navigation on the demo cluster. There is no
    /// <c>ResolveOwnerAsync</c> to call, so the target is looked up in the dataset by
    /// kind and name. A reference the dataset doesn't carry says so in the same inline
    /// warning a deleted owner gets on a real cluster — the demo is a sample, not a
    /// complete cluster, and pretending otherwise would be the one place it lies.
    /// </summary>
    private void OpenDemoOwner(OwnerRef owner, string? namespaceHint)
    {
        var catalog = Demo.DemoData.BuildCatalog();
        var descriptor = catalog.FirstOrDefault(d => d.ApiVersion == owner.ApiVersion && d.Kind == owner.Kind);
        var resolved = descriptor is null
            ? null
            : Demo.DemoData.ResourcesFor(descriptor, namespaceHint)
                .FirstOrDefault(r => string.Equals(r.Name, owner.Name, StringComparison.Ordinal));

        if (descriptor is null || resolved is null)
        {
            ConnectionWarning = $"{owner.Kind}/{owner.Name} isn't part of the demo dataset.";
            return;
        }

        var key = YamlEditorTabViewModel.KeyFor("", descriptor, resolved.Namespace, resolved.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } open)
        {
            open.IsPreview = false;
            SelectedInspectorTab = open;
            return;
        }

        AddInspectorTab(
            new YamlEditorTabViewModel(null, descriptor, resolved.Namespace, resolved.Name, resolved.ToYaml()),
            replacePreview: false);
    }

    /// <summary>
    /// Plenty of clusters run without metrics-server. Probing once at connect
    /// (off the cached discovery catalog) keeps the usage columns out of the way
    /// entirely on those clusters instead of showing a column full of dashes.
    /// </summary>
    private async Task DetectMetricsApiAsync()
    {
        try
        {
            _metricsApiAvailable = Client is not null && await Client.IsMetricsApiAvailableAsync();
        }
        catch (Exception)
        {
            _metricsApiAvailable = false; // usage is supplementary; never fail the connect over it
        }
    }

    partial void OnSelectedNamespaceChanged(string value)
    {
        if (IsHelmView)
        {
            _ = RefreshHelmReleasesAsync();
        }
        else if (IsArgoView)
        {
            // Deliberately nothing. The Argo dashboard is cluster-wide (see ReloadArgoAsync),
            // so the namespace picker does not narrow it — and re-reading the same list on
            // every namespace change would be work nobody asked for.
        }
        else
        {
            RestartWatch();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsHelmView)
        {
            _ = RefreshHelmReleasesAsync();
        }
        else if (IsArgoView)
        {
            _ = ReloadArgoAsync();
        }
        else
        {
            RestartWatch();
        }
    }

    /// <summary>Cancels the current list watch (and the metrics poll riding on its token).</summary>
    private void StopWatch()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    private void RestartWatch()
    {
        StopWatch();

        // An armed scale/restart/delete is a question about the list that is being torn
        // down here (kind switched, namespace switched, fleet toggled). Its target row
        // is about to leave the screen, so the strip goes with it rather than lingering
        // over a list it no longer belongs to. A *completed* action's result strip is
        // dismissed the same way, which is correct: the answer was already read.
        PendingRowAction = null;

        Rows.Clear();
        _rowsByKey.Clear();
        _fleetTargets.Clear();
        IsListLoading = (Client is not null || IsDemo) && SelectedKind is not null;
        RecomputeListEmpty();

        if ((Client is null && !IsDemo) || SelectedKind is null)
        {
            AreMetricsVisible = false;
            PrinterColumns = [];
            return;
        }

        var descriptor = SelectedKind.Descriptor;
        var @namespace = descriptor.Namespaced && SelectedNamespace != AllNamespaces ? SelectedNamespace : null;

        if (Client is not { } client)
        {
            UpdatePrinterColumns(descriptor);

            // Only reachable on the demo cluster — the guard above returned for every
            // other tab with no client. Rows come from the shipped dataset; no watch,
            // no metrics poll, no socket.
            PopulateDemoRows(descriptor, @namespace);
            return;
        }

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;

        // Fleet mode uses *this* tab's descriptor for the columns, deliberately. The
        // headers can only be one set, so they come from the cluster whose sidebar the
        // kind was selected in; every row is then evaluated against those same JSON
        // paths whatever cluster served it. A member serving an older version with a
        // different shape resolves to blank cells rather than to a wrong value, which
        // is the same outcome a missing field already has, and the alternative —
        // per-cluster headers — is not a thing a single table can render.
        UpdatePrinterColumns(descriptor);

        if (IsFleetView && FleetMembersProvider?.Invoke() is { Count: > 0 } members)
        {
            // In fleet mode metrics availability is per cluster and unknown up front,
            // so the columns go on for a metered kind and the poll takes them away
            // again if no cluster in scope actually serves metrics.k8s.io.
            AreMetricsVisible = IsMeteredKind(descriptor);
            StartFleetWatch(descriptor, members, @namespace, token);
            return;
        }

        AreMetricsVisible = _metricsApiAvailable && IsMeteredKind(descriptor);

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                    descriptor, @namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() => ConnectionWarning = ex.Message),
                    cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Apply(evt));
                }
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace or disconnecting
            }
            catch (Exception ex)
            {
                // A watch that ended is not a watch that is still loading. Leaving the
                // spinner up here would turn a reported failure into a window that looks
                // busy forever, which is the same lie the empty-list state used to tell
                // in the other direction (UI rule 18).
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Watch ended: {ex.Message}";
                    IsListLoading = false;
                    RecomputeListEmpty();
                });
            }
        }, token);

        StartMetricsPolling(descriptor, [("", client)], @namespace, token);
    }

    /// <summary>
    /// The demo counterpart of a list+watch: rows straight from the shipped dataset,
    /// no client and no watch. A kind the dataset has nothing for comes back empty and
    /// lands on the list's real "No &lt;kind&gt; found" state — most of a 100-kind
    /// catalog is like that, and it has to read as an empty namespace rather than as
    /// something broken (UI rule 9).
    /// </summary>
    private void PopulateDemoRows(ResourceDescriptor descriptor, string? @namespace)
    {
        var printerColumns = VisiblePrinterColumns;
        foreach (var resource in Demo.DemoData.ResourcesFor(descriptor, @namespace))
        {
            var row = new ResourceRowViewModel(resource);
            row.SetPrinterColumns(printerColumns);
            _rowsByKey[resource.Key] = row;
            Rows.Add(row);
        }

        AreMetricsVisible = IsMeteredKind(descriptor);
        if (AreMetricsVisible)
        {
            // Through the real ApplyUsage, one stamped sample per simulated poll —
            // metrics.k8s.io has no history endpoint, so this is the only honest way to
            // give the sparklines a shape without a second charting code path.
            Demo.DemoUsage.SeedRows(Rows);
        }

        IsListLoading = false;
        RecomputeListEmpty();
    }

    /// <summary>
    /// Fleet mode: resolve the selected kind against every connected cluster's own
    /// discovery, then run one list+watch per cluster merged into this list.
    /// </summary>
    private void StartFleetWatch(
        ResourceDescriptor descriptor, IReadOnlyList<FleetMember> members, string? @namespace, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var targets = await ClusterFleet.ResolveAsync(
                    members, descriptor.Group, descriptor.Kind,
                    memberUnavailable: (member, ex) => Dispatcher.UIThread.Post(
                        () => ConnectionWarning = $"{member.ClusterName}: {ex.Message}"),
                    cancellationToken: token);

                // The Pod descriptor of each member, for the same reason the target
                // descriptor is per member: whether a node there can be drained is that
                // server's answer, not this tab's. The catalogs are already cached on
                // each client, so this costs nothing after the first list.
                var memberPodCatalogs = new List<(string ClusterName, IReadOnlyList<ResourceDescriptor> Catalog)>();
                foreach (var target in targets)
                {
                    memberPodCatalogs.Add((
                        target.Member.ClusterName,
                        await target.Member.Client.GetResourceCatalogAsync(token)));
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _fleetTargets.Clear();
                    foreach (var target in targets)
                    {
                        _fleetTargets[target.Member.ClusterName] = target;
                    }

                    foreach (var (clusterName, catalog) in memberPodCatalogs)
                    {
                        RecordPodDescriptor(clusterName, catalog);
                    }

                    FleetSummary = $"{targets.Count} of {members.Count} clusters serve {descriptor.Kind}";
                    if (targets.Count == 0)
                    {
                        IsListLoading = false;
                        RecomputeListEmpty();
                    }
                });

                if (targets.Count == 0)
                {
                    return;
                }

                StartMetricsPolling(
                    descriptor,
                    targets.Select(t => (t.Member.ClusterName, t.Member.Client)).ToArray(),
                    @namespace,
                    token);

                await foreach (var evt in ClusterFleet.WatchAsync(
                    targets, @namespace,
                    connectionLost: (member, ex) => Dispatcher.UIThread.Post(
                        () => ConnectionWarning = $"{member.ClusterName}: {ex.Message}"),
                    cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyFleet(evt));
                }
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace, leaving fleet mode, or disconnecting
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Fleet watch ended: {ex.Message}";
                    IsListLoading = false;
                    RecomputeListEmpty();
                });
            }
        }, token);
    }

    /// <summary>Kinds metrics.k8s.io reports on. Everything else has no usage to show.</summary>
    private static bool IsMeteredKind(ResourceDescriptor descriptor) =>
        string.IsNullOrEmpty(descriptor.Group) && descriptor.Kind is "Pod" or "Node";

    /// <summary>The client that owns a row — its own cluster's in fleet mode, this tab's otherwise.</summary>
    private ClusterClient? ClientFor(ResourceRowViewModel row) => ClientForCluster(row.ClusterName);

    private ClusterClient? ClientForCluster(string clusterName) =>
        clusterName.Length > 0 && _fleetTargets.TryGetValue(clusterName, out var target)
            ? target.Member.Client
            : Client;

    /// <summary>Remembers a cluster's core/v1 Pod descriptor, verbs and subresources included.</summary>
    private void RecordPodDescriptor(string clusterName, IReadOnlyList<ResourceDescriptor> catalog)
    {
        if (catalog.FirstOrDefault(d => d is { Group: "", Kind: "Pod" }) is { } pods)
        {
            _podDescriptors[clusterName] = pods;
        }
    }

    /// <summary>
    /// The Pod descriptor of the cluster a row came from — the tab's own outside fleet
    /// mode. Null until that cluster's discovery has been read, which the drain
    /// capability check correctly reads as "not offered yet" rather than as "cannot".
    /// </summary>
    private ResourceDescriptor? PodDescriptorFor(ResourceRowViewModel row) =>
        _podDescriptors.TryGetValue(row.ClusterName, out var pods)
            ? pods
            : _podDescriptors.GetValueOrDefault("");

    /// <summary>
    /// The descriptor to use for a row. Resolved per cluster in fleet mode: the same
    /// CRD kind can be served at different versions on different clusters, and this
    /// descriptor is what an apply/delete builds its path from.
    /// </summary>
    private ResourceDescriptor? DescriptorFor(ResourceRowViewModel row) =>
        row.ClusterName.Length > 0 && _fleetTargets.TryGetValue(row.ClusterName, out var target)
            ? target.Descriptor
            : SelectedKind?.Descriptor;

    /// <summary>
    /// Polls usage for the visible list alongside its watch, on the same
    /// cancellation token — switching kind or namespace tears both down together.
    /// The metrics API has no watch endpoint (it's a point-in-time aggregate),
    /// so this is the one place the app polls rather than streams.
    /// </summary>
    private void StartMetricsPolling(
        ResourceDescriptor descriptor,
        IReadOnlyList<(string ClusterName, ClusterClient Client)> sources,
        string? @namespace,
        CancellationToken token)
    {
        if (!AreMetricsVisible || sources.Count == 0)
        {
            return;
        }

        var pods = descriptor.Kind == "Pod";

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(MetricsPollInterval);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var byKey = new Dictionary<string, (long? Cpu, long? Memory)>(StringComparer.Ordinal);
                    var unavailable = 0;

                    // One request per cluster in scope (exactly one outside fleet mode).
                    // Keys are cluster-qualified the same way the rows are, so a pod
                    // with the same namespace/name on two clusters stays two rows.
                    foreach (var (clusterName, client) in sources)
                    {
                        try
                        {
                            if (pods)
                            {
                                foreach (var m in await client.GetPodMetricsAsync(@namespace, token))
                                {
                                    byKey[ResourceRowViewModel.KeyFor(clusterName, m.Key)] = (m.CpuNanocores, m.MemoryBytes);
                                }
                            }
                            else
                            {
                                foreach (var m in await client.GetNodeMetricsAsync(token))
                                {
                                    byKey[ResourceRowViewModel.KeyFor(clusterName, $"/{m.Name}")] = (m.CpuNanocores, m.MemoryBytes);
                                }
                            }
                        }
                        catch (MetricsUnavailableException)
                        {
                            // Registered but not serving (metrics-server down), or absent
                            // entirely on this cluster.
                            unavailable++;
                        }
                        catch (Exception) when (!token.IsCancellationRequested)
                        {
                            // Transient (throttling, a restarting metrics-server):
                            // keep the last sample on screen and retry next tick.
                        }
                    }

                    if (unavailable == sources.Count)
                    {
                        // No cluster in scope has a usable metrics API: stop asking and
                        // take the columns away rather than polling into the void.
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _metricsApiAvailable = false;
                            AreMetricsVisible = false;
                        });
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() => ApplyUsage(byKey));
                    await timer.WaitForNextTickAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                // normal on kind/namespace switch or disconnect
            }
        }, token);
    }

    /// <summary>Pushes one poll's samples onto the matching rows; rows with no sample fall back to "—".</summary>
    private void ApplyUsage(Dictionary<string, (long? Cpu, long? Memory)> byKey)
    {
        foreach (var (key, row) in _rowsByKey)
        {
            if (byKey.TryGetValue(key, out var sample))
            {
                row.ApplyUsage(sample.Cpu, sample.Memory);
            }
            else
            {
                row.ClearUsage();
            }
        }

        // A poll rewrites the very values a CPU- or memory-sorted list is ordered by, and
        // it rewrites every row at once — so unlike a watch tick, which changes one row,
        // this re-orders the whole list. In place, though (see ResortVisibleRows): a
        // rebuild would raise a Reset, and a list that jumped back to the top every 15
        // seconds would be unusable for the one job a CPU sort exists for.
        if (SortColumnId is ResourceColumn.Cpu or ResourceColumn.Memory)
        {
            ResortVisibleRows();
        }
    }

    /// <summary>
    /// Applies one watch event to <see cref="Rows"/> by key. Nothing here consults the
    /// row filter, and that is the invariant, not an omission: <c>Rows</c> is the
    /// informer's own view of the cluster, so a row hidden by the filter has to stay in
    /// it — drop it and the next Modified for that object finds no entry in
    /// <see cref="_rowsByKey"/> and reads as a fresh add, which resurfaces the row in
    /// the middle of a filtered list. Pinned by <c>ClusterTabRowFilterTests</c>.
    ///
    /// Internal rather than private only so that test can drive this path for real;
    /// nothing else in the app calls it.
    /// </summary>
    internal void Apply(ResourceEvent<DynamicResource> evt)
    {
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                // A Reset is the *start* of a list, not the end of one, so it turns the
                // loading state back on rather than off. Ending it here is what made a
                // distant cluster render "No pods found" for the whole duration of the
                // list request — the empty state and the loading state swapped places,
                // which is the exact failure UI rule 18 exists to prevent. Synced below
                // is the honest end; the first row arriving is the other one.
                IsListLoading = true;
                Rows.Clear();
                _rowsByKey.Clear();
                ConnectionWarning = null;
                break;

            case ResourceEventType.Synced:
                // Everything that existed when the sync started has been delivered. For
                // an empty namespace this is the only frame that ever arrives, so it is
                // what settles "no objects" against "not there yet".
                IsListLoading = false;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } resource:
                // The first row is enough to stop waiting: the list paginates, and
                // hiding page one behind a spinner until page four lands is the same
                // unresponsiveness in the other direction.
                IsListLoading = false;
                if (_rowsByKey.TryGetValue(resource.Key, out var existing))
                {
                    existing.Update(resource);
                    RepositionRow(existing);
                }
                else
                {
                    var row = new ResourceRowViewModel(resource);
                    row.SetPrinterColumns(VisiblePrinterColumns);
                    _rowsByKey[resource.Key] = row;
                    Rows.Add(row);
                }

                break;

            case ResourceEventType.Deleted when evt.Resource is { } resource:
                IsListLoading = false;
                if (_rowsByKey.Remove(resource.Key, out var removed))
                {
                    Rows.Remove(removed);
                }

                break;
        }

        RecomputeListEmpty();
    }

    /// <summary>
    /// Fleet-mode counterpart of <see cref="Apply"/>. The one thing it must not do is
    /// treat a Reset as "clear the list": a Reset is scoped to the cluster that sent it
    /// (initial sync, or a relist after 410 Gone), so clearing everything would wipe
    /// four healthy clusters because the fifth reconnected.
    ///
    /// Internal for the same reason <see cref="Apply"/> is — the cluster-qualified keys
    /// make this a second way to get the filter/informer split wrong.
    /// </summary>
    internal void ApplyFleet(FleetResourceEvent tagged)
    {
        var cluster = tagged.ClusterName;

        switch (tagged.Event.Type)
        {
            case ResourceEventType.Synced:
                // Any one member finishing its list is enough to stop waiting: partial
                // is the normal state of a fleet view and the header already says how
                // many clusters are in it, so holding the spinner for the slowest
                // member would hide four healthy lists behind the fifth.
                IsListLoading = false;
                break;

            case ResourceEventType.Reset:
                foreach (var key in _rowsByKey
                    .Where(entry => string.Equals(entry.Value.ClusterName, cluster, StringComparison.Ordinal))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    if (_rowsByKey.Remove(key, out var stale))
                    {
                        Rows.Remove(stale);
                    }
                }

                ConnectionWarning = null;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when tagged.Event.Resource is { } added:
                IsListLoading = false;
                var addedKey = ResourceRowViewModel.KeyFor(cluster, added.Key);
                if (_rowsByKey.TryGetValue(addedKey, out var existing))
                {
                    existing.Update(added);
                    RepositionRow(existing);
                }
                else
                {
                    var row = new ResourceRowViewModel(added, cluster);
                    row.SetPrinterColumns(VisiblePrinterColumns);
                    _rowsByKey[addedKey] = row;
                    Rows.Add(row);
                }

                break;

            case ResourceEventType.Deleted when tagged.Event.Resource is { } deleted:
                IsListLoading = false;
                if (_rowsByKey.Remove(ResourceRowViewModel.KeyFor(cluster, deleted.Key), out var gone))
                {
                    Rows.Remove(gone);
                }

                break;
        }

        RecomputeListEmpty();
    }

    /// <summary>Double-click / Enter: promotes (or opens) a permanent tab. Pod → detail; anything else → YAML.</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync() => await OpenRowAsync(SelectedRow, preview: false);

    /// <summary>Space: quick-peek — replaces the current preview tab in place.</summary>
    [RelayCommand]
    private async Task PeekSelectedAsync() => await OpenRowAsync(SelectedRow, preview: true);

    // --------------------------------------------------------- row actions
    //
    // Right-clicking a resource did nothing — the app had exactly one context menu
    // anywhere (the cluster tab's environment override), and none of logs / exec /
    // port-forward was reachable from the list at all: you had to open a pod's detail
    // tab first and find the buttons on its container strip. These commands back both
    // the row's ContextFlyout and the matching palette entries, so the same six
    // actions are reachable by mouse and by keyboard.

    /// <summary>True when the selected row is a pod — the only kind logs/exec/forward apply to.</summary>
    public bool IsPodRowSelected => SelectedRow is { } row && DescriptorFor(row) is { Kind: "Pod", Group: "" };

    /// <summary>True whenever a row is selected at all (YAML and delete work for any kind).</summary>
    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>
    /// Opens pod detail on the Logs tab. Same tab-reuse path as a double-click, so
    /// this never opens a second tab for a pod that already has one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private async Task OpenLogsAsync() => await OpenPodDetailAsync(previous: false);

    /// <summary>
    /// Opens pod detail on the Logs tab, showing the crashed instance. This is the
    /// single most important gesture on a CrashLoopBackOff and it had no entry point
    /// outside a toggle that didn't work.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private async Task OpenPreviousLogsAsync() => await OpenPodDetailAsync(previous: true);

    private async Task OpenPodDetailAsync(bool previous)
    {
        await OpenRowAsync(SelectedRow, preview: false);
        if (SelectedInspectorTab is not PodDetailTabViewModel detail)
        {
            return;
        }

        detail.SelectedDetailTabIndex = 0;
        detail.IsShowingPreviousLogs = previous;
    }

    /// <summary>
    /// True when the selected object names the pods it owns — which is the honest test
    /// for "can these be tailed as one stream", and is read off the object rather than
    /// off a list of kinds, exactly as the scale/restart capability checks are. A
    /// Deployment, StatefulSet, DaemonSet, ReplicaSet, Job, Service and a CRD that
    /// declares a pod selector all qualify on the same evidence; a pod does not (it has
    /// its own detail pane), and neither does an object whose selector is empty — see
    /// <see cref="LabelSelector.ForPodsOf"/> for why an empty selector is refused rather
    /// than read as "everything".
    /// </summary>
    public bool CanAggregateLogsForSelectedRow =>
        SelectedRow is { } row && LabelSelector.ForPodsOf(row.Resource) is not null;

    /// <summary>
    /// One pane over every pod the selected workload owns. This is the gesture people
    /// leave for <c>stern</c>: during a rolling deployment the pod going away and the
    /// pod coming up are the same question, and reading them in two panes is reading
    /// them in the wrong order.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAggregateLogsForSelectedRow))]
    private void OpenWorkloadLogs()
    {
        if (SelectedRow is not { } row
            || DescriptorFor(row) is not { } descriptor
            || LabelSelector.ForPodsOf(row.Resource) is not { } selector)
        {
            return;
        }

        // Null in demo mode, where the pane still works: its pods come out of the
        // shipped dataset through the same LabelSelector.Matches the live path renders
        // into a query — see InspectorTabViewModelBase.IsDemo.
        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        var key = WorkloadLogsTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new WorkloadLogsTabViewModel(client, descriptor, row.Resource, selector, row.ClusterName));
    }

    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private void ExecIntoSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        // Null in demo mode, which is exactly what the tab reads as "not available
        // here" — see InspectorTabViewModelBase.IsDemo.
        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        AddInspectorTab(new ExecTabViewModel(client, row.Namespace, row.Name, FirstContainerOf(row)));
    }

    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private void PortForwardSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        AddInspectorTab(new PortForwardTabViewModel(client, row.Namespace, row.Name, DeclaredPortsOf(row)));
    }

    /// <summary>
    /// Always the YAML editor, even for a pod — whose default action is the detail
    /// pane, leaving no way to reach its manifest from the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void EditSelectedYaml()
    {
        if (SelectedRow is not { } row || DescriptorFor(row) is not { } descriptor)
        {
            return;
        }

        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        var key = YamlEditorTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new YamlEditorTabViewModel(
            client, descriptor, row.Namespace, row.Name, row.Resource.ToYaml(), row.ClusterName));
    }

    // ----------------------------------------------------- the machine's terminal
    //
    // "Open a terminal here" — the daily gesture people leave a GUI for, and the one
    // thing this app had no answer to at all. It is not a shell *inside* kubeNimbus
    // (that would need a PTY dependency and would still not be the user's terminal,
    // with their prompt, their fonts and their tools); it is the user's own terminal,
    // handed KUBECONFIG and a pinned current-context. See TerminalLauncher for how the
    // context is pinned without ever writing to the user's kubeconfig, and for why
    // Windows starts a shell directly rather than going through wt.exe.

    /// <summary>
    /// The result of the last "open in terminal", or null when there is nothing to say.
    /// Present only while it has something to report, so it costs no chrome the rest of
    /// the time (UI rule 1), and rendered as an InfoBar rather than a status dot
    /// (UI rule 11). Four outcomes land here — opened, opened-without-kubectl, nothing
    /// to open, and the demo cluster's refusal — because a fire-and-forget command whose
    /// window opens behind the app is exactly the kind that otherwise fails silently.
    /// </summary>
    [ObservableProperty]
    private string? _terminalNotice;

    [ObservableProperty]
    private bool _terminalNoticeIsWarning;

    [ObservableProperty]
    private bool _terminalNoticeIsError;

    /// <summary>
    /// Sentence for each outcome. Static and public so the wording — which is the whole
    /// deliverable of the "says so when kubectl is missing" half of this — can be
    /// asserted without a terminal, a display or a process, and so the screenshot
    /// harness renders the app's own words rather than a fixture's approximation of them.
    /// </summary>
    public static (string Message, bool Warning, bool Error) DescribeTerminalLaunch(TerminalLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            TerminalLaunchOutcome.NoKubeconfig => (
                "The demo cluster's objects ship inside kubeNimbus — there is no kubeconfig behind it, so there is "
                + "nothing for a terminal to point at. Open a real cluster and try again.",
                true, false),

            TerminalLaunchOutcome.NoTerminal => (
                $"No terminal could be opened. Tried: {string.Join(", ", result.Tried)}. "
                + $"Set KUBECONFIG={result.KubeconfigValue} in a terminal of your own — the context is already "
                + $"pinned to {result.ContextName} in the first file.",
                false, true),

            TerminalLaunchOutcome.Failed => (
                $"Could not prepare the terminal: {result.Error}", false, true),

            _ when result.KubectlMissing => (
                $"Opened {result.TerminalLabel} on {result.ContextName}, but kubectl was not found on this app's "
                + "PATH. KUBECONFIG and the context are set either way, so kubectl, helm, k9s and anything else "
                + "that reads a kubeconfig will use this cluster — a GUI often sees a shorter PATH than your "
                + "shell does, so it may well be there.",
                true, false),

            _ => (
                $"Opened {result.TerminalLabel} on {result.ContextName}. KUBECONFIG={result.KubeconfigValue} — "
                + "your own kubeconfig is merged in unchanged, the context is pinned in the first file.",
                false, false),
        };
    }

    /// <summary>
    /// Opens the machine's own terminal on this cluster. Deliberately has no
    /// <c>CanExecute</c> gate for the demo cluster: it refuses in place with a reason
    /// (the demo section's rule 5), which is more use than a menu item that is greyed
    /// out for a reason nobody can read.
    /// </summary>
    [RelayCommand]
    private async Task OpenInTerminalAsync()
    {
        TerminalNotice = "Opening a terminal…";
        TerminalNoticeIsWarning = false;
        TerminalNoticeIsError = false;

        TerminalLaunchResult result;
        try
        {
            result = await TerminalLauncher.OpenAsync(Context);
        }
        catch (Exception ex)
        {
            // The launcher answers ordinary failures with an outcome; anything that gets
            // here is unexpected, and still must not take the tab down.
            TerminalNotice = $"Could not open a terminal: {ex.Message}";
            TerminalNoticeIsError = true;
            return;
        }

        var (message, warning, error) = DescribeTerminalLaunch(result);
        TerminalNotice = message;
        TerminalNoticeIsWarning = warning;
        TerminalNoticeIsError = error;
    }

    [RelayCommand]
    private void DismissTerminalNotice() => TerminalNotice = null;

    // ------------------------------------------------- mutating workload actions
    //
    // Scale / rollout restart / delete. The app was read-mostly before these: the only
    // way to change a replica count was to edit YAML, and "restart that deployment" —
    // the single most common on-call GUI action, and one click in every competitor —
    // had no entry point at all. All three land on the same armed confirm strip
    // (see RowActionViewModel), which is what makes "confirmable" one implementation
    // rather than three, and what gives the replica count somewhere to be typed.

    /// <summary>
    /// The armed action, or null when nothing is pending. Set by the three commands
    /// below and cleared when the strip is dismissed or the list changes underneath it.
    /// </summary>
    [ObservableProperty]
    private RowActionViewModel? _pendingRowAction;

    /// <summary>
    /// Whether the selected row's kind can be scaled — the server declares a
    /// <c>scale</c> subresource for it. Discovery, never a list of kinds: in an
    /// aggregated fleet list the descriptor is the one that cluster's own discovery
    /// produced, so the same CRD can be scalable on one cluster and not on another and
    /// the menu is right on both.
    /// </summary>
    public bool CanScaleSelectedRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor && WorkloadActions.SupportsScale(descriptor);

    /// <summary>
    /// Whether the selected object can be rollout-restarted: it has a pod template to
    /// stamp. A property of the object rather than of its kind, which is what makes it
    /// true for Deployments, StatefulSets, DaemonSets <em>and</em> a CRD that embeds a
    /// pod template, with none of the four named anywhere.
    /// </summary>
    public bool CanRestartSelectedRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor
        && WorkloadActions.SupportsRestart(descriptor, row.Resource);

    /// <summary>Whether the server says the selected row's kind can be deleted at all.</summary>
    public bool CanDeleteSelectedRow =>
        SelectedRow is { } row && DescriptorFor(row) is { } descriptor && WorkloadActions.SupportsDelete(descriptor);

    [RelayCommand(CanExecute = nameof(CanScaleSelectedRow))]
    private async Task ScaleSelectedAsync()
    {
        if (ArmRowAction(RowActionKind.Scale) is { } action)
        {
            // Opens on the object's own spec.replicas so the box is never empty, then
            // replaces it with the scale subresource's answer, which is the field the
            // patch will actually set.
            await action.LoadCurrentScaleAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartSelectedRow))]
    private void RestartSelected() => ArmRowAction(RowActionKind.Restart);

    /// <summary>
    /// Delete, with the confirm armed in place. It used to open the object's YAML with
    /// that editor's own confirm armed, which put an editor tab and a manifest between
    /// someone and a one-line question; the strip asks it where the row is, and names
    /// the object either way. The YAML editor keeps its own Delete for when you are
    /// already in there.
    ///
    /// <para>
    /// "Confirm before deleting" is read here, at the press, exactly as
    /// <c>YamlEditorTabViewModel.RequestDeleteAsync</c> reads it: someone who turns it
    /// back on after a near-miss expects the very next delete to ask. Scale and restart
    /// do not consult it — it is a setting about deleting, and scale needs its input
    /// step regardless.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRow))]
    private void DeleteSelected()
    {
        if (ArmRowAction(RowActionKind.Delete) is { } action && !App.LoadSettings().ConfirmDeletes)
        {
            action.ConfirmCommand.Execute(null);
        }
    }

    // ---------------------------------------------------------------- node actions
    //
    // Cordon, uncordon and drain, on the same armed strip as scale/restart/delete. They
    // are node-only and they say so through the same kind of capability check the other
    // three use — with one honest difference, argued in NodeActions.SupportsCordon: there
    // is no discovery signal or object marker for "can be cordoned", because
    // spec.unschedulable is a field of the core Node schema that an uncordoned node omits
    // entirely. Drain adds the signal there *is* one for: whether this server serves
    // pods/eviction.

    /// <summary>True when the selected row is a core/v1 Node this server says is patchable.</summary>
    public bool CanCordonSelectedRow =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsCordon(descriptor)
        && !NodeActions.IsCordoned(row.Resource);

    /// <summary>
    /// Uncordon is offered only for a node that <em>is</em> cordoned, and cordon only for
    /// one that is not. Two commands, one slot: the menu shows whichever applies, which
    /// is the "a control pair where one half is always disabled is one control" rule the
    /// port-forward pane's Start/Stop settled (UI rule 11). Two commands rather than one
    /// toggle so that neither the palette nor a test has to infer which way it would go.
    /// </summary>
    public bool CanUncordonSelectedRow =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsCordon(descriptor)
        && NodeActions.IsCordoned(row.Resource);

    /// <summary>True when this cluster serves <c>pods/eviction</c> and the row is a node.</summary>
    public bool CanDrainSelectedRow =>
        SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.SupportsDrain(descriptor, PodDescriptorFor(row));

    [RelayCommand(CanExecute = nameof(CanCordonSelectedRow))]
    private void CordonSelected() => ArmRowAction(RowActionKind.Cordon);

    [RelayCommand(CanExecute = nameof(CanUncordonSelectedRow))]
    private void UncordonSelected() => ArmRowAction(RowActionKind.Uncordon);

    /// <summary>
    /// Arms a drain and reads the pods on the node so the strip can state what it would
    /// do — and refuse, by name, for the pods that need an option nobody has given. The
    /// plan is loaded before anything is evicted for the same reason the replica count is
    /// read before a scale: the confirm has to be about something real.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDrainSelectedRow))]
    private async Task DrainSelectedAsync()
    {
        if (ArmRowAction(RowActionKind.Drain) is { } action)
        {
            await action.LoadDrainPlanAsync();
        }
    }

    /// <summary>
    /// Builds the pending action for the selected row, against that row's own client and
    /// descriptor (its cluster's, in fleet mode — an action that resolved either from the
    /// tab would fire at the wrong cluster).
    /// </summary>
    private RowActionViewModel? ArmRowAction(RowActionKind kind)
    {
        if (SelectedRow is not { } row || DescriptorFor(row) is not { } descriptor)
        {
            return null;
        }

        // Null only on the demo cluster, which is what the strip reads as "not available
        // here" — same shape as the exec and port-forward panes.
        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return null;
        }

        // One action at a time, and a running one is never replaced. It matters most for
        // a drain, whose eviction loop lives in the strip: re-arming over it would leave
        // the loop running with nothing on screen reporting it. Portainer reached the
        // same rule from the other direction (portainer#4006) — a drain should be issued
        // to one node at a time, and a single-slot strip is what enforces that here.
        if (PendingRowAction is { IsBusy: true } or { IsDraining: true })
        {
            return null;
        }

        var action = new RowActionViewModel(
            kind, client, descriptor, row.Namespace, row.Name, row.ClusterName,
            kind == RowActionKind.Scale ? WorkloadActions.DeclaredReplicas(row.Resource) : null,
            PodDescriptorFor(row));

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingRowAction, action))
            {
                PendingRowAction = null;
            }
        };

        PendingRowAction = action;
        return action;
    }

    /// <summary>
    /// The pod's first container, which is what <c>kubectl exec</c> defaults to. The
    /// pane's own picker is where a different one gets chosen.
    /// </summary>
    private static string FirstContainerOf(ResourceRowViewModel row)
    {
        if (row.Resource.Raw.TryGetProperty("spec", out var spec)
            && spec.TryGetProperty("containers", out var containers)
            && containers.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (container.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return "";
    }

    /// <summary>Every TCP port every container declares, so the forward pane can offer them.</summary>
    private static IReadOnlyList<ContainerPort> DeclaredPortsOf(ResourceRowViewModel row)
    {
        var ports = new List<ContainerPort>();
        if (!row.Resource.Raw.TryGetProperty("spec", out var spec)
            || !spec.TryGetProperty("containers", out var containers)
            || containers.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return ports;
        }

        foreach (var container in containers.EnumerateArray())
        {
            if (!container.TryGetProperty("ports", out var portsEl)
                || portsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            foreach (var port in portsEl.EnumerateArray())
            {
                if (port.TryGetProperty("containerPort", out var cp) && cp.TryGetInt32(out var number)
                    && (!port.TryGetProperty("protocol", out var proto) || proto.GetString() is null or "TCP"))
                {
                    var name = port.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                    ports.Add(new ContainerPort(number, name));
                }
            }
        }

        return ports;
    }

    private async Task OpenRowAsync(ResourceRowViewModel? row, bool preview)
    {
        // In fleet mode the row's own cluster owns it — using this tab's client here
        // would open (and later apply/delete) against the wrong cluster.
        if (row is null || DescriptorFor(row) is not { } descriptor)
        {
            return;
        }

        var client = ClientFor(row);
        if (client is null && !IsDemo)
        {
            return;
        }

        // Events aren't independently editable/useful objects to browse — jumping
        // straight to what the event is about (the same navigation owner-chips use)
        // is the more useful default action here, matching CLAUDE.md's "double-click
        // = default action" rule.
        if (descriptor is { Kind: "Event", Group: "" } && row.Resource.InvolvedObject() is { } involved)
        {
            await OpenOwnerAsync(involved, row.Resource.InvolvedObjectNamespace() ?? row.Namespace);
            return;
        }

        // Double-click = the default action for the kind (UI rule 2). A node's is its
        // detail pane, not its manifest: conditions, taints and how full it is are what
        // the double-click is for, and the YAML is one context-menu item away as it is
        // for a pod.
        var isPod = descriptor is { Kind: "Pod", Group: "" };
        var isNode = NodeActions.IsNodeKind(descriptor);
        var key = (isPod, isNode) switch
        {
            (true, _) => PodDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name),
            (_, true) => NodeDetailTabViewModel.KeyFor(row.ClusterName, row.Name),
            _ => YamlEditorTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name),
        };
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            SelectedInspectorTab = existing;
            return;
        }

        InspectorTabViewModelBase tab = (isPod, isNode) switch
        {
            (true, _) => new PodDetailTabViewModel(
                client, row, AddInspectorTab,
                // Bound to this row's cluster so owner navigation stays on it.
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName),
                row.ClusterName),
            (_, true) => new NodeDetailTabViewModel(
                client, row, PodDescriptorFor(row),
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName),
                row.ClusterName),
            _ => new YamlEditorTabViewModel(
                client, descriptor, row.Namespace, row.Name, row.Resource.ToYaml(), row.ClusterName),
        };

        tab.IsPreview = preview;
        AddInspectorTab(tab, replacePreview: preview);
    }

    private void AddInspectorTab(InspectorTabViewModelBase tab) => AddInspectorTab(tab, replacePreview: tab.IsPreview);

    private void AddInspectorTab(InspectorTabViewModelBase tab, bool replacePreview)
    {
        if (replacePreview)
        {
            var previousPreview = InspectorTabs.FirstOrDefault(t => t.IsPreview);
            if (previousPreview is not null)
            {
                _ = CloseInspectorTabAsync(previousPreview);
            }
        }

        InspectorTabs.Add(tab);
        SelectedInspectorTab = tab;
    }

    /// <summary>
    /// Resolves an ownerReference (pod → replicaset → deployment, etc.) and opens its
    /// YAML. <paramref name="clusterName"/> keeps the whole chain on the cluster the
    /// starting object came from when navigating out of an aggregated fleet row —
    /// an owner chain that hopped clusters mid-way would be nonsense.
    /// </summary>
    private async Task OpenOwnerAsync(OwnerRef owner, string? namespaceHint, string clusterName = "")
    {
        if (IsDemo)
        {
            OpenDemoOwner(owner, namespaceHint);
            return;
        }

        if (ClientForCluster(clusterName) is not { } client)
        {
            return;
        }

        var resolved = await client.ResolveOwnerAsync(owner, namespaceHint);
        if (resolved is null)
        {
            ConnectionWarning = $"Owner {owner.Kind}/{owner.Name} could not be resolved (deleted?).";
            return;
        }

        var catalog = await client.GetResourceCatalogAsync();
        var descriptor = catalog.FirstOrDefault(d =>
            d.ApiVersion == owner.ApiVersion && d.Kind == owner.Kind);
        if (descriptor is null)
        {
            return;
        }

        var key = descriptor.Kind == "Pod"
            ? PodDetailTabViewModel.KeyFor(clusterName, resolved.Namespace, resolved.Name)
            : YamlEditorTabViewModel.KeyFor(clusterName, descriptor, resolved.Namespace, resolved.Name);
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            SelectedInspectorTab = existing;
            return;
        }

        var tab = new YamlEditorTabViewModel(
            client, descriptor, resolved.Namespace, resolved.Name, resolved.ToYaml(), clusterName);
        AddInspectorTab(tab, replacePreview: false);
    }

    /// <summary>
    /// Opens the access-review tab. With no subject it answers "what may I do in
    /// this namespace?" straight from the API server; with one (a selected
    /// ServiceAccount) it also traces where that subject's access comes from.
    /// </summary>
    [RelayCommand]
    private void OpenAccessReview(SubjectRef? subject) => ShowAccessReview(subject);

    /// <summary>
    /// Opens the access review straight onto "Who can do X?" — the cluster-wide direction,
    /// which has no row to start from (the answer is a set of subjects, not a property of
    /// the selected object), so the palette is where it belongs.
    /// </summary>
    [RelayCommand]
    private void OpenWhoCan()
    {
        if (ShowAccessReview(null) is { } tab)
        {
            tab.SelectedTabIndex = RbacTabViewModel.WhoCanTabIndex;
        }
    }

    private RbacTabViewModel? ShowAccessReview(SubjectRef? subject)
    {
        if (Client is null)
        {
            return null;
        }

        var @namespace = SelectedNamespace == AllNamespaces ? "default" : SelectedNamespace;
        var key = subject is null
            ? $"rbac:{@namespace}"
            : $"rbac:{subject.Kind}/{subject.Namespace}/{subject.Name}";

        if (InspectorTabs.FirstOrDefault(t => t.Key == key) is RbacTabViewModel existing)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return existing;
        }

        var tab = new RbacTabViewModel(Client, @namespace, subject);
        AddInspectorTab(tab, replacePreview: false);
        return tab;
    }

    /// <summary>
    /// The selected row as an RBAC subject, when it is one — only ServiceAccounts
    /// exist as objects (Users and Groups are just strings in a binding), so
    /// that's the one kind that can seed a subject review from the list.
    /// </summary>
    public SubjectRef? SelectedRowAsSubject =>
        SelectedKind?.Descriptor is { Group: "", Kind: "ServiceAccount" } && SelectedRow is { } row
            ? new SubjectRef("ServiceAccount", row.Name, row.Namespace)
            : null;

    [RelayCommand]
    private void SelectInspectorTab(InspectorTabViewModelBase tab) => SelectedInspectorTab = tab;

    [RelayCommand]
    private async Task CloseInspectorTabAsync(InspectorTabViewModelBase tab)
    {
        await tab.OnClosingAsync();
        var index = InspectorTabs.IndexOf(tab);
        InspectorTabs.Remove(tab);
        if (SelectedInspectorTab == tab)
        {
            SelectedInspectorTab = InspectorTabs.Count == 0
                ? null
                : InspectorTabs[Math.Min(index, InspectorTabs.Count - 1)];
        }

        if (InspectorTabs.Count == 0)
        {
            IsInspectorMaximized = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A drain runs in this process and in this strip. Closing the tab stops it —
        // which is the honest behaviour and the one the confirm warned about, but it has
        // to be an explicit cancel rather than a task left running against a disposed
        // client.
        PendingRowAction?.CancelDrain();

        if (_watchCts is not null)
        {
            await _watchCts.CancelAsync();
            _watchCts.Dispose();
        }

        foreach (var tab in InspectorTabs.ToArray())
        {
            await tab.OnClosingAsync();
        }

        Client?.Dispose();
    }
}
