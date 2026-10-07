using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>Which narrowing chip is on. Exactly one is, like a segmented control.</summary>
public enum ApplicationChip
{
    All,
    NeedsAttention,
    RecentDeploy,
    NotInArgo,
}

/// <summary>
/// The Applications mode of one cluster tab: a list of applications — Argo CD Applications,
/// and workloads no Application tracks — with their health and a one-line reason, read live
/// through the same list+watch informer the Resources mode uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns its own watches and never touches the tab's.</b> Switching modes must not
/// restart anything, so the Resources list keeps its watch on whatever kind it shows and this
/// keeps one per kind it reads (Deployments, StatefulSets, DaemonSets, CronJobs, Jobs,
/// ReplicaSets, Pods, and Argo CD Applications when the cluster serves them). They start the
/// first time the mode is shown for this tab and run until the tab closes.
/// </para>
/// <para>
/// <b>Narrow RBAC is the expected case, not an error.</b> Each kind is first watched across
/// the cluster. A 403 there moves that kind to one watch per namespace the tab has a reason
/// to believe in (<see cref="ApplicationScope.CandidateNamespaces"/>), and a 403 in a
/// namespace is recorded against it. The list then says what it covers and what it was
/// refused — a silently partial list reads as "all is well" exactly where it is not looking.
/// </para>
/// <para>
/// <b>No verdict before the data (UI rule 18).</b> "No applications" is said only after every
/// watch has delivered its <c>Synced</c> frame (or been refused). Rows appear as soon as they
/// can be built, and while some kinds are still being read the scope line says which.
/// </para>
/// </remarks>
public sealed partial class ApplicationsViewModel : ObservableObject, IAsyncDisposable
{
    internal static readonly (string Group, string Kind, string Plural)[] WatchedKinds =
    [
        ("apps", "Deployment", "deployments"),
        ("apps", "StatefulSet", "statefulsets"),
        ("apps", "DaemonSet", "daemonsets"),
        ("batch", "CronJob", "cronjobs"),
        ("batch", "Job", "jobs"),
        ("apps", "ReplicaSet", "replicasets"),
        ("", "Pod", "pods"),
    ];

    private const string ArgoKind = "Application";

    private static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(30);

    private readonly ClusterTabViewModel _tab;
    private readonly Dictionary<string, Dictionary<string, DynamicResource>> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private List<ApplicationRowViewModel> _ordered = [];

    private readonly Dictionary<string, KindWatch> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _scopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly List<ArgoApplication> _argoApplications = [];

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _rebuildTimer;
    private DispatcherTimer? _clockTimer;
    private bool _wanted;
    private int _rebuildGeneration;

    public ApplicationsViewModel(ClusterTabViewModel tab)
    {
        _tab = tab;
        Clock = tab.IsDemo ? () => DemoData.Now : () => DateTimeOffset.UtcNow;
    }

    public ClusterTabViewModel Tab => _tab;

    public bool IsDemo => _tab.IsDemo;

    /// <summary>"Now" for every relative time and every time-based rule. The demo cluster reads its dataset at <see cref="DemoData.Now"/>.</summary>
    internal Func<DateTimeOffset> Clock { get; set; }

    /// <summary>
    /// False in the view-model tests, which apply watch events and read the rows back in the
    /// same breath. The app coalesces a burst of events into one rebuild off the UI thread.
    /// </summary>
    internal bool DeferRebuilds { get; set; } = true;

    public RangeObservableCollection<ApplicationRowViewModel> VisibleRows { get; } = [];

    /// <summary>Every row, sorted, before the chips and the search box narrow it.</summary>
    public IReadOnlyList<ApplicationRowViewModel> Rows => _ordered;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>True once the mode has been shown for this tab and its reads have begun.</summary>
    /// <remarks>
    /// The connection states read it too, and must be told. The tab sets <c>IsConnected</c>
    /// as soon as <c>/version</c> answers but stays connecting through namespaces, discovery
    /// and the metrics probe — seconds on a distant cluster signed in through SSO — so the
    /// reads start while the tab is still connecting. Without this, "Connecting to …" stayed
    /// on screen and the next state was drawn over it in the same cell.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(LoadingText), nameof(IsEmpty), nameof(IsConnecting), nameof(IsDisconnected), nameof(ShowsConnectionFailure))]
    private bool _hasStarted;

    /// <summary>
    /// The mode is on screen for this tab. The first call starts the reads (once the tab is
    /// connected); after that nothing stops them until the tab closes, so flipping to Resources
    /// and back costs nothing and loses nothing.
    /// </summary>
    public void Activate()
    {
        _wanted = true;
        TryStart();
    }

