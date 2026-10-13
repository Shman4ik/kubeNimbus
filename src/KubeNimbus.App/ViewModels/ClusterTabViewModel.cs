using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;
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

    /// <summary>
    /// Metered kinds whose usage this user may not read (a 403 from metrics.k8s.io), with
    /// the server's sentence, so returning to the list does not ask again. Narrow RBAC is
    /// the expected case, and a refusal is an answer, not a blip (#310). Cleared on connect,
    /// the one moment the user's permissions can have changed under the app's feet.
    /// </summary>
    private readonly Dictionary<string, string> _metricsRefused = new(StringComparer.Ordinal);

    public ClusterContext Context { get; }

    /// <summary>
    /// "Open a terminal on this cluster" and the notice that reports how it went, which the
    /// row and palette hand-offs to the user's terminal report into too (machine-terminal.md).
    /// </summary>
    public TerminalNoticeViewModel Terminal { get; }

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

    /// <summary>
    /// The connection, once one succeeded; null on the demo cluster for its whole life.
    /// The setter is internal for the view-model tests, which hand a tab an offline client
    /// so a mutating path runs past its demo refusal; only <c>ConnectAsync</c> sets it here.
    /// </summary>
    public ClusterClient? Client
    {
        get => _client;
        internal set
        {
            _client = value;

            // New YAML… needs an API server to apply to, and the menu item bound to it is
            // created before the connect lands.
            OnPropertyChanged(nameof(CanCreateFromYaml));
            NewYamlCommand.NotifyCanExecuteChanged();
        }
    }

    private ClusterClient? _client;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    private bool _isConnecting;

    /// <summary>
    /// The last connect's failure, stated where the list would be; null while connecting
    /// and once connected. See <see cref="ConnectionFailureViewModel"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionFailure))]
    private ConnectionFailureViewModel? _connectionFailure;

    public bool HasConnectionFailure => ConnectionFailure is not null;

    partial void OnConnectionFailureChanging(ConnectionFailureViewModel? value) => ConnectionFailure?.Detach();

    partial void OnConnectionFailureChanged(ConnectionFailureViewModel? value) => Applications.OnTabConnectionChanged();

    /// <summary>
    /// Whether the warning beside the list carries a Reconnect button: true for a watch
    /// that lost its connection or had its credentials refused, false for the other things
    /// that warning reports (a refused namespace list, a missing owner), where
    /// re-resolving credentials would change nothing. Reset whenever the warning changes,
    /// so a later warning of the other kind cannot inherit the button.
    /// </summary>
    [ObservableProperty]
    private bool _connectionWarningOffersReconnect;

    partial void OnConnectionWarningChanged(string? value)
    {
        ConnectionWarningOffersReconnect = false;
        OnPropertyChanged(nameof(IsStatusWorthShowing));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isConnected;

    /// <summary>
    /// Neither connected nor connecting. The tab's status dot needs three states, not
    /// two: opening a cluster that is still dialling looked identical to one that
    /// failed, so picking a cluster appeared to do nothing until it finished.
    /// </summary>
    public bool IsIdle => !IsConnected && !IsConnecting;

    partial void OnIsConnectedChanged(bool value) => Applications.OnTabConnectionChanged();

    partial void OnIsConnectingChanged(bool value) => Applications.OnTabConnectionChanged();

    /// <summary>
    /// This tab's Applications mode. Created with the tab and started the first time the
    /// mode is shown for it; its watches are its own, so switching modes never restarts the
    /// Resources list's watch or loses its state, and the other way round.
    /// </summary>
    public ApplicationsViewModel Applications { get; }

    /// <summary>The namespaces recently picked on this context — one of the Applications list's fallbacks under narrow RBAC.</summary>
    internal IReadOnlyList<string> RecentNamespaceNames => _recentNamespaces;

    [ObservableProperty]
    private string _status = "Not connected.";

    /// <summary>
    /// The status that says nothing a glance at the tab does not: "Connected — Kubernetes
    /// v1.33", or the demo line under the demo bar. Recorded where it is written rather
    /// than recognized by its wording.
    /// </summary>
    private string? _routineStatus;

    /// <summary>
    /// Whether the shell's status bar is worth its row for this tab. It used to show
    /// "Connected — Kubernetes v1.31.2" for the whole life of every healthy tab, a row of
    /// chrome for a fact the tab's green dot already carries (its tooltip keeps the
    /// version). Anything else — connecting, a failure, a watch that ended, a connection
    /// warning — still gets the bar.
    /// </summary>
    public bool IsStatusWorthShowing =>
        Status != _routineStatus || ConnectionWarning is not null || IsTlsUnverified || IsPlainHttp
        || MetricsNotice is not null;

    /// <summary>
    /// The connected cluster's entry sets <c>insecure-skip-tls-verify</c>: nothing checks
    /// that the server is the cluster, so anyone on the network path can read the credential
    /// and answer in the cluster's name. It keeps the status bar on screen, with
    /// <see cref="TlsUnverifiedNotice"/>, for as long as the tab is connected — the one row
    /// already there for "something about this connection is worth knowing", in both modes,
    /// costing nothing on a tab without the flag.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusWorthShowing))]
    private bool _isTlsUnverified;

    /// <summary>What the status bar says while <see cref="IsTlsUnverified"/>.</summary>
    public const string TlsUnverifiedNotice = "TLS not verified: this cluster's kubeconfig sets insecure-skip-tls-verify.";

    /// <summary>
    /// The connected cluster's server is a plain <c>http://</c> URL (ENG-59): no TLS at all, so
    /// the credential and every response are readable on the network path. Kept on screen the
    /// way <see cref="IsTlsUnverified"/> is, in the same slot; where both would apply this is
    /// the one shown, since with no TLS there is no certificate to skip checking.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusWorthShowing))]
    private bool _isPlainHttp;

    /// <summary>What the status bar says while <see cref="IsPlainHttp"/>.</summary>
    public const string PlainHttpNotice = "Not encrypted: this cluster's server is plain http://, so its traffic, credential included, is sent in the clear.";

    /// <summary>
    /// The two transport notices for a connected <paramref name="client"/>, or both off
    /// without one. One place, so a connect and a reconnect cannot disagree about them.
    /// </summary>
    internal void ApplyTransportNotices(ClusterClient? client)
    {
        IsPlainHttp = client?.UsesPlainHttp ?? false;
        IsTlsUnverified = client is { SkipsTlsVerification: true, UsesPlainHttp: false };
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(IsStatusWorthShowing));

    /// <summary>The routine connected line, recorded as routine. The screenshot harness's fixture tabs set it the same way.</summary>
    internal void SetConnectedStatus(string gitVersion)
    {
        _routineStatus = $"Connected — Kubernetes {gitVersion}.";
        Status = _routineStatus;
    }

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
        MarkSelectedKind();

        var layout =value is { IsHelmReleases: false, IsArgoDashboard: false, Descriptor: { } descriptor }
            ? GridLayoutStore.Load(GridLayoutStore.KeyFor(descriptor))
            : GridLayout.Empty;

        // The column chooser's half of the same record (FEAT-13). The view reads it on the
        // SelectedKind notification below, after its own visibility passes.
        LoadHiddenColumns(layout);

        // A selection is a question about the list it was made in.
        SetSelectedRows([]);

        // Unhealthy-only is a mode that outlives the kind, but whether it *applies* is
        // a property of the kind. No rebuild of its own: SetSort below rebuilds anyway.
        RefreshHealthFilter(rebuild: false);

        // persist: false — this is reading a choice back, not making one, and writing it
        // straight back would turn every kind ever opened into a stored layout. No stored
        // choice means the kind's own default (the Events list opens newest first);
        // Unsorted is a stored choice of arrival order on a kind that has a default.
        var (sortColumn, sortDescending) = ShownSort(layout.SortColumn switch
        {
            null => DefaultSortFor(value?.Descriptor),
            ResourceColumn.Unsorted => (null, false),
            var column => (column, layout.SortDescending),
        });
        SetSort(sortColumn, sortDescending, persist: false);
        OnPropertyChanged(nameof(IsEventList));
        OnPropertyChanged(nameof(RowFilterPlaceholder));
        OnPropertyChanged(nameof(EmptyListHint));
        RaiseViewStateChanged();
    }

    /// <summary>
    /// The sort a kind opens on when nobody has chosen one for it. Arrival order for
    /// everything except Events, which open on Last seen with the most recent first:
    /// the question an events list answers is "what just happened", and arrival order
    /// there is the informer's relist order, which is the API server's storage order —
    /// meaningless to a reader. (Last seen follows Age's direction, so the newest-first
    /// order is its ascending one.)
    /// </summary>
    internal static (string? Column, bool Descending) DefaultSortFor(ResourceDescriptor? descriptor) =>
        descriptor is not null && descriptor.IsEventKind()
            ? (ResourceColumn.EventLastSeen, false)
            : (null, false);

    /// <summary>Whether the selected kind is Events, which the grid draws with kubectl's
    /// event columns instead of Name/Status/Age.</summary>
    public bool IsEventList => SelectedKind is { IsHelmReleases: false, IsArgoDashboard: false } kind
        && kind.Descriptor.IsEventKind();

    /// <summary>
    /// The search box's placeholder names what it matches, because for Events that is
    /// more than a name — see <see cref="ResourceRowViewModel.Matches"/>.
    /// </summary>
    public string RowFilterPlaceholder => IsEventList ? "Search reason, object, message…" : "Search by name…";

    /// <summary>
    /// A third line under "No &lt;kind&gt; found", for the kind whose empty list most
    /// often means something other than "there is nothing here": events expire, an hour
    /// after they last happened by default, so a quiet namespace with a perfectly real
    /// incident yesterday shows none. Empty for every other kind.
    /// </summary>
    public string EmptyListHint => IsEventList
        ? "Events expire an hour after they last happen (the API server's default --event-ttl), so an empty list means nothing happened recently — not that nothing ever did."
        : "";

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

    /// <summary>
    /// True when unhealthy-only has emptied a list that has rows the search box would
    /// otherwise show: "nothing is wrong here", which is a third state beside
    /// <see cref="IsListEmpty"/> ("there is nothing here") and <see cref="IsFilterEmpty"/>
    /// ("nothing is called that") — and the one of the three that is good news, so it
    /// must not look like either of the others.
    /// </summary>
    [ObservableProperty]
    private bool _isHealthFilterEmpty;

    /// <summary>"Nothing unhealthy among 12 Pods".</summary>
    [ObservableProperty]
    private string _healthFilterEmptyTitle = "";

    /// <summary>What the count was taken over — the search, the namespace — or, when
    /// no row reports a status at all, that there was nothing to judge.</summary>
    [ObservableProperty]
    private string _healthFilterEmptyDetail = "";

    partial void OnIsListLoadingChanged(bool value) => RecomputeListEmpty();

    /// <summary>
    /// The server's sentence when this user may not list the kind here (a 403 on the list's
    /// watch, #339), or null. Its own state in the list's place, never "No pods found" (which
    /// states a fact the app does not have) and never a "connection lost" warning over a
    /// spinner: the shared watch ends a refused list instead of retrying it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListRefused))]
    private string? _listRefusal;

    public bool IsListRefused => ListRefusal is not null;

    /// <summary>
    /// What to try instead, under the refusal: a namespace, when the refused list was the whole
    /// cluster's of a namespaced kind (narrow RBAC grants namespaces, not the cluster).
    /// </summary>
    [ObservableProperty]
    private string _listRefusalHint = "";

    /// <summary>
    /// The list's watch ended. A refusal is stated in the list's place (<see cref="ListRefusal"/>);
    /// anything else is said in the status bar. Either way the wait is over (UI rule 18).
    /// </summary>
    internal void EndListWatch(Exception ex, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        var refused = WatchRefusedException.From(ex);
        if (refused is not null && Rows.Count > 0)
        {
            // Listed, then refused the watch (list allowed, watch not): the rows are real, and
            // the status bar says they no longer update.
            Status = $"Live updates stopped: {refused.Message}";
        }
        else if (refused is not null)
        {
            ListRefusalHint = SelectedKind?.Descriptor.Namespaced == true && WatchNamespaces(SelectedKind.Descriptor) is [null]
                ? "Choose a namespace this account can read from the namespace picker."
                : "";
            ListRefusal = refused.Message;
        }
        else
        {
            Status = $"Watch ended: {ex.Message}";
        }

        IsListLoading = false;
        RecomputeListEmpty();
    }

    partial void OnListRefusalChanged(string? value) => RecomputeListEmpty();

    private void RecomputeListEmpty()
    {
        IsListEmpty = Rows.Count == 0 && !IsListLoading && ListRefusal is null;

        // Loading wins over every verdict (UI rule 18), and an empty list is its own
        // state; the other two only apply to a list that has rows but shows none.
        var narrowedToNothing = Rows.Count > 0 && VisibleRows.Count == 0 && !IsListLoading;
        var healthEmpty = narrowedToNothing && _healthFilterActive && DescribeHealthFilterEmpty();
        IsHealthFilterEmpty = healthEmpty;
        IsFilterEmpty = narrowedToNothing && !healthEmpty;

        RowFilterSummary = !IsRowFiltering && !_healthFilterActive
            ? ""
            : _healthFilterActive
                ? $"{VisibleRows.Count} of {Rows.Count} unhealthy"
                : $"{VisibleRows.Count} of {Rows.Count}";

        // Every change to the rows passes through here, so the capacity strip over the Nodes
        // list follows them without a hook of its own (#206).
        RefreshCapacity();
    }

    /// <summary>
    /// Decides between the two empty states a narrowed list can land in, and words the
    /// health one. If the search box matches nothing at all, that is the search's empty
    /// state ("Nothing matches …") whatever the health filter says — the typo is the
    /// thing to fix. If it matches rows and none is unhealthy, that is this one. Only
    /// runs while the list is showing nothing, so the scan never costs a busy list.
    /// </summary>
    private bool DescribeHealthFilterEmpty()
    {
        var candidates = 0;
        var judged = 0;
        foreach (var row in Rows)
        {
            if (_rowQuery.Length > 0 && !row.Matches(_rowQuery))
            {
                continue;
            }

            candidates++;
            if (row.StatusHealth != ResourceHealth.Idle)
            {
                judged++;
            }
        }

        if (candidates == 0)
        {
            return false;
        }

        HealthFilterEmptyTitle = $"Nothing unhealthy among {candidates} {SelectedKind?.DisplayName ?? "rows"}";

        var parts = new List<string>(2);
        if (_rowQuery.Length > 0)
        {
            parts.Add($"matching “{_rowQuery}”");
        }

        if (SelectedKind?.Descriptor.Namespaced == true)
        {
            parts.Add($"in {NamespaceDisplay}");
        }

        // A CRD whose objects carry no status the app can read passes the kind gate
        // (it is not a known statusless kind) and then judges nothing. Saying "nothing
        // unhealthy" there without qualification would be a claim the app cannot make.
        HealthFilterEmptyDetail = judged == 0
            ? "None of them reports a status this list can judge"
            : string.Join(" · ", parts);
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPodRowSelected))]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanScaleSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanRestartSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanAggregateLogsForSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanOpenLogsForSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanOpenPodLogsForSelectedRow))]
    [NotifyPropertyChangedFor(nameof(SelectedEventPod))]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkloadLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogsMaximizedCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(SyncArgoApplicationWithPruneCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshArgoApplicationCommand))]
    [NotifyPropertyChangedFor(nameof(CanTerminateSelectedArgoSync))]
    [NotifyCanExecuteChangedFor(nameof(TerminateArgoSyncCommand))]
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
    /// default.</b> It governs one thing: which kinds the sidebar lists. Off keeps the
    /// built-ins people actually browse (<see cref="SidebarGrouping.IsShownInBasicView"/>)
    /// and hides the API machinery and CRDs; on shows the whole catalog.
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
    /// Hidden for Helm (releases aren't an API kind, so there's nothing to fan out). The
    /// Argo dashboard has it since #195: one merged list with a Cluster column and the counts
    /// summed. It used to also require the advanced view, with a
    /// carve-out so that turning the switch off could not strand a tab in fleet mode
    /// with no control to leave it; the switch is the sidebar's alone now, so both the
    /// gate and the carve-out are gone and the toggle appears exactly when aggregating
    /// would show something a single tab does not.
    /// </summary>
    public bool IsFleetToggleVisible =>
        IsFleetViewAvailable && !IsHelmView;

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
        NotifyTerminalHandoffs();
        ReloadForFleetChange();
    }

    /// <summary>
    /// The Argo dashboard moves between its own cluster's watch and the fleet's per-member watches, so a
    /// fleet change reloads it; everything else re-fans its watch.
    /// </summary>
    private void ReloadForFleetChange()
    {
        if (IsArgoView)
        {
            _ = ReloadArgoAsync();
        }
        else
        {
            RestartWatch();
        }
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
            ReloadForFleetChange();
        }
    }

    // ------------------------------------------------------------ Argo CD dashboard
    //
    // Every Application on the cluster, with the sync and health Argo itself reports, the
    // seven counts across all of them, and the two actions Argo's own UI leads with. It is
    // read from the Kubernetes API and nothing else — see ClusterClient.ArgoCd.cs.

    public ObservableCollection<ArgoApplicationRowViewModel> ArgoApplications { get; } = [];

    /// <summary>The Argo grid's header sort; most urgent first until a header is clicked. Kept across reloads for the tab's life.</summary>
    public GridSort<ArgoApplicationRowViewModel> ArgoSort => _argoSort ??= new(
        ArgoApplications, ArgoApplicationComparer.ByRank, (column, descending) => new ArgoApplicationComparer(column, descending),
        () => SelectedArgoApplication, application => SelectedArgoApplication = application);

    private GridSort<ArgoApplicationRowViewModel>? _argoSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncSelectedArgoApplication))]
    [NotifyCanExecuteChangedFor(nameof(SyncArgoApplicationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncArgoApplicationWithPruneCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshArgoApplicationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenArgoApplicationCommand))]
    [NotifyPropertyChangedFor(nameof(CanTerminateSelectedArgoSync))]
    [NotifyCanExecuteChangedFor(nameof(TerminateArgoSyncCommand))]
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

    /// <summary>
    /// The kind to open on after connecting, as <c>&lt;group&gt;/&lt;Kind&gt;</c> — the one
    /// the tab was showing when the workspace was saved. Null (or a kind this cluster no
    /// longer serves) opens on Pods.
    /// </summary>
    public string? RestoreKindKey { get; init; }

    /// <summary>
    /// The namespace to open on after connecting. Null falls back to the kubeconfig
    /// context's own <c>namespace</c>, which is what kubectl would use, and then to all
    /// namespaces.
    /// </summary>
    public string? RestoreNamespace { get; init; }

    /// <summary>Every namespace the tab was showing when the workspace was saved, when it was more than one; <see cref="RestoreNamespace"/> is the first.</summary>
    public IReadOnlyList<string>? RestoreNamespaces { get; init; }

    /// <summary>The selected kind as a workspace key; see <see cref="RestoreKindKey"/>.</summary>
    public string? ViewKindKey => SelectedKind?.Descriptor is { } descriptor ? GridLayoutStore.KeyFor(descriptor) : null;

    /// <summary>
    /// Raised when the kind or namespace changes after the tab has settled, so the shell
    /// can save the workspace. Not raised during connect, where both are set by the
    /// restore itself and writing them straight back would be noise.
    /// </summary>
    public event EventHandler? ViewStateChanged;

    private bool _viewStateReady;

    private void RaiseViewStateChanged()
    {
        if (_viewStateReady)
        {
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ClusterTabViewModel(ClusterContext context)
    {
        Context = context;
        Terminal = new TerminalNoticeViewModel(context);
        Helm = new HelmBrowserViewModel(this);
        _recentNamespaces = WorkspaceStore.Load().RecentNamespaces?.GetValueOrDefault(NamespaceHistoryKey) ?? [];
        NamespaceOptions.CollectionChanged += (_, _) => RebuildNamespaceChoices();
        RebuildNamespaceChoices();

        // The list renders VisibleRows; everything that produces rows writes to Rows.
        // Subscribing here rather than filtering at each producer is what keeps the
        // watch, the fleet merge, the demo dataset and the screenshot fixtures all
        // unaware that a filter exists.
        Rows.CollectionChanged += OnRowsChanged;
        VisibleRows.CollectionChanged += OnRowsChangedForReveal;
        Applications = new ApplicationsViewModel(this);
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
        ConnectionFailure = null;
        Status = $"Connecting to {Context.Name}…";
        ClusterClient? created = null;
        try
        {
            // Awaited, never the synchronous Connect: blocking this (UI) thread on the
            // kubeconfig read hung the NativeAOT build at startup whenever a restored tab
            // connected — see Kubeconfig.BuildClientConfigAsync.
            //
            // A tab that already has a client (a connect that got past /version and failed
            // later) keeps it and re-resolves its credentials in place, so whatever holds
            // that object sees the retry too.
            ClusterClient client;
            if (Client is { } existing)
            {
                await existing.RefreshCredentialsAsync(force: true);
                client = existing;
            }
            else
            {
                client = created = await ClusterClient.ConnectAsync(Context);
            }

            // First and alone: it is the reachability check, and it is the request that
            // runs an exec credential plugin. Everything after it reuses that token, so
            // the fan-out below cannot start a dozen `aws eks get-token`s at once.
            var version = await client.GetServerVersionAsync();
            Client = client;
            created = null;
            IsConnected = true;
            ApplyTransportNotices(client);
            SetConnectedStatus(version.GitVersion);

            var sidebar = BuildSidebarAsync();
            var namespaces = RefreshNamespacesAsync();
            var metrics = DetectMetricsApiAsync();
            await namespaces;
            var startedPods = StartInitialPods();
            try
            {
                await Task.WhenAll(sidebar, metrics);
                if (startedPods) ReconcileInitialPods();
                else ApplyInitialView(fallbackNamespace: Context.Namespace);
            }
            catch (Exception ex) when (startedPods)
            {
                ConnectionWarning = $"Could not load the resource catalog: {ex.Message}";
            }

        }
        catch (Exception ex)
        {
            // A client that never answered /version was never handed to anything.
            created?.Dispose();

            // The report reads the kubeconfig again for the facts, off this thread, and is
            // set before the status and the flags so that anything waiting for
            // "Connection failed" (the smoke test's unreachable-cluster scenario, for one)
            // finds the explanation already in place.
            ConnectionFailure = new ConnectionFailureViewModel(await ConnectionReport.CreateAsync(Context, ex), this);

            // The status bar gets the step; the content area has the rest. The two used to
            // be the same sentence twice on one screen.
            Status = $"Connection failed ({ConnectionFailure.Report.StepPhrase}).";
            IsConnected = false;
            ApplyTransportNotices(null);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanReconnect => !IsConnecting && !IsDemo;

    /// <summary>
    /// Retry after a failed connect; on a connected tab, re-read the kubeconfig, re-run
    /// its credential plugin and restart the list — without closing the tab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case it exists for is an expired session: <c>aws sso login</c> (or
    /// <c>az login</c>, or a new Teleport certificate) in a terminal, then back to the app.
    /// The only way back used to be closing the tab and opening it again, which also threw
    /// away its kind, namespace, filters and inspector panes.
    /// </para>
    /// <para>
    /// The credential is re-resolved inside the tab's existing <see cref="ClusterClient"/>
    /// (<see cref="ClusterClient.RefreshCredentialsAsync"/>), so every pane that holds it —
    /// pod detail, logs, workload detail, a fleet merge — uses the new credential on its
    /// next request without being told. Re-running rather than remembering is hard rule 4:
    /// nothing the plugin printed is kept anywhere but inside that client.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        if (IsDemo)
        {
            return;
        }

        // A tab whose view was set up by a connect that succeeded, and whose reconnect then
        // failed, retries through this path again rather than through ConnectAsync, which would
        // re-apply the restored kind and namespace over the ones the reader had moved to.
        if (Client is not { } client || (!IsConnected && !_reconnectFailed))
        {
            await ConnectAsync();
            return;
        }

        IsConnecting = true;
        ConnectionWarning = null;
        Status = $"Re-reading the kubeconfig for {Context.Name}…";
        try
        {
            await client.RefreshCredentialsAsync(force: true);
            var version = await client.GetServerVersionAsync();
            _reconnectFailed = false;
            ConnectionFailure = null;
            IsConnected = true;
            ApplyTransportNotices(client);
            Status = $"Connected — Kubernetes {version.GitVersion}. Credentials re-read from the kubeconfig.";
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowReconnectFailureAsync(ex);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    // Set while the failure view stands for a reconnect of a tab that had connected (#272).
    private bool _reconnectFailed;

    /// <summary>
    /// A reconnect that failed (#272). Every step it runs — reading the kubeconfig, building
    /// the client, reaching the server — is one the first connect reports with the failure
    /// view, so it is reported the same way: the list's watch stops and the view takes the
    /// list's place. Before, the tab kept its old rows under a "Watch connection lost"
    /// warning that the informer went on retrying, and the explanation only appeared once
    /// the tab was closed and opened again. A watch that drops on its own is not this: the
    /// informer retries it, and the list keeps its warning (UI rule 9).
    /// </summary>
    internal async Task ShowReconnectFailureAsync(Exception ex)
    {
        StopWatch();
        _reconnectFailed = true;
        ConnectionFailure = new ConnectionFailureViewModel(await ConnectionReport.CreateAsync(Context, ex), this);
        Status = $"Reconnect failed ({ConnectionFailure.Report.StepPhrase}).";
        IsConnected = false;
        ApplyTransportNotices(null);

        // The Applications mode's watches go too (#294): the client they read through just
        // failed, and the failure view covers their rows. They start again from the
        // connection change a successful Reconnect makes (ApplicationsViewModel.TryStart).
        // After IsConnected is false, or the connection change would start them again here.
        Applications.Suspend();

        // And the shared watches behind both, kept or not: the next successful connect reads
        // everything again rather than resuming a cache the failed client filled.
        Client?.Watches.Clear();
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
        _routineStatus = DemoStatus;
        Status = DemoStatus;

        var catalog = Demo.DemoData.BuildCatalog();
        RecordPodDescriptor("", catalog);
        SidebarSections.Clear();
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
        RestoreRecentKinds();

        NamespaceOptions.Clear();
        NamespaceOptions.Add(AllNamespaces);
        foreach (var ns in Demo.DemoData.Namespaces)
        {
            NamespaceOptions.Add(ns);
        }

        _metricsApiAvailable = true;

        ApplyInitialView(fallbackNamespace: null);
    }

    /// <summary>
    /// Where a freshly connected tab lands: the kind and namespace it was left on when
    /// the workspace was saved, else Pods in all namespaces. The namespace choice is the
    /// tab's, shared with the Applications mode, which is the whole cluster at a glance, so
    /// a tab with nothing saved opens on the whole cluster in both. Only where listing
    /// namespaces was refused does it open on <paramref name="fallbackNamespace"/>, the
    /// context's own: under that RBAC the cluster-wide read is refused too, and the context's
    /// namespace is how kubectl copes.
    ///
    /// <para>
    /// The namespace is set before the kind, so the single RestartWatch that SelectKind
    /// triggers is the one that populates the rows — assigning it afterwards would clear
    /// them and latch IsListEmpty, the ordering gotcha CLAUDE.md documents for the
    /// screenshot fixtures.
    /// </para>
    /// </summary>
    internal void ApplyInitialView(string? fallbackNamespace)
    {
        var listingRefused = NamespaceOptions.Count <= 1 && !IsDemo;
        var @namespace = RestoreNamespace ?? (listingRefused ? fallbackNamespace : null);
        if (!string.IsNullOrEmpty(@namespace))
        {
            // A namespace the list does not contain is still a real answer when the list
            // itself was refused: plenty of RBAC setups grant a namespace but not the
            // right to enumerate namespaces, and the context's own namespace is exactly
            // how kubectl copes with that. When the list *was* read, a name missing from
            // it has been deleted, and opening on it would be an empty list that looks
            // like a broken watch — so it is only added in the first case.
            if (!NamespaceOptions.Contains(@namespace) && listingRefused)
            {
                NamespaceOptions.Add(@namespace);
            }

            if (NamespaceOptions.Contains(@namespace))
            {
                SelectedNamespace = @namespace;
            }

            // The others chosen with it, under the same rule: a name the namespace list was
            // read without has been deleted and is dropped; one it could not read is kept.
            if (RestoreNamespaces is { Count: > 1 } restored && SelectedNamespace == @namespace)
            {
                SetNamespaces(restored.Where(ns => NamespaceOptions.Contains(ns) || listingRefused));
            }
        }

        var kinds = SidebarSections.SelectMany(s => s.Kinds).Where(k => !k.IsRecentEntry).ToList();
        var kind = RestoreKindKey is { } key
            ? kinds.FirstOrDefault(k => GridLayoutStore.KeyFor(k.Descriptor) == key)
            : null;
        kind ??= SidebarSections
            .FirstOrDefault(s => s.Title == "Workloads")?.Kinds
            .FirstOrDefault(k => k.Descriptor.Kind == "Pod")
            ?? kinds.FirstOrDefault();
        if (kind is not null)
        {
            SelectKind(kind);
        }

        _viewStateReady = true;
    }

    // Pods has a stable core/v1 endpoint. A saved non-Pod kind still waits for discovery.
    internal bool StartInitialPods()
    {
        if (RestoreKindKey is not (null or "/Pod")) return false;
        if (!SidebarSections.SelectMany(s => s.Kinds).Any(k => k.Descriptor is { Group: "", Kind: "Pod" }))
        {
            var section = new SidebarSectionViewModel("Workloads");
            section.Kinds.Add(new SidebarKindViewModel(ResourceDescriptor.Pods, "workload"));
            SidebarSections.Insert(0, section);
        }
        ApplyInitialView(Context.Namespace);
        return true;
    }

    internal void ReconcileInitialPods()
    {
        // Assigning the discovered descriptor refreshes capabilities but does not call
        // SelectKind: that would clear rows and open the same watch a second time.
        if (SelectedKind is not { } selected) return;
        var replacement = SidebarSections.SelectMany(s => s.Kinds).FirstOrDefault(k => !k.IsRecentEntry
            && k.Descriptor.Group == selected.Descriptor.Group && k.Descriptor.Kind == selected.Descriptor.Kind);
        if (replacement is not null)
        {
            SelectedKind = replacement;
        }
        if (!IsFleetView && !AreMetricsVisible && _metricsApiAvailable && Client is { } client
            && _watchCts is { } cts && IsMeteredKind(SelectedKind.Descriptor))
        {
            AreMetricsVisible = true;
            StartMetricsPolling(SelectedKind.Descriptor, [("", client)], WatchNamespaces(SelectedKind.Descriptor), cts.Token);
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
            MarkNamespacesListed();
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
        // A Recent entry is a shortcut to a row the sidebar already has, so it selects that
        // row. SelectedKind is then always the canonical instance, which is what makes a
        // click on the Recent copy of the kind already showing the no-op that a click on
        // the row itself is, rather than a reload of the same list.
        var fromRecent = kind.IsRecentEntry;
        if (fromRecent && CanonicalKind(kind) is { } canonical)
        {
            kind = canonical;
        }

        if (SelectedKind == kind)
        {
            return;
        }

        // Not from a Recent click: reordering the section under the pointer that just
        // clicked it makes the section unusable.
        if (!fromRecent)
        {
            RecordRecentKind(kind);
        }

        // A name filter is a question about the list it was typed into: carrying
        // "nginx" from Pods over to ConfigMaps lands on an empty list that looks
        // like a broken watch.
        RowFilter = "";

        // The selected-row highlight follows from this, in OnSelectedKindChanged.
        SelectedKind = kind;

        if (kind.IsHelmReleases)
        {
            StopWatch();
            IsHelmView = true;
            IsArgoView = false;
            AreMetricsVisible = false;
            PrinterColumns = []; // Helm releases are not an API kind and have no CRD behind them.
            _ = Helm.RefreshAsync();
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

        _ = OpenRowAsync(new ResourceRowViewModel(resolved), preview: false, descriptor);
    }

    /// <summary>
    /// Plenty of clusters run without metrics-server. Probing once at connect
    /// (off the cached discovery catalog) keeps the usage columns out of the way
    /// entirely on those clusters instead of showing a column full of dashes.
    /// </summary>
    private async Task DetectMetricsApiAsync()
    {
        _metricsRefused.Clear();
        try
        {
            _metricsApiAvailable = Client is not null && await Client.IsMetricsApiAvailableAsync();
        }
        catch (Exception)
        {
            _metricsApiAvailable = false; // usage is supplementary; never fail the connect over it
        }
    }

    partial void OnSelectedNamespaceChanging(string value)
    {
        // A namespace chosen on its own — the picker's Enter, the palette, a reveal, a
        // restore — replaces any others chosen with the previous one. SetNamespaces sets
        // the others itself, around this.
        if (!_settingNamespaces)
        {
            _additionalNamespaces = [];
        }
    }

    partial void OnSelectedNamespaceChanged(string value) => OnNamespacesChanged();

    private void OnNamespacesChanged()
    {
        foreach (var ns in SelectedNamespaces.Reverse())
        {
            RememberNamespace(ns);
        }

        OnPropertyChanged(nameof(SelectedNamespaces));
        OnPropertyChanged(nameof(NamespaceDisplay));
        OnPropertyChanged(nameof(NamespaceButtonTip));
        UpdateNamespaceChecks();
        RaiseViewStateChanged();

        if (IsHelmView)
        {
            _ = Helm.RefreshAsync();
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

    /// <summary>Kinds metrics.k8s.io reports on. Everything else has no usage to show.</summary>
    private static bool IsMeteredKind(ResourceDescriptor descriptor) =>
        string.IsNullOrEmpty(descriptor.Group) && descriptor.Kind is "Pod" or "Node";

    /// <summary>The client that owns a row — its own cluster's in fleet mode, this tab's otherwise.</summary>
    private ClusterClient? ClientFor(ResourceRowViewModel row) => ClientForCluster(row.ClusterName);

    private ClusterClient? ClientForCluster(string clusterName) =>
        clusterName.Length > 0 && _fleetTargets.TryGetValue(clusterName, out var target)
            ? target.Member.Client
            : Client;

    /// <summary>
    /// The name a confirm uses for the cluster a row came from: the fleet member's name in an
    /// aggregated list (which is that tab's context name), this tab's context otherwise — the
    /// name the cluster switcher and the tab strip show.
    /// </summary>
    internal string ContextNameFor(string clusterName) => clusterName.Length > 0 ? clusterName : Context.Name;

    /// <summary>
    /// The environment of the cluster a row came from. In an aggregated list that is the
    /// row's <em>own</em> cluster's, read live from the shell's members (an override made
    /// since the fleet watch started counts), then from the watch's targets; a name nothing
    /// knows is classified by name, which leans toward production (cluster-switcher.md).
    /// </summary>
    internal ClusterEnvironment EnvironmentOf(string clusterName)
    {
        if (clusterName.Length == 0)
        {
            return Environment;
        }

        if (FleetMembersProvider?.Invoke().FirstOrDefault(m => m.ClusterName == clusterName) is { } member)
        {
            return member.Environment;
        }

        return _fleetTargets.TryGetValue(clusterName, out var target)
            ? target.Member.Environment
            : ClusterEnvironments.Classify(clusterName);
    }

    /// <summary>Remembers a cluster's core/v1 Pod descriptor, verbs and subresources included.</summary>
    private void RecordPodDescriptor(string clusterName, IReadOnlyList<ResourceDescriptor> catalog)
    {
        if (catalog.FirstOrDefault(d => d is { Group: "", Kind: "Pod" }) is { } pods)
        {
            _podDescriptors[clusterName] = pods;
        }

        RecordJobDescriptor(clusterName, catalog);
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

    public async ValueTask DisposeAsync()
    {
        await Applications.DisposeAsync();

        // A drain runs in this process and in this strip. Closing the tab stops it —
        // which is the honest behaviour and the one the confirm warned about, but it has
        // to be an explicit cancel rather than a task left running against a disposed
        // client.
        PendingRowAction?.CancelDrain();
        if (_logTargetsCts is not null)
        {
            await _logTargetsCts.CancelAsync();
            _logTargetsCts.Dispose();
            _logTargetsCts = null;
        }

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
