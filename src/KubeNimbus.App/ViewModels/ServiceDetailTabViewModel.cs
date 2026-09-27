using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Service detail: the answer to "why is traffic not reaching my pods". The pods the
/// selector matches and the endpoints that actually route, joined into one list, with the
/// verdict stated above it — including the three states that are easy to misread as "no
/// data": a selector that matches nothing, a selector-less service, and an ExternalName.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two watches, not a one-shot read.</b> Node detail's pod list is a snapshot because a
/// node's pods move slowly; a service's endpoints are the thing that changes while someone
/// is looking — a rollout, a readiness probe flipping, a crash loop — and "is it serving
/// now" is the question. Both watches are label-selected server-side (the service's own
/// selector for pods, <c>kubernetes.io/service-name</c> for EndpointSlices), so each is
/// exactly this service's objects, and both end with the pane.
/// </para>
/// <para>
/// <b>No verdict before both lists have synced</b> (UI rule 18). "The selector matches no
/// pod" said while the pod list is still in flight is the premature verdict that rule
/// exists to forbid, and on a distant cluster that window is seconds long.
/// </para>
/// <para>
/// It tracks the list's own <see cref="ResourceRowViewModel"/>, like node detail, so an
/// edit to the selector restarts the pod watch with the new one.
/// </para>
/// </remarks>
public sealed partial class ServiceDetailTabViewModel : InspectorTabViewModelBase
{
    public const int BackendsTabIndex = 0;

    public const int OverviewTabIndex = 1;

    public const int EventsTabIndex = 2;

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceRowViewModel _row;
    private readonly Func<OwnerRef, string?, Task>? _openPod;
    private readonly OpenNamedLogs? _openLogs;
    private readonly CancellationTokenSource _cts = new();

    private readonly Dictionary<string, DynamicResource> _pods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DynamicResource> _slices = new(StringComparer.Ordinal);
    private bool _podsSynced;
    private bool _slicesSynced;
    private string? _podsQuery;
    private bool _slicesStarted;
    private CancellationTokenSource? _podsCts;
    private CancellationTokenSource? _slicesCts;

    public ServiceDetailTabViewModel(
        ClusterClient? client,
        ResourceRowViewModel row,
        Func<OwnerRef, string?, Task>? openPod = null,
        string clusterName = "",
        OpenNamedLogs? openLogs = null)
        : base(clusterName.Length == 0 ? $"Service/{row.Name}" : $"Service/{row.Name} · {clusterName}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(row);

        _client = client;
        _row = row;
        _openPod = openPod;
        _openLogs = openLogs;
        ClusterName = clusterName;
        Key = KeyFor(clusterName, row.Namespace, row.Name);

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();
        _ = RefreshEventsAsync();
    }

    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"service:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    public string ClusterName { get; }

    public string ServiceName => _row.Name;

    public string Namespace => _row.Namespace;

    /// <summary>Backends = 0, Overview = 1, Events = 2 — bound by the strip and the headerless TabControl.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    // ------------------------------------------------------------- the service itself

    [ObservableProperty]
    private ServiceShape _shape;

    /// <summary>The chrome row's one line: the list's own Details text ("ClusterIP · 10.43.12.40 · 80/TCP").</summary>
    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private IReadOnlyList<DetailRow> _overviewRows = [];

    public ObservableCollection<ServicePortInfo> Ports { get; } = [];

