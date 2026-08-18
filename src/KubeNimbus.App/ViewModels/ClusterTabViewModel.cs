using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

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

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();
        foreach (var row in Rows)
        {
            if (MatchesRowFilter(row))
            {
                VisibleRows.Add(row);
            }
        }

        RecomputeListEmpty();
    }

    /// <summary>
    /// Mirrors <see cref="Rows"/> into <see cref="VisibleRows"/> through the filter.
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
                foreach (var row in added.OfType<ResourceRowViewModel>())
                {
                    if (MatchesRowFilter(row))
                    {
                        VisibleRows.Add(row);
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
    /// <see cref="MainWindowViewModel"/> (which owns it and persists it). Off — the
    /// default — hides the controls only a fraction of sessions need; on restores
    /// today's surface exactly. It is a *display* switch and nothing more: flipping
    /// it must never restart a watch, refetch anything, or lose list/inspector
    /// state, which is why every consumer below is a derived property rather than
    /// something that re-runs <see cref="RestartWatch"/>.
    ///
    /// Bind this two-way and nothing else. A <c>ToggleButton</c> given BOTH an
    /// <c>IsChecked</c> binding and a toggling <c>Command</c> flips the property in
    /// <c>OnClick()</c> before the command runs, so an inverting command lands back
    /// on the original value — a guaranteed no-op, and a bug this repo has shipped
    /// before.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreUsageColumnsVisible))]
    [NotifyPropertyChangedFor(nameof(IsFleetToggleVisible))]
    [NotifyPropertyChangedFor(nameof(VisiblePrinterColumns))]
    private bool _isAdvancedView;

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
        ApplySidebarChrome();

        // The advanced view is this app's `kubectl get -o wide`: a CRD's own
        // `priority: 1` columns join the list with it and leave with it. Re-evaluating
        // is JSON reading over objects the rows already hold, so this stays a display
        // switch — no fetch, no watch restart, no lost selection.
        PushPrinterColumnsToRows();

        // Tabs already open have to follow the switch live — the alternative is a
        // force-apply button that stays on screen until the tab is reopened.
        foreach (var tab in InspectorTabs)
        {
            tab.IsAdvancedView = value;
        }

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
    /// The list's CPU/Memory columns (number + sparkline). Two conditions, not one:
    /// the cluster has to actually serve metrics.k8s.io for the metered kind
    /// (<see cref="AreMetricsVisible"/>), *and* the user has to have asked for the
    /// busier layout. Read by <see cref="Views.ClusterTabView"/>'s code-behind —
    /// a DataGridColumn is outside the visual tree and can't bind.
    /// </summary>
    public bool AreUsageColumnsVisible => AreMetricsVisible && IsAdvancedView;

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
    /// The columns the grid actually draws: priority-0 always, the CRD's own
    /// <c>priority: 1</c> ones only in the advanced view (kubectl's <c>-o wide</c>
    /// lever, wired to the switch this app already has), a declared Age folded into the
    /// list's own live Age column, and the whole thing capped at the number of printer
    /// slots the grid declares. See <see cref="PrinterColumns.Visible"/>.
    /// </summary>
    public IReadOnlyList<PrinterColumn> VisiblePrinterColumns =>
        KubeNimbus.Core.PrinterColumns.Visible(PrinterColumns, IsAdvancedView, ResourceRowViewModel.PrinterCellCount);

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
    /// and outside the advanced view — but never while aggregation is actually on.
    /// Turning the advanced view off must not strand a tab in fleet mode with no
    /// control to leave it: the exit stays on screen as long as there is something
    /// to exit from, which also keeps the toggle a pure display switch (dropping
    /// out of fleet mode would restart the watch).
    /// </summary>
    public bool IsFleetToggleVisible => IsFleetViewAvailable && !IsHelmView && (IsAdvancedView || IsFleetView);

    partial void OnIsHelmViewChanged(bool value) => OnPropertyChanged(nameof(IsFleetToggleVisible));

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
        Status = DemoBanner;

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
    private void ApplySidebarChrome()
    {
        foreach (var section in SidebarSections)
        {
            section.ShowKindCount = IsAdvancedView;

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
            AreMetricsVisible = false;
            PrinterColumns = []; // Helm releases are not an API kind and have no CRD behind them.
            _ = RefreshHelmReleasesAsync();
            return;
        }

        IsHelmView = false;
        RestartWatch();
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
                Dispatcher.UIThread.Post(() => Status = $"Watch ended: {ex.Message}");
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
                Dispatcher.UIThread.Post(() => Status = $"Fleet watch ended: {ex.Message}");
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
        IsListLoading = false;

        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                Rows.Clear();
                _rowsByKey.Clear();
                ConnectionWarning = null;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } resource:
                if (_rowsByKey.TryGetValue(resource.Key, out var existing))
                {
                    existing.Update(resource);
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
        IsListLoading = false;
        var cluster = tagged.ClusterName;

        switch (tagged.Event.Type)
        {
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
                var addedKey = ResourceRowViewModel.KeyFor(cluster, added.Key);
                if (_rowsByKey.TryGetValue(addedKey, out var existing))
                {
                    existing.Update(added);
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

        // The one funnel every inspector tab enters through, which is why the
        // advanced-view mirror is stamped here rather than at each construction
        // site: a tab kind added later inherits the gate instead of quietly
        // shipping with it open.
        tab.IsAdvancedView = IsAdvancedView;

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