    /// <summary>Called by the tab whenever its connection state changes.</summary>
    internal void OnTabConnectionChanged()
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ShowsConnectionFailure));
        TryStart();
    }

    public bool IsConnecting => _tab.IsConnecting && !HasStarted;

    /// <summary>
    /// The tab is not connected, not trying, and has no failure to explain — the moment
    /// before its first connect starts. A failed connect is <see cref="ShowsConnectionFailure"/>.
    /// </summary>
    public bool IsDisconnected => !_tab.IsConnected && !_tab.IsConnecting && !HasStarted && !_tab.HasConnectionFailure;

    /// <summary>The tab's connect failed: the page shows the same failure view the Resources list does.</summary>
    public bool ShowsConnectionFailure => _tab.HasConnectionFailure && !_tab.IsConnecting && !HasStarted;

    private void TryStart()
    {
        if (!_wanted || HasStarted || !_tab.IsConnected)
        {
            return;
        }

        if (_tab.IsDemo)
        {
            HasStarted = true;
            LoadDemo();
            return;
        }

        if (_tab.Client is { } client)
        {
            // Before HasStarted, whose notification is what the view reads the states on.
            _readingCatalog = true;
            HasStarted = true;
            _ = StartAsync(client);
        }
    }

    /// <summary>
    /// The catalog is being read and no watch has started yet. Nothing is pending in that
    /// gap, so without this the list read as empty — "No applications found" — for as long
    /// as discovery took, which on a distant cluster is seconds (UI rule 18).
    /// </summary>
    private bool _readingCatalog;

    /// <summary>
    /// The demo cluster's "watches": the shipped dataset, poured through the same store, the
    /// same catalog and the same rules a live cluster's events go through (demo rule 4).
    /// </summary>
    private void LoadDemo()
    {
        foreach (var (_, kind, _) in WatchedKinds)
        {
            foreach (var resource in DemoData.OfKind(kind))
            {
                Store(kind)[resource.Key] = resource;
            }
        }

        foreach (var application in DemoData.ArgoApplicationObjects)
        {
            Store(ArgoKind)[application.Key] = application;
        }

        ScopeText = "All namespaces";
        RebuildNow();
    }

    private async Task StartAsync(ClusterClient client)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IReadOnlyList<ResourceDescriptor> catalog;
        try
        {
            catalog = await client.GetResourceCatalogAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Discovery failing is the tab's to report; the built-in kinds all have stable
            // versions, so the list can still be read without it. Argo cannot — its version
            // is never assumed.
            catalog = [];
        }
        finally
        {
            _readingCatalog = false;
        }

        foreach (var (group, kind, plural) in WatchedKinds)
        {
            var descriptor = catalog.FirstOrDefault(d => d.Group == group && d.Kind == kind)
                ?? (catalog.Count == 0 ? WellKnown(group, kind, plural) : null);
            if (descriptor is not null)
            {
                _kinds[kind] = new KindWatch(descriptor);
                StartScope(client, kind, @namespace: null);
            }
        }

        if (ArgoCd.ApplicationDescriptor(catalog) is { } argo)
        {
            _kinds[ArgoKind] = new KindWatch(argo);
            StartScope(client, ArgoKind, @namespace: null);
        }

        _clockTimer = new DispatcherTimer { Interval = ClockTick };
        _clockTimer.Tick += (_, _) => ScheduleRebuild();
        _clockTimer.Start();
        UpdateScope();

        // The scopes just started are pending; say so now rather than on the first event.
        RefreshStates();
    }

    private static ResourceDescriptor WellKnown(string group, string kind, string plural) =>
        kind == "Pod"
            ? ResourceDescriptor.Pods
            : new ResourceDescriptor(group, "v1", kind, plural, kind.ToLowerInvariant(), true, [], []);

    private static string ScopeId(string kind, string? @namespace) => $"{kind}@{@namespace ?? "*"}";

    private void StartScope(ClusterClient client, string kind, string? @namespace)
    {
        if (_cts is null || !_kinds.TryGetValue(kind, out var watch))
        {
            return;
        }

        var id = ScopeId(kind, @namespace);
        if (_scopes.ContainsKey(id))
        {
            return;
        }

        if (@namespace is not null)
        {
            watch.Namespaces.Add(@namespace);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _scopes[id] = cts;
        _pending.Add(id);
        var token = cts.Token;
        var descriptor = watch.Descriptor;

        _ = Task.Run(async () =>
        {
            try
            {
                // One awaited hop per batch of events (AsyncBatching), where this used to post
                // one unawaited job per event: a cluster-wide initial list (5,000 pods and
                // 10,000 ReplicaSets) queued 15,000 jobs ahead of input and rendering.
                await foreach (var batch in client.WatchResourceAsync(
                    descriptor,
                    @namespace,
                    connectionLost: ex => OnConnectionLost(client, kind, @namespace, ex, cts),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        foreach (var evt in batch)
                        {
                            Apply(kind, evt, @namespace);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // closed, or moved to per-namespace watches
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Refuse(client, kind, @namespace, FirstLine(ex.Message)));
            }
        }, token);
    }

    private void OnConnectionLost(ClusterClient client, string kind, string? @namespace, Exception ex, CancellationTokenSource cts)
    {
        // A 403 on the list is a KubernetesApiException carrying the server's sentence; on
        // the watch request itself (list allowed, watch not) it is a bare HttpRequestException.
        if (ex.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } forbidden)
        {
            // Not a connection problem and not worth a retry loop: stop this watch and say so.
            cts.Cancel();
            var reason = (forbidden as KubernetesApiException)?.ServerMessage ?? forbidden.Message;
            Dispatcher.UIThread.Post(() => Refuse(client, kind, @namespace, reason));
            return;
        }

        Dispatcher.UIThread.Post(() => ConnectionWarning = ex.Message);
    }

    /// <summary>
    /// A watch was refused. Across the cluster, the kind moves to per-namespace watches; in a
    /// namespace, the refusal is recorded and stated.
    /// </summary>
    internal void Refuse(ClusterClient? client, string kind, string? @namespace, string reason)
    {
        _pending.Remove(ScopeId(kind, @namespace));
        if (!_kinds.TryGetValue(kind, out var watch))
        {
            watch = _kinds[kind] = new KindWatch(null);
        }

        if (@namespace is null)
        {
            watch.ClusterWide = false;
            watch.ClusterRefusal = reason;
            if (client is not null)
            {
                foreach (var candidate in CandidateNamespaces(kind))
                {
                    StartScope(client, kind, candidate);
                }
            }
        }
        else
        {
            watch.Refused[@namespace] = reason;
        }

        UpdateScope();
        RefreshStates();
    }

    private IReadOnlyList<string> CandidateNamespaces(string kind)
    {
        var selected = _tab.SelectedNamespace == ClusterTabViewModel.AllNamespaces ? null : _tab.SelectedNamespace;
        var candidates = ApplicationScope.CandidateNamespaces(
            _tab.Context.Namespace, _tab.RecentNamespaceNames, _argoApplications, selected).ToList();

        // Applications live in Argo's own namespace, which no workload names.
        if (kind == ArgoKind && !candidates.Contains("argocd"))
        {
            candidates.Add("argocd");
        }

        return candidates;
    }

    /// <summary>New Argo destinations are new reasons to look — extend every per-namespace kind to them.</summary>
    private void ExtendFallbacks()
    {
        if (_tab.Client is not { } client)
        {
            return;
        }

        foreach (var (kind, watch) in _kinds)
        {
            if (watch.ClusterWide == false)
            {
                foreach (var candidate in CandidateNamespaces(kind))
                {
                    StartScope(client, kind, candidate);
                }
            }
        }
    }

    // --------------------------------------------------------------- the store

    private Dictionary<string, DynamicResource> Store(string kind)
    {
        if (!_objects.TryGetValue(kind, out var store))
        {
            store = _objects[kind] = new Dictionary<string, DynamicResource>(StringComparer.Ordinal);
        }

        return store;
    }

    /// <summary>
    /// Applies one informer event. A Reset clears only what its own scope had seeded (one
    /// namespace's watch relisting must not blank the rest), and marks the scope pending
    /// again — a 410 relist is a load too. Synced is what ends it.
    /// </summary>
    internal void Apply(string kind, ResourceEvent<DynamicResource> evt, string? @namespace = null)
    {
        var store = Store(kind);
        var scope = ScopeId(kind, @namespace);
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                foreach (var key in store.Where(kv => @namespace is null || kv.Value.Namespace == @namespace).Select(kv => kv.Key).ToList())
                {
                    store.Remove(key);
                }

                _pending.Add(scope);
                if (_kinds.TryGetValue(kind, out var reset) && @namespace is null)
                {
                    reset.ClusterWide ??= true;
                }

                break;

            case ResourceEventType.Synced:
                // A reconnect ends in a relist, and the relist ends here.
                ConnectionWarning = null;
                _pending.Remove(scope);
                if (_kinds.TryGetValue(kind, out var synced) && @namespace is null)
                {
                    synced.ClusterWide = true;
                }

                UpdateScope();
                break;

            case ResourceEventType.Added:
            case ResourceEventType.Modified:
                if (evt.Resource is { } resource)
                {
                    store[resource.Key] = resource;
                }

                break;

            case ResourceEventType.Deleted:
                if (evt.Resource is { } deleted)
                {
                    store.Remove(deleted.Key);
                }

                break;
        }

        if (DeferRebuilds)
        {
            ScheduleRebuild();
        }
        else
        {
            RebuildNow();
        }
    }

    /// <summary>
    /// The narrow-RBAC state, written in: which kinds were refused across the cluster, the
    /// namespaces they fell back to, and what was refused there too. The screenshot harness
    /// and the tests use it; a demo or fixture tab has no API server to refuse anything.
    /// </summary>
    internal void SetFallbackForFixture(
        IReadOnlyList<(string Kind, string Plural)> kinds,
        IReadOnlyList<string> namespaces,
        IReadOnlyList<(string Kind, string Namespace, string Reason)> refused)
    {
        foreach (var (kind, plural) in kinds)
        {
            var watch = _kinds[kind] = new KindWatch(new ResourceDescriptor("", "v1", kind, plural, kind.ToLowerInvariant(), true, [], []))
            {
                ClusterWide = false,
            };
            foreach (var ns in namespaces)
            {
                watch.Namespaces.Add(ns);
            }
        }

        foreach (var (kind, ns, reason) in refused)
        {
            if (_kinds.TryGetValue(kind, out var watch))
            {
                watch.Refused[ns] = reason;
            }
        }

        UpdateScope();
        RefreshStates();
    }

    /// <summary>Marks a scope as being read — what the tests use to stand for a watch that has started.</summary>
    internal void MarkPending(string kind, string? @namespace = null)
    {
        HasStarted = true;
        _pending.Add(ScopeId(kind, @namespace));
        RefreshStates();
    }

    private void ScheduleRebuild()
    {
        if (_rebuildTimer is null)
        {
            _rebuildTimer = new DispatcherTimer { Interval = RebuildDelay };
            _rebuildTimer.Tick += (_, _) =>
            {
                _rebuildTimer!.Stop();
                _ = RebuildInBackgroundAsync();
            };
        }

        if (!_rebuildTimer.IsEnabled)
        {
            _rebuildTimer.Start();
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DynamicResource, ArgoApplication> _parsedArgo = [];

    private ClusterSnapshot TakeSnapshot()
    {
        IReadOnlyList<DynamicResource> Of(string kind) => _objects.TryGetValue(kind, out var s) ? [.. s.Values] : [];

        // Parsed once per object version, not once per rebuild: this runs on the UI thread
        // every time the list rebuilds (up to five times a second under churn), and reading
        // an Application walks its whole status.resources. A watch update replaces the
        // DynamicResource, so a changed Application is a new key and is parsed again.
        var argo = Of(ArgoKind);
        return new ClusterSnapshot(
            [.. argo.Select(resource => _parsedArgo.GetValue(resource, ArgoCd.ReadApplication))],
            [.. Of("Deployment"), .. Of("StatefulSet"), .. Of("DaemonSet"), .. Of("CronJob"), .. Of("Job")],
            Of("ReplicaSet"),
            Of("Job"),
            Of("Pod"),
            Clock());
    }

    private static List<(ApplicationEntry Entry, ApplicationAssessment Assessment)> Assess(ClusterSnapshot snapshot) =>
        [.. ApplicationCatalog.Build(snapshot).Select(e => (e, ApplicationRules.Evaluate(e.Input)))];

    /// <summary>Rebuilds synchronously — the tests' path, and the demo's.</summary>
    internal void RebuildNow()
    {
        var snapshot = TakeSnapshot();
        ApplyAssessments(snapshot, Assess(snapshot));
    }

    private async Task RebuildInBackgroundAsync()
    {
        var generation = ++_rebuildGeneration;
        var snapshot = TakeSnapshot();
        var assessed = await Task.Run(() => Assess(snapshot));
        if (generation == _rebuildGeneration)
        {
            ApplyAssessments(snapshot, assessed);
        }
    }

    /// <summary>The snapshot the rows were last built from — what the application page reads.</summary>
    internal ClusterSnapshot? LastSnapshot { get; private set; }

    /// <summary>Raised after every rebuild, so an open application page can refresh from the same snapshot.</summary>
    public event EventHandler? Rebuilt;

    private void ApplyAssessments(ClusterSnapshot snapshot, List<(ApplicationEntry Entry, ApplicationAssessment Assessment)> assessed)
    {
        LastSnapshot = snapshot;
        var argoBefore = _argoApplications.Select(a => a.DestinationNamespace).ToHashSet(StringComparer.Ordinal);
        _argoApplications.Clear();
        _argoApplications.AddRange(snapshot.ArgoApplications);

        var now = snapshot.Now;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ApplicationRowViewModel>(assessed.Count);
        foreach (var (entry, assessment) in assessed)
        {
            seen.Add(entry.Key);
            if (_rowsByKey.TryGetValue(entry.Key, out var row))
            {
                row.Update(entry, assessment, now);
            }
            else
            {
                row = _rowsByKey[entry.Key] = new ApplicationRowViewModel(entry, assessment, now);
            }

            rows.Add(row);
        }

        foreach (var gone in _rowsByKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rowsByKey.Remove(gone);
        }

        _ordered = [.. rows
            .OrderBy(r => r.Status)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal)];

        RefreshVisible();

        if (!_argoApplications.Select(a => a.DestinationNamespace).ToHashSet(StringComparer.Ordinal).SetEquals(argoBefore))
        {
            ExtendFallbacks();
        }

        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------- scope and state

    private sealed class KindWatch(ResourceDescriptor? descriptor)
    {
        public ResourceDescriptor Descriptor { get; } = descriptor!;

        /// <summary>Null while the cluster-wide attempt is in flight; false once it was refused.</summary>
        public bool? ClusterWide { get; set; }

        public string? ClusterRefusal { get; set; }

        public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Refused { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What the list covers: "All namespaces", or the namespaces it fell back to.</summary>
    [ObservableProperty]
    private string _scopeText = "";

    /// <summary>What it was refused, in the API server's words. Null when nothing was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeWarning))]
    private string? _scopeWarning;

    public bool HasScopeWarning => ScopeWarning is not null;

    /// <summary>A watch lost its connection and is retrying; the list may be stale meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionWarning))]
    private string? _connectionWarning;

    public bool HasConnectionWarning => ConnectionWarning is not null;

    /// <summary>True when the list fell back to namespaces and has none it may read.</summary>
    [ObservableProperty]
    private bool _hasNoReadableScope;

    /// <summary>
    /// The reads fell back to namespaces. Only then does the title line state the scope: in
    /// the routine case it read "All namespaces" beside a namespace picker that said the
    /// same (UI rule 20).
    /// </summary>
    [ObservableProperty]
    private bool _isScopeNarrowed;

    private void UpdateScope()
    {
        var narrowed = _kinds.Where(k => k.Value.ClusterWide == false).ToList();
        IsScopeNarrowed = narrowed.Count > 0;
        if (narrowed.Count == 0)
        {
            ScopeText = "All namespaces";
            ScopeWarning = null;
            HasNoReadableScope = false;
            return;
        }

        var readable = narrowed
            .SelectMany(k => k.Value.Namespaces.Where(ns => !k.Value.Refused.ContainsKey(ns)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        HasNoReadableScope = readable.Count == 0 && narrowed.All(k => k.Value.Namespaces.Count > 0 || !_pending.Any());
        ScopeText = readable.Count switch
        {
            0 => "No namespace readable",
            1 => $"Namespace {readable[0]}",
            <= 4 => $"{readable.Count} namespaces: {string.Join(", ", readable)}",
            _ => $"{readable.Count} namespaces: {string.Join(", ", readable.Take(3))} +{readable.Count - 3}",
        };

        var kinds = string.Join(", ", narrowed.Select(k => k.Value.Descriptor?.Plural ?? k.Key.ToLowerInvariant()));
        var refused = narrowed
            .SelectMany(k => k.Value.Refused.Keys.Select(ns => $"{k.Value.Descriptor?.Plural ?? k.Key} in {ns}"))
            .ToList();
        var warning = $"Listing {kinds} across the cluster was refused, so only the namespaces this tab knows are read"
            + (readable.Count == 0
                ? narrowed.All(k => k.Value.Namespaces.Count == 0)
                    ? " — and it knows none. Set a namespace on the kubeconfig context, or pick one in Resources mode."
                    : "."
                : ".");
        if (refused.Count > 0)
        {
            warning += " Also refused: " + string.Join("; ", refused.Take(4)) + (refused.Count > 4 ? $"; +{refused.Count - 4} more" : "") + ".";
        }

        ScopeWarning = warning;
    }

    /// <summary>The kinds still being read, as the loading state names them.</summary>
    public string PendingText
    {
        get
        {
            var kinds = _pending.Select(p => p[..p.IndexOf('@')])
                .Distinct(StringComparer.Ordinal)
                .Select(KindTitle)
                .ToList();
            return kinds.Count == 0 ? "" : string.Join(", ", kinds);
        }
    }

    private static string KindTitle(string kind) =>
        kind == ArgoKind
            ? "Argo CD Applications"
            : WatchedKinds.FirstOrDefault(w => w.Kind == kind) is { Plural.Length: > 0 } known ? PluralTitle(known.Plural) : kind;

    private static string PluralTitle(string plural) =>
        plural switch
        {
            "statefulsets" => "StatefulSets",
            "daemonsets" => "DaemonSets",
            "cronjobs" => "CronJobs",
            "replicasets" => "ReplicaSets",
            _ => plural.Length == 0 ? plural : char.ToUpperInvariant(plural[0]) + plural[1..],
        };

    /// <summary>Waiting, with nothing to show yet (UI rule 18): names what it is waiting for.</summary>
    public bool IsLoading => HasStarted && (_readingCatalog || _pending.Count > 0) && _ordered.Count == 0;

    public string LoadingText => _pending.Count == 0 && _readingCatalog
        ? "Reading the cluster's resource types…"
        : $"Reading {PendingText}…";

    /// <summary>Rows are on screen and some kinds are still arriving — said in the scope line, not as a spinner over the rows.</summary>
    public bool IsPartiallyLoaded => _pending.Count > 0 && _ordered.Count > 0;

    public string StillReadingText => $"Still reading {PendingText}…";

    /// <summary>The verdict "there are no applications", given only once every read has answered.</summary>
    public bool IsEmpty => HasStarted && !_readingCatalog && _pending.Count == 0 && _ordered.Count == 0;

    /// <summary>Rows exist, and the chips or the search hid all of them — a different state with a different way out.</summary>
    public bool IsFilterEmpty => _ordered.Count > 0 && VisibleRows.Count == 0;

    public string FilterEmptyText
    {
        get
        {
            var where = IsNamespaceSelected ? $" {SelectionPhrase}" : "";
            return Filter.Trim().Length > 0
                ? $"Nothing matches “{Filter.Trim()}”{where}"
                : Chip switch
                {
                    ApplicationChip.NeedsAttention => $"Nothing needs attention{where}",
                    ApplicationChip.RecentDeploy => $"Nothing was deployed in the last hour{where}",
                    ApplicationChip.NotInArgo => $"Every application{(IsNamespaceSelected ? where : " here")} is in Argo CD",
                    _ => IsNamespaceSelected
                        ? $"No applications {SelectionPhrase}"
                        : "Every application here is in a kube-* namespace",
                };
        }
    }

    public string FilterEmptyDetail => $"{_ordered.Count} application{(_ordered.Count == 1 ? "" : "s")} in the list";

    private void RefreshStates()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(IsPartiallyLoaded));
        OnPropertyChanged(nameof(StillReadingText));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(FilterEmptyText));
        OnPropertyChanged(nameof(FilterEmptyDetail));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnected));
    }

    // ------------------------------------------------------ chips and search

    /// <summary>The search box. Matches name and namespace; never status (UI rule 13).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    private string _filter = "";

    public bool IsFiltering => Filter.Length > 0;

    partial void OnFilterChanged(string value) => RefreshVisible();

    [ObservableProperty]
    private ApplicationChip _chip;

    partial void OnChipChanged(ApplicationChip value)
    {
        OnPropertyChanged(nameof(IsAllChip));
        OnPropertyChanged(nameof(IsAttentionChip));
        OnPropertyChanged(nameof(IsRecentDeployChip));
        OnPropertyChanged(nameof(IsNotInArgoChip));
        RefreshVisible();
    }

    // Two-way IsChecked targets for the chip row (UI rule 8b: no command beside them). The
    // chips are RadioChips, which a click never unchecks; a false written here anyway is
    // ignored, so one chip is always on, like a segmented control.
    public bool IsAllChip { get => Chip == ApplicationChip.All; set => SetChip(ApplicationChip.All, value, nameof(IsAllChip)); }

    public bool IsAttentionChip { get => Chip == ApplicationChip.NeedsAttention; set => SetChip(ApplicationChip.NeedsAttention, value, nameof(IsAttentionChip)); }

    public bool IsRecentDeployChip { get => Chip == ApplicationChip.RecentDeploy; set => SetChip(ApplicationChip.RecentDeploy, value, nameof(IsRecentDeployChip)); }

    public bool IsNotInArgoChip { get => Chip == ApplicationChip.NotInArgo; set => SetChip(ApplicationChip.NotInArgo, value, nameof(IsNotInArgoChip)); }

    private void SetChip(ApplicationChip chip, bool on, string property)
    {
        if (on)
        {
            Chip = chip;
        }
        else
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>Show applications whose every namespace is <c>kube-*</c>. Off by default; the chip states the count.</summary>
    [ObservableProperty]
    private bool _showSystemNamespaces;

    partial void OnShowSystemNamespacesChanged(bool value) => RefreshVisible();

    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private int _recentDeployCount;
    [ObservableProperty] private int _notInArgoCount;

    /// <summary>
    /// Whether any application here is an Argo CD one. Without one the Sync column is a
    /// column of dashes and "Not in Argo CD" is the same list as "All", so the view drops
    /// both.
    /// </summary>
    [ObservableProperty] private bool _hasArgo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSystem), nameof(SystemChipText))]
    private int _systemCount;

    public bool HasSystem => SystemCount > 0;

    public string SystemChipText => $"kube-* · {SystemCount}";

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    /// <summary>Clears every narrowing — the empty state's way back.</summary>
    [RelayCommand]
    private void ShowEverything()
    {
        Filter = "";
        Chip = ApplicationChip.All;
        SetNamespaces([]);
        ShowSystemNamespaces = true;
    }

    // ------------------------------------------------------------- namespace

    public const string AllNamespaces = ClusterTabViewModel.AllNamespaces;

    private readonly SortedSet<string> _selectedNamespaces = new(StringComparer.Ordinal);

    /// <summary>
    /// The namespaces the list is narrowed to; empty means every namespace. Its own, not the
    /// tab's: the Resources list's namespace is often the kubeconfig context's, and this
    /// list's job is the whole cluster at a glance, so it opens on every namespace whatever
    /// the other mode shows. Session state, like the chips. It narrows what was read and
    /// starts no watch: under narrow RBAC the reads already cover every namespace the tab
    /// knows of.
    /// </summary>
    public IReadOnlyCollection<string> SelectedNamespaces => _selectedNamespaces;

    public bool IsNamespaceSelected => _selectedNamespaces.Count > 0;

    /// <summary>
    /// What the picker's button reads: All namespaces, the one chosen, two by name, or a
    /// count past that — the names are in the picker and in the tooltip.
    /// </summary>
    public string NamespaceButtonText => _selectedNamespaces.Count switch
    {
        0 => AllNamespaces,
        1 or 2 => string.Join(", ", _selectedNamespaces),
        var n => $"{n} namespaces",
    };

    public string NamespaceButtonTip => _selectedNamespaces.Count > 2
        ? $"Namespaces: {string.Join(", ", _selectedNamespaces)}. Click to change."
        : $"Choose a namespace: type to filter, click a row for it alone, its box or {Hotkeys.PrimaryLabel}+click to add it";

    /// <summary>How an empty state names the selection: "in payments", "in the 3 chosen namespaces".</summary>
    private string SelectionPhrase => _selectedNamespaces.Count == 1
        ? $"in {_selectedNamespaces.Min}"
        : $"in the {_selectedNamespaces.Count} chosen namespaces";

    /// <summary>Narrows to exactly these namespaces; none means every namespace.</summary>
    public void SetNamespaces(IEnumerable<string> namespaces)
    {
        var wanted = namespaces.Where(n => n.Length > 0 && n != AllNamespaces).ToHashSet(StringComparer.Ordinal);
        if (_selectedNamespaces.SetEquals(wanted))
        {
            return;
        }

        _selectedNamespaces.Clear();
        _selectedNamespaces.UnionWith(wanted);
        OnNamespacesChanged();
    }

    /// <summary>A click on a picker row: adds or removes that namespace; All namespaces clears the selection.</summary>
    internal void ToggleNamespace(ApplicationNamespaceChoice choice)
    {
        if (choice.IsAll)
        {
            SetNamespaces([]);
            return;
        }

        if (!_selectedNamespaces.Remove(choice.Name))
        {
            _selectedNamespaces.Add(choice.Name);
        }

        OnNamespacesChanged();
    }

    /// <summary>Enter on a picker row: that namespace and no other, the single-namespace gesture.</summary>
    internal void ChooseOnly(ApplicationNamespaceChoice choice) =>
        SetNamespaces(choice.IsAll ? [] : [choice.Name]);

    private void OnNamespacesChanged()
    {
        OnPropertyChanged(nameof(SelectedNamespaces));
        OnPropertyChanged(nameof(IsNamespaceSelected));
        OnPropertyChanged(nameof(NamespaceButtonText));
        OnPropertyChanged(nameof(NamespaceButtonTip));

        // In place, so the row the keyboard is on keeps its focus while it is toggled.
        foreach (var choice in NamespaceChoices)
        {
            choice.IsChecked = choice.IsAll ? _selectedNamespaces.Count == 0 : _selectedNamespaces.Contains(choice.Name);
        }

        RefreshVisible();
    }

    /// <summary>The picker's search box.</summary>
    [ObservableProperty]
    private string _namespaceFilter = "";

    partial void OnNamespaceFilterChanged(string value) => RebuildNamespaceChoices();

    [ObservableProperty]
    private ApplicationNamespaceChoice? _namespaceCandidate;

    /// <summary>
    /// The picker's rows: All namespaces, then every namespace an application runs in, with
    /// how many do. Taken from the rows rather than from a namespace list, so the picker
    /// offers only what narrows to something, and works where RBAC refuses
    /// <c>list namespaces</c>. A kube-* namespace is offered even while the kube-* chip hides
    /// its applications: choosing it is asking for them.
    /// </summary>
    public RangeObservableCollection<ApplicationNamespaceChoice> NamespaceChoices { get; } = [];

    public bool NoNamespaceMatches => NamespaceChoices.Count == 0;

    /// <summary>Rebuilt when the picker opens and as its search is typed, not on every watch tick.</summary>
    internal void RebuildNamespaceChoices()
    {
        var query = NamespaceFilter.Trim();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _ordered)
        {
            foreach (var ns in row.Namespaces.Distinct(StringComparer.Ordinal))
            {
                counts[ns] = counts.GetValueOrDefault(ns) + 1;
            }
        }

        // A namespace whose last application went away stays in the picker while it is
        // chosen, so the picker can always show, and undo, what the list is narrowed to.
        foreach (var ns in _selectedNamespaces)
        {
            counts.TryAdd(ns, 0);
        }

        var choices = new List<ApplicationNamespaceChoice>();
        if (AllNamespaces.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new(AllNamespaces, _ordered.Count, isAll: true) { IsChecked = _selectedNamespaces.Count == 0 });
        }

        choices.AddRange(counts
            .Where(kv => kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ApplicationNamespaceChoice(kv.Key, kv.Value) { IsChecked = _selectedNamespaces.Contains(kv.Key) }));

        NamespaceChoices.ReplaceAll(choices);

        // With a query, the first namespace that matches, so Enter takes what was typed; with
        // none, the first chosen row, where the reader left off.
        NamespaceCandidate = query.Length == 0
            ? NamespaceChoices.FirstOrDefault(c => c.IsChecked) ?? NamespaceChoices.FirstOrDefault()
            : NamespaceChoices.FirstOrDefault(c => !c.IsAll) ?? NamespaceChoices.FirstOrDefault();
        OnPropertyChanged(nameof(NoNamespaceMatches));
    }

    // ------------------------------------------------------------------ sort

    /// <summary>
    /// The column a header click sorted by, or null for the list's own order: urgency, then
    /// name, with the two groups captioned. Session state, like the chips.
    /// </summary>
    [ObservableProperty]
    private ApplicationSortColumn? _sortColumn;

    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>
    /// A header click: ascending, then descending, then back to the list's own order — the
    /// resource grid's three states, for the same reason: two would leave no way back.
    /// </summary>
    [RelayCommand]
    private void SortBy(ApplicationSortColumn column)
    {
        if (SortColumn != column)
        {
            SortColumn = column;
            SortDescending = false;
        }
        else if (!SortDescending)
        {
            SortDescending = true;
        }
        else
        {
            SortColumn = null;
            SortDescending = false;
        }

        foreach (var name in HeaderProperties)
        {
            OnPropertyChanged(name);
        }

        RefreshVisible();
    }

    private static readonly string[] HeaderProperties =
    [
        nameof(HealthHeader), nameof(NameHeader), nameof(NamespaceHeader), nameof(PodsHeader),
        nameof(RestartsHeader), nameof(LastDeployHeader), nameof(SyncHeader),
    ];

    // The arrow is drawn into the header text, as the resource grid draws it.
    private string Header(ApplicationSortColumn column, string label) =>
        SortColumn == column ? label + (SortDescending ? " ↓" : " ↑") : label;

    public string HealthHeader => Header(ApplicationSortColumn.Health, "Health");

    public string NameHeader => Header(ApplicationSortColumn.Name, "Application");

    public string NamespaceHeader => Header(ApplicationSortColumn.Namespace, "Namespace");

    public string PodsHeader => Header(ApplicationSortColumn.Pods, "Pods");

    public string RestartsHeader => Header(ApplicationSortColumn.Restarts, "Restarts");

    public string LastDeployHeader => Header(ApplicationSortColumn.LastDeploy, "Last deploy");

    public string SyncHeader => Header(ApplicationSortColumn.Sync, "Sync");

    /// <summary>
    /// The group captions earn their row while the two groups are contiguous: in the list's
    /// own order and sorted by Health either way. Sorted by anything else the groups
    /// interleave, and a caption would head a group that is not under it.
    /// </summary>
    private bool IsGroupedOrder => SortColumn is null or ApplicationSortColumn.Health;

    private bool PassesChip(ApplicationRowViewModel row) => Chip switch
    {
        ApplicationChip.NeedsAttention => row.NeedsAttention,
        ApplicationChip.RecentDeploy => row.IsRecentDeploy,
        ApplicationChip.NotInArgo => !row.IsArgo,
        _ => true,
    };

    private void RefreshVisible()
    {
        var query = Filter.Trim();

        // A chosen namespace is a deliberate ask, so it shows that namespace's applications
        // whether or not it is a kube-* one, and the kube-* chip, which would do nothing, goes.
        // An application in several namespaces is shown when any of them is chosen.
        var chosen = IsNamespaceSelected;
        var inScope = !chosen
            ? _ordered.Where(r => ShowSystemNamespaces || !r.IsSystem).ToList()
            : _ordered.Where(r => r.Namespaces.Any(_selectedNamespaces.Contains)).ToList();
        AllCount = inScope.Count;
        AttentionCount = inScope.Count(r => r.NeedsAttention);
        RecentDeployCount = inScope.Count(r => r.IsRecentDeploy);
        NotInArgoCount = inScope.Count(r => !r.IsArgo);
        SystemCount = chosen ? 0 : _ordered.Count(r => r.IsSystem);
        HasArgo = _ordered.Any(r => r.IsArgo);

        var target = inScope.Where(r => PassesChip(r) && r.Matches(query)).ToList();

        // A header sort is maintained, not applied once: every rebuild orders the rows
        // again, so a row whose sorted value changes on a watch event moves with it.
        // List.Sort is not stable, which the comparer's name-and-key tie-break makes moot.
        if (SortColumn is { } column)
        {
            target.Sort(new ApplicationRowComparer(column, SortDescending));
        }

        var attention = target.Count(r => r.NeedsAttention);
        var rest = target.Count - attention;
        // A heading earns its row only when it separates two groups. With one group on
        // screen — the Needs attention chip, or a healthy cluster — it repeated what the
        // chip above already says, count included. Sorted by a column other than Health
        // the groups interleave, and there is nothing for a heading to head.
        var grouped = attention > 0 && rest > 0 && IsGroupedOrder;
        var firstAttention = grouped;
        var firstRest = grouped;
        foreach (var row in target)
        {
            if (row.NeedsAttention)
            {
                row.GroupHeader = firstAttention ? $"NEEDS ATTENTION · {attention}" : null;
                firstAttention = false;
            }
            else
            {
                row.GroupHeader = firstRest ? $"EVERYTHING ELSE · {rest}" : null;
                firstRest = false;
            }
        }

        var before = SelectedRow;
        Sync(VisibleRows, target);

        // A wholesale change is one Reset (see Sync), and a list answers a Reset by clearing
        // its selection; put it back while the row is still in the list.
        SelectedRow = before is not null && target.Contains(before) ? before : null;

        RefreshStates();
    }

    /// <summary>
    /// Brings <paramref name="target"/>'s order into <paramref name="collection"/> by moves,
    /// inserts and removes, so a watch event that changes one row moves one row. When more
    /// than <see cref="ClusterTabViewModel.InPlaceResortLimit"/> rows come or go — a search
    /// keystroke that narrows 2,000 rows to 20 — a <see cref="RangeObservableCollection{T}"/>
    /// is replaced in one Reset instead of a notification per row; the caller restores the
    /// selection.
    /// </summary>
    internal static void Sync<T>(ObservableCollection<T> collection, IReadOnlyList<T> target) where T : class
    {
        var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        if (collection is RangeObservableCollection<T> range)
        {
            var present = new HashSet<T>(collection, ReferenceEqualityComparer.Instance);
            var churn = collection.Count(item => !wanted.Contains(item)) + target.Count(item => !present.Contains(item));
            if (churn > ClusterTabViewModel.InPlaceResortLimit)
            {
                range.ReplaceAll(target);
                return;
            }
        }

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], target[i]))
            {
                continue;
            }

            var at = -1;
            for (var j = i + 1; j < collection.Count; j++)
            {
                if (ReferenceEquals(collection[j], target[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
            {
                collection.Move(at, i);
            }
            else
            {
                collection.Insert(i, target[i]);
            }
        }
    }

    // ------------------------------------------------------------- selection

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    private ApplicationRowViewModel? _selectedRow;

    private bool HasSelectedRow => SelectedRow is not null;

    /// <summary>Enter: open the selected application's page. A click opens through <see cref="Open"/>.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void OpenSelected()
    {
        if (SelectedRow is { } row)
        {
            Open(row);
        }
    }

    /// <summary>The open application page, or null while the list is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen), nameof(IsListVisible))]
    private ApplicationPageViewModel? _page;

    public bool IsPageOpen => Page is not null;

    public bool IsListVisible => Page is null;

    public void Open(ApplicationRowViewModel row)
    {
        SelectedRow = row;
        var previous = Page;
        Page = new ApplicationPageViewModel(this, row);
        _ = previous?.CloseAsync();
    }

    /// <summary>Esc on the page: back to the list, with the same row selected.</summary>
    [RelayCommand]
    private async Task ClosePageAsync()
    {
        if (Page is not { } page)
        {
            return;
        }

        var key = page.Key;
        Page = null;
        await page.CloseAsync();
        if (_rowsByKey.TryGetValue(key, out var row) && VisibleRows.Contains(row))
        {
            SelectedRow = row;
        }
    }

    /// <summary>
    /// Set by the shell: switch this window to the Resources mode. The page calls it after
    /// asking the tab to reveal a linked object or to open the YAML editor, both of which live
    /// in the Resources mode's list and dock.
    /// </summary>
    public Action? SwitchToResources { get; set; }

    internal ApplicationRowViewModel? RowFor(string key) => _rowsByKey.GetValueOrDefault(key);

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    public async ValueTask DisposeAsync()
    {
        _rebuildTimer?.Stop();
        _clockTimer?.Stop();
        if (Page is { } page)
        {
            Page = null;
            await page.CloseAsync();
        }

        if (_cts is { } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        foreach (var scope in _scopes.Values)
        {
            scope.Dispose();
        }

        _scopes.Clear();
    }
}