    public bool HasNoPorts => Ports.Count == 0;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceRowViewModel.Resource))
        {
            RefreshFromRow();
        }
    }

    private void RefreshFromRow()
    {
        var service = _row.Resource;
        Shape = ServiceBackends.ShapeOf(service);
        SummaryText = _row.Details;

        var rows = BuildOverviewRows(service);
        if (!rows.SequenceEqual(OverviewRows))
        {
            OverviewRows = rows;
        }

        var ports = ServiceBackends.Ports(service);
        if (!ports.SequenceEqual(Ports))
        {
            Ports.Clear();
            foreach (var port in ports)
            {
                Ports.Add(port);
            }

            OnPropertyChanged(nameof(HasNoPorts));
        }

        EnsureWatches();
        Rebuild();
    }

    /// <summary>
    /// The Overview card: how the service is addressed and how it picks its backends. Rows
    /// for settings at their defaults are left out — a "Session affinity: None" line is a
    /// line saying nothing.
    /// </summary>
    internal static IReadOnlyList<DetailRow> BuildOverviewRows(DynamicResource service)
    {
        var raw = service.Raw;
        var spec = raw.TryGetProperty("spec", out var s) ? s : default;
        string Text(string name) =>
            spec.ValueKind == System.Text.Json.JsonValueKind.Object && spec.TryGetProperty(name, out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        IEnumerable<string> List(System.Text.Json.JsonElement owner, string name) =>
            owner.ValueKind == System.Text.Json.JsonValueKind.Object && owner.TryGetProperty(name, out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.Array
                ? v.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString() ?? "")
                : [];

        var rows = new List<DetailRow>();
        void Add(string label, string value)
        {
            if (value.Length > 0)
            {
                rows.Add(new DetailRow(label, value));
            }
        }

        var shape = ServiceBackends.ShapeOf(service);
        Add("Type", Text("type") is { Length: > 0 } type ? type : "ClusterIP");
        if (shape == ServiceShape.ExternalName)
        {
            Add("External name", ServiceBackends.ExternalNameOf(service));
        }
        else
        {
            var clusterIps = List(spec, "clusterIPs").ToList();
            var clusterIp = Text("clusterIP");
            Add(clusterIps.Count > 1 ? "Cluster IPs" : "Cluster IP",
                clusterIp == "None" ? "None (headless — DNS returns the pod IPs directly)"
                : clusterIps.Count > 1 ? string.Join(", ", clusterIps) : clusterIp);
        }

        var status = raw.TryGetProperty("status", out var st) ? st : default;
        var lb = status.ValueKind == System.Text.Json.JsonValueKind.Object && status.TryGetProperty("loadBalancer", out var l) ? l : default;
        var lbAddresses = lb.ValueKind == System.Text.Json.JsonValueKind.Object && lb.TryGetProperty("ingress", out var ing)
            && ing.ValueKind == System.Text.Json.JsonValueKind.Array
                ? ing.EnumerateArray()
                    .Select(i => i.TryGetProperty("ip", out var ip) ? ip.GetString() : i.TryGetProperty("hostname", out var h) ? h.GetString() : null)
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Select(a => a!)
                    .ToList()
                : [];
        Add("Load balancer", lbAddresses.Count > 0
            ? string.Join(", ", lbAddresses)
            : Text("type") == "LoadBalancer" ? "<pending> — no address assigned yet" : "");
        Add("External IPs", string.Join(", ", List(spec, "externalIPs")));

        Add("Selector", shape switch
        {
            ServiceShape.Selector => LabelSelector.ForPodsOf(service)?.ToQuery() ?? "",
            ServiceShape.NoSelector => "none — endpoints are written by something other than Kubernetes",
            _ => "",
        });

        if (Text("sessionAffinity") == "ClientIP")
        {
            Add("Session affinity", "ClientIP");
        }

        if (Text("externalTrafficPolicy") == "Local")
        {
            Add("External traffic", "Local — only to pods on the node that received it");
        }

        if (Text("internalTrafficPolicy") == "Local")
        {
            Add("Internal traffic", "Local — only to pods on the caller's node");
        }

        if (spec.ValueKind == System.Text.Json.JsonValueKind.Object
            && spec.TryGetProperty("publishNotReadyAddresses", out var publish)
            && publish.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            Add("Not-ready pods", "published — endpoints are marked ready whatever the probes say");
        }

        return rows;
    }

    // ------------------------------------------------------------- the backends

    public ObservableCollection<ServiceBackendViewModel> Backends { get; } = [];

    /// <summary>
    /// False when there is no row to show — the grid is then hidden and the verdict above it
    /// is the whole answer, rather than a header over nothing (UI rule 9).
    /// </summary>
    public bool HasBackends => Backends.Count > 0;

    /// <summary>
    /// True until every list the verdict depends on has delivered its initial sync. While it
    /// is, the pane says what it is reading and shows no verdict.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict))]
    private bool _isLoading;

    [ObservableProperty]
    private string _verdictHeadline = "";

    [ObservableProperty]
    private string _verdictDetail = "";

    /// <summary>"success" / "warn" / "error", or empty for a neutral verdict — the infoBar's severity class.</summary>
    [ObservableProperty]
    private string _verdictLevel = "";

    public bool HasVerdict => !IsLoading;

    /// <summary>The server's own sentence when the pods or the slices could not be read — a 403 most often.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadError))]
    private string? _readError;

    public bool HasReadError => !string.IsNullOrEmpty(ReadError);

    private string? _podsError;
    private string? _slicesError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPodCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsMaximizedCommand))]
    private ServiceBackendViewModel? _selectedBackend;

    /// <summary>Why the last logs open did not open anything — the pod went away since the watch's last frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogsNotice))]
    private string? _logsNotice;

    public bool HasLogsNotice => !string.IsNullOrEmpty(LogsNotice);

    /// <summary>
    /// Starts, restarts or stops the two watches to match what the service now is. Called on
    /// open and on every change to the service object, so an edited selector re-lists the pods
    /// and an ExternalName that becomes a ClusterIP starts reading endpoints.
    /// </summary>
    private void EnsureWatches()
    {
        var service = _row.Resource;
        var shape = ServiceBackends.ShapeOf(service);
        var selector = shape == ServiceShape.Selector ? LabelSelector.ForPodsOf(service) : null;
        var query = selector?.ToQuery();

        if (!string.Equals(query, _podsQuery, StringComparison.Ordinal) || (_podsQuery is null && !_podsSynced))
        {
            _podsCts?.Cancel();
            _podsCts?.Dispose();
            _podsCts = null;
            _pods.Clear();
            _podsQuery = query;
            _podsSynced = selector is null; // nothing to read is read
            _podsError = null;

            if (selector is not null)
            {
                if (_client is { } client)
                {
                    _podsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    _ = WatchAsync(client, ResourceDescriptor.Pods, selector, ApplyPodEvent, e => _podsError = e, _podsCts.Token);
                }
                else
                {
                    ApplyPodEvent(ResourceEvent<DynamicResource>.Reset);
                    foreach (var pod in DemoData.Pods.Where(p =>
                                 string.Equals(p.Namespace, Namespace, StringComparison.Ordinal) && selector.Matches(p.Labels)))
                    {
                        ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Added, pod));
                    }

                    ApplyPodEvent(ResourceEvent<DynamicResource>.Synced);
                }
            }
        }

        if (shape == ServiceShape.ExternalName)
        {
            _slicesCts?.Cancel();
            _slicesCts?.Dispose();
            _slicesCts = null;
            _slicesStarted = false;
            _slices.Clear();
            _slicesSynced = true;
            _slicesError = null;
            return;
        }

        if (_slicesStarted)
        {
            return;
        }

        _slicesStarted = true;
        _slicesSynced = false;
        var slicesOf = ServiceBackends.SlicesOf(ServiceName);
        if (_client is { } sliceClient)
        {
            _slicesCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _ = WatchAsync(sliceClient, ResourceDescriptor.EndpointSlices, slicesOf, ApplySliceEvent, e => _slicesError = e, _slicesCts.Token);
        }
        else
        {
            ApplySliceEvent(ResourceEvent<DynamicResource>.Reset);
            foreach (var slice in DemoData.OfKind("EndpointSlice").Where(s =>
                         string.Equals(s.Namespace, Namespace, StringComparison.Ordinal) && slicesOf.Matches(s.Labels)))
            {
                ApplySliceEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Added, slice));
            }

            ApplySliceEvent(ResourceEvent<DynamicResource>.Synced);
        }
    }

    private async Task WatchAsync(
        ClusterClient client,
        ResourceDescriptor descriptor,
        LabelSelector selector,
        Action<ResourceEvent<DynamicResource>> apply,
        Action<string?> reportError,
        CancellationToken token)
    {
        try
        {
            await foreach (var evt in client.WatchResourceAsync(
                               descriptor, Namespace,
                               connectionLost: ex => Dispatcher.UIThread.Post(() =>
                               {
                                   if (!token.IsCancellationRequested)
                                   {
                                       reportError(ex.Message);
                                       Rebuild();
                                   }
                               }),
                               cancellationToken: token,
                               labelSelector: selector).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        reportError(null);
                        apply(evt);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A watch that ends badly still ends the wait (UI rule 18): the error is the
            // verdict, and the rows read so far stay on screen.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    reportError(ex.Message);
                    apply(ResourceEvent<DynamicResource>.Synced);
                }
            });
        }
    }

    /// <summary>One frame of the pod watch — the entry point the demo, the tests and the watch share.</summary>
    internal void ApplyPodEvent(ResourceEvent<DynamicResource> evt) => Apply(evt, _pods, ref _podsSynced);

    /// <summary>One frame of the EndpointSlice watch.</summary>
    internal void ApplySliceEvent(ResourceEvent<DynamicResource> evt) => Apply(evt, _slices, ref _slicesSynced);

    private void Apply(ResourceEvent<DynamicResource> evt, Dictionary<string, DynamicResource> store, ref bool synced)
    {
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                // The start of a (re)list, not its end: loading again, not "empty".
                store.Clear();
                synced = false;
                break;
            case ResourceEventType.Synced:
                synced = true;
                break;
            case ResourceEventType.Deleted when evt.Resource is { } gone:
                store.Remove(gone.Name);
                break;
            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } resource:
                store[resource.Name] = resource;
                break;
        }

        Rebuild();
    }

    /// <summary>Re-joins the two stores and restates the verdict. Cheap: a service has tens of backends, not thousands.</summary>
    private void Rebuild()
    {
        var shape = Shape;
        var backends = shape == ServiceShape.ExternalName
            ? []
            : ServiceBackends.Join(
                _pods.Values.OrderBy(p => p.Name, StringComparer.Ordinal),
                _slices.Values.OrderBy(s => s.Name, StringComparer.Ordinal),
                hasSelector: shape == ServiceShape.Selector);

        SyncBackends(backends);
        OnPropertyChanged(nameof(HasBackends));

        ReadError = (_podsError, _slicesError) switch
        {
            ({ } pods, { } slices) => $"Could not read the pods: {pods}\nCould not read the endpoints: {slices}",
            ({ } pods, null) => $"Could not read the pods: {pods}",
            (null, { } slices) => $"Could not read the endpoints: {slices}",
            _ => null,
        };

        IsLoading = !(_podsSynced && _slicesSynced);
        if (IsLoading)
        {
            VerdictHeadline = shape == ServiceShape.Selector
                ? "Reading the pods the selector matches and the endpoints serving them…"
                : "Reading the endpoints…";
            VerdictDetail = "";
            VerdictLevel = "";
            return;
        }

        var verdict = ServiceBackends.Verdict(_row.Resource, backends);
        VerdictHeadline = verdict.Headline;
        VerdictDetail = verdict.Detail;
        VerdictLevel = verdict.Level switch
        {
            Core.Networking.VerdictLevel.Good => "success",
            Core.Networking.VerdictLevel.Warning => "warn",
            Core.Networking.VerdictLevel.Problem => "error",
            _ => "",
        };
    }

    /// <summary>
    /// Updates the rows in place by key, so a watch tick that changes one backend does not
    /// rebuild the grid under the reader — and the selected row stays selected.
    /// </summary>
    private void SyncBackends(IReadOnlyList<ServiceBackend> backends)
    {
        var selectedKey = SelectedBackend?.Key;
        var fresh = backends.Select(b => new ServiceBackendViewModel(b)).ToList();

        if (fresh.Select(b => b.Key).SequenceEqual(Backends.Select(b => b.Key), StringComparer.Ordinal))
        {
            for (var i = 0; i < fresh.Count; i++)
            {
                if (!fresh[i].SameAs(Backends[i]))
                {
                    Backends[i] = fresh[i];
                }
            }
        }
        else
        {
            Backends.Clear();
            foreach (var backend in fresh)
            {
                Backends.Add(backend);
            }
        }

        if (selectedKey is not null && !ReferenceEquals(SelectedBackend, Backends.FirstOrDefault(b => b.Key == selectedKey)))
        {
            SelectedBackend = Backends.FirstOrDefault(b => b.Key == selectedKey);
        }
    }

    private bool CanOpenPod => SelectedBackend?.PodName is not null && _openPod is not null;

    private bool CanOpenPodLogs => SelectedBackend?.PodName is not null && _openLogs is not null;

    /// <summary>Enter, double-click and the row's chevron: the backend's pod, through the owner resolver.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private async Task OpenPodAsync()
    {
        if (SelectedBackend is { PodName: { } name } backend && _openPod is not null)
        {
            await _openPod(new OwnerRef("v1", "Pod", name, backend.Uid, false), backend.Namespace ?? Namespace);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsAsync() => OpenPodLogsAsync(SelectedBackend, maximized: false);

    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsMaximizedAsync() => OpenPodLogsAsync(SelectedBackend, maximized: true);

    /// <summary>
    /// A backend's logs — L, Shift+L, the row's icon — through the cluster tab's one
    /// open-logs path, so the pane reused and "Open logs maximized" are the list's own.
    /// The UID goes with it: a StatefulSet pod recreated under the same name is stated,
    /// not opened.
    /// </summary>
    public async Task OpenPodLogsAsync(ServiceBackendViewModel? backend, bool maximized)
    {
        if (backend?.PodName is not { } name || _openLogs is null)
        {
            return;
        }

        SelectedBackend = backend;
        LogsNotice = await _openLogs(
            new OwnerRef("v1", "Pod", name, backend.Uid, false), backend.Namespace ?? Namespace, maximized, _cts.Token);
    }

    // ----------------------------------------------------------------------- events

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EventsCaption))]
    private bool _isLoadingEvents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEventsError))]
    [NotifyPropertyChangedFor(nameof(EventsCaption))]
    private string? _eventsError;

    public bool HasEventsError => !string.IsNullOrEmpty(EventsError);

    public bool HasNoEvents => !IsLoadingEvents && !HasEventsError && Events.Count == 0;

    public string EventsCaption => IsLoadingEvents
        ? "Reading events…"
        : HasEventsError
            ? "Could not read events"
            : Events.Count switch
            {
                0 => "No recent events",
                1 => "1 event",
                var n => $"{n} events",
            };

    /// <summary>
    /// What the controllers said about this service — a load balancer that would not
    /// provision, an EndpointSlice the controller failed to write. A one-shot with Refresh,
    /// like node detail's: these arrive minutes apart.
    /// </summary>
    [RelayCommand]
    private async Task RefreshEventsAsync()
    {
        IsLoadingEvents = true;
        EventsError = null;
        try
        {
            var events = _client is { } client
                ? await client.GetEventsForAsync(_row.Resource, _cts.Token)
                : [.. DemoData.Events
                    .Where(e => e.InvolvedObject() is { Kind: "Service" } involved
                        && string.Equals(involved.Name, ServiceName, StringComparison.Ordinal)
                        && string.Equals(e.InvolvedObjectNamespace(), Namespace, StringComparison.Ordinal))
                    .OrderByDescending(e => e.LastTimestamp() ?? DateTimeOffset.MinValue)];

            Events.Clear();
            foreach (var evt in events)
            {
                Events.Add(new EventRowViewModel(evt));
            }
        }
        catch (OperationCanceledException)
        {
            // Pane closed while the list was in flight.
        }
        catch (Exception ex)
        {
            EventsError = ex.Message;
        }
        finally
        {
            IsLoadingEvents = false;
            OnPropertyChanged(nameof(EventsCaption));
            OnPropertyChanged(nameof(HasNoEvents));
        }
    }

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        await _cts.CancelAsync();
        _podsCts?.Dispose();
        _slicesCts?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>One label/value line of a detail pane's overview card.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>One row of the Service pane's backends list, formatted.</summary>
