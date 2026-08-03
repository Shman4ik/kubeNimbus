using System.Collections.ObjectModel;
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

    /// <summary>metrics.k8s.io aggregates over a ~30s window, so polling faster than this buys nothing.</summary>
    private static readonly TimeSpan MetricsPollInterval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, ResourceRowViewModel> _rowsByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Which cluster served each row, in fleet mode — a row's client and descriptor
    /// have to come from its own cluster, not from this tab's, or opening a row from
    /// cluster B would apply YAML to cluster A. Empty outside fleet mode.
    /// </summary>
    private readonly Dictionary<string, FleetTarget> _fleetTargets = new(StringComparer.Ordinal);

    private CancellationTokenSource? _watchCts;
    private bool _metricsApiAvailable;

    public ClusterContext Context { get; }

    public string Header => Context.Name;

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

    public ObservableCollection<ResourceRowViewModel> Rows { get; } = [];

    /// <summary>True from the moment a watch (re)starts until its first event
    /// arrives — distinguishes "still loading" from "genuinely empty" so the
    /// list doesn't flash an empty state while the initial list is in flight.</summary>
    [ObservableProperty]
    private bool _isListLoading;

    /// <summary>True once the list has genuinely settled on zero rows (not
    /// merely mid-load) — drives the "No <kind> found" empty state.</summary>
    [ObservableProperty]
    private bool _isListEmpty;

    partial void OnIsListLoadingChanged(bool value) => RecomputeListEmpty();

    private void RecomputeListEmpty() => IsListEmpty = Rows.Count == 0 && !IsListLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPodRowSelected))]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenPreviousLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecIntoSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(PortForwardSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedYamlCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
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

        // Tabs already open have to follow the switch live — the alternative is a
        // force-apply button that stays on screen until the tab is reopened.
        foreach (var tab in InspectorTabs)
        {
            tab.IsAdvancedView = value;
        }

        AdvancedViewChanged?.Invoke(value);
    }

    /// <summary>
    /// The list's CPU/Memory columns (number + sparkline). Two conditions, not one:
    /// the cluster has to actually serve metrics.k8s.io for the metered kind
    /// (<see cref="AreMetricsVisible"/>), *and* the user has to have asked for the
    /// busier layout. Read by <see cref="Views.ClusterTabView"/>'s code-behind —
    /// a DataGridColumn is outside the visual tree and can't bind.
    /// </summary>
    public bool AreUsageColumnsVisible => AreMetricsVisible && IsAdvancedView;

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
    }

    private bool CanConnect => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
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

    private async Task BuildSidebarAsync()
    {
        if (Client is null)
        {
            return;
        }

        var catalog = await Client.GetResourceCatalogAsync();
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
        }
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
        if (Client is null)
        {
            return;
        }

        IsHelmLoading = true;
        IsHelmEmpty = false;
        HelmReleases.Clear();
        try
        {
            var @namespace = SelectedNamespace == AllNamespaces ? null : SelectedNamespace;
            foreach (var release in await Client.ListHelmReleasesAsync(@namespace))
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
        if (SelectedHelmRelease is not { } row || Client is null)
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

        Rows.Clear();
        _rowsByKey.Clear();
        _fleetTargets.Clear();
        IsListLoading = Client is not null && SelectedKind is not null;
        RecomputeListEmpty();

        if (Client is null || SelectedKind is null)
        {
            AreMetricsVisible = false;
            return;
        }

        var descriptor = SelectedKind.Descriptor;
        var @namespace = descriptor.Namespaced && SelectedNamespace != AllNamespaces ? SelectedNamespace : null;

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        var client = Client;

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

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _fleetTargets.Clear();
                    foreach (var target in targets)
                    {
                        _fleetTargets[target.Member.ClusterName] = target;
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

    private void Apply(ResourceEvent<DynamicResource> evt)
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
    /// </summary>
    private void ApplyFleet(FleetResourceEvent tagged)
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

    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private void ExecIntoSelected()
    {
        if (SelectedRow is not { } row || ClientFor(row) is not { } client)
        {
            return;
        }

        AddInspectorTab(new ExecTabViewModel(client, row.Namespace, row.Name, FirstContainerOf(row)));
    }

    [RelayCommand(CanExecute = nameof(IsPodRowSelected))]
    private void PortForwardSelected()
    {
        if (SelectedRow is not { } row || ClientFor(row) is not { } client)
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
        if (SelectedRow is not { } row
            || ClientFor(row) is not { } client
            || DescriptorFor(row) is not { } descriptor)
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

    /// <summary>
    /// Delete goes through the YAML editor's existing two-step confirm rather than
    /// deleting from the menu: a context-menu item that destroys an object on one
    /// click, with the object named nowhere, is not a gesture this app should have.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void DeleteSelected()
    {
        EditSelectedYaml();
        if (SelectedInspectorTab is YamlEditorTabViewModel yaml)
        {
            yaml.IsConfirmingDelete = true;
        }
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
        if (row is null || ClientFor(row) is not { } client || DescriptorFor(row) is not { } descriptor)
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

        var isPod = descriptor.Kind == "Pod";
        var key = isPod
            ? PodDetailTabViewModel.KeyFor(row.ClusterName, row.Namespace, row.Name)
            : YamlEditorTabViewModel.KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
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

        InspectorTabViewModelBase tab = isPod
            ? new PodDetailTabViewModel(
                client, row, AddInspectorTab,
                // Bound to this row's cluster so owner navigation stays on it.
                (owner, namespaceHint) => OpenOwnerAsync(owner, namespaceHint, row.ClusterName),
                row.ClusterName)
            : new YamlEditorTabViewModel(client, descriptor, row.Namespace, row.Name, row.Resource.ToYaml(), row.ClusterName);

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