public sealed class ServiceBackendViewModel
{
    public ServiceBackendViewModel(ServiceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        Backend = backend;
        PodName = backend.Pod?.Name ?? backend.Endpoints.Select(e => e.PodName).FirstOrDefault(n => n is not null);
        Namespace = backend.Namespace;
        Uid = backend.Uid;
        Name = backend.Name;
        Key = backend.Pod is not null ? $"pod:{backend.Name}" : $"endpoint:{backend.Name}";

        if (backend.Pod is { } pod)
        {
            var summary = ResourceStatusSummary.Summarize(pod);
            PodStatus = summary.Status;
            PodHealth = summary.Health;
        }
        else
        {
            PodStatus = PodName is null ? "no pod" : "not matched";
            PodHealth = ResourceHealth.Idle;
        }

        StateText = backend.State switch
        {
            BackendState.Serving => "Serving",
            BackendState.TerminatingServing => "Draining",
            BackendState.NotReady => "Not ready",
            BackendState.Terminating => "Terminating",
            _ => "No endpoint",
        };
        StateHealth = backend.State switch
        {
            BackendState.Serving => ResourceHealth.Ok,
            BackendState.NotReady => ResourceHealth.Error,
            _ => ResourceHealth.Warn,
        };
        Reason = backend.Reason;

        var ports = backend.Endpoints.SelectMany(e => e.Ports).Distinct(StringComparer.Ordinal).ToList();
        var addresses = backend.Addresses;
        AddressText = addresses.Count == 0
            ? "—"
            : ports is [var single] && single.EndsWith("/TCP", StringComparison.Ordinal)
              && addresses.Count == 1 && !addresses[0].Contains(':', StringComparison.Ordinal)
                ? $"{addresses[0]}:{single[..^4]}"
                : string.Join(", ", addresses);
        AddressTooltip = ports.Count == 0 ? AddressText : $"{string.Join(", ", addresses)} → {string.Join(", ", ports)}";
        NodeName = backend.NodeName;
        HasLogs = PodName is not null;
    }

    public ServiceBackend Backend { get; }

    public string Key { get; }

    /// <summary>The pod this row names, if any — what Enter, L and the chevron open.</summary>
    public string? PodName { get; }

    public string? Namespace { get; }

    public string? Uid { get; }

    public string Name { get; }

    public string PodStatus { get; }

    public string PodHealth { get; }

    /// <summary>What the endpoint is doing with traffic: Serving, Not ready, Draining, Terminating, No endpoint.</summary>
    public string StateText { get; }

    public string StateHealth { get; }

    /// <summary>Why a row is not serving, in a sentence — the state cell's tooltip.</summary>
    public string Reason { get; }

    public string StateTooltip => Reason.Length > 0 ? $"{StateText} — {Reason}" : StateText;

    public string AddressText { get; }

    public string AddressTooltip { get; }

    public string NodeName { get; }

    public bool HasLogs { get; }

    /// <summary>Same visible content — the in-place update skips rows a watch tick did not change.</summary>
    internal bool SameAs(ServiceBackendViewModel other) =>
        Key == other.Key && PodStatus == other.PodStatus && StateText == other.StateText
        && AddressText == other.AddressText && NodeName == other.NodeName && Reason == other.Reason
        && Uid == other.Uid;
}
