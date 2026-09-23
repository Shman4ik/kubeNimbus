using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Node detail: the conditions and taints the node reports about itself, the machine
/// behind it, how much of it the scheduler has already promised away (allocatable vs
/// requested), how much of it is actually in use, the pods that are on it, and what the
/// kubelet and the node controller have said about it.
/// </summary>
/// <remarks>
/// <para>
/// It tracks the same <see cref="ResourceRowViewModel"/> the live list holds, exactly as
/// <see cref="PodDetailTabViewModel"/> does, so the cordon state, the conditions and the
/// taints follow the node's own watch with no second stream. The pods are a one-shot
/// list with an explicit Refresh — the same shape pod detail's Events tab has, and for
/// the same reason: it is a snapshot you read, not a stream you watch, and the alternative
/// is a second watch keyed on a <c>fieldSelector</c> the watch engine does not take today.
/// </para>
/// <para>
/// The mutating half (cordon / uncordon / drain) is deliberately <em>not</em> here: it
/// lands on the cluster tab's shared confirm strip like every other mutating action
/// (UI rule 17), so there is one implementation of "ask first", one place a drain can be
/// running, and no new always-visible control in a pane that already has a full chrome
/// row (UI rules 1 and 10).
/// </para>
/// </remarks>
public sealed partial class NodeDetailTabViewModel : InspectorTabViewModelBase
{
    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceRowViewModel _row;
    private readonly ResourceDescriptor? _podDescriptor;
    private readonly Func<OwnerRef, string?, Task>? _openPod;
    private readonly CancellationTokenSource _cts = new();

    public const int OverviewTabIndex = 0;

    public const int PodsTabIndex = 1;

    public const int EventsTabIndex = 2;

    public const int UsageTabIndex = 3;

    private static TimeSpan MetricsPollInterval =>
        TimeSpan.FromSeconds(App.LoadSettings().MetricsPollSeconds);

    public NodeDetailTabViewModel(
        ClusterClient? client,
        ResourceRowViewModel row,
        ResourceDescriptor? podDescriptor = null,
        Func<OwnerRef, string?, Task>? openPod = null,
        string clusterName = "")
        : base(
            clusterName.Length == 0 ? $"Node/{row.Name}" : $"Node/{row.Name} · {clusterName}",
            isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(row);

        _client = client;
        _row = row;
        _podDescriptor = podDescriptor;
        _openPod = openPod;
        NodeName = row.Name;
        ClusterName = clusterName;
        Key = KeyFor(clusterName, row.Name);

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();
        SeedUsageFromRow();
        _ = RefreshPodsAsync();
        _ = RefreshEventsAsync();

        if (client is null)
        {
            // The demo list has already replayed a window of polls onto this row, which
            // SeedUsageFromRow copied. A node detail opened with no such history (a
            // screenshot fixture that skipped the list) gets the same replay here, through
            // the same ApplyMetrics a real poll lands on (demo rule 4).
            if (!HasUsageSamples)
            {
                DemoUsage.SeedNode(this);
            }
        }
        else
        {
            _ = Task.Run(() => PollMetricsAsync(client, _cts.Token), _cts.Token);
        }
    }

    public static string KeyFor(string clusterName, string name) => $"node:{clusterName}/{name}";

    public override string Key { get; }

    public string NodeName { get; }

    /// <summary>Cluster this node came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    /// <summary>
    /// Overview = 0, Pods = 1, Events = 2, Usage = 3 (the constants above). Bound by both
    /// the segmented strip and the headerless TabControl; new tabs are appended so the
    /// existing indices stay what the screenshot scenarios already select.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    // ------------------------------------------------------------------ the node itself

    /// <summary>kubectl's own status word for a node — "Ready", "Ready,SchedulingDisabled", "NotReady".</summary>
    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _statusHealth = ResourceHealth.Idle;

    /// <summary>True while the node is cordoned. Its own line, because it is the one node
    /// state a reader is usually looking for and it is easy to miss inside a status string.</summary>
    [ObservableProperty]
    private bool _isCordoned;

    [ObservableProperty]
    private string _roles = "";

    [ObservableProperty]
    private NodeInfo _info = new("", "", "", "", "", "");

    /// <summary>
    /// The System card, one label/value pair per line the node actually reported — a node
    /// that has no ExternalIP or no provider ID has no such row, rather than a label beside
    /// an empty value that reads as "this field is broken".
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<NodeSystemRow> _systemRows = [];

    public ObservableCollection<NodeConditionViewModel> Conditions { get; } = [];

    public ObservableCollection<NodeTaint> Taints { get; } = [];

    /// <summary>True when the node reports no taints at all — its own visual, not a blank list (UI rule 9).</summary>
    public bool HasNoTaints => Taints.Count == 0;

    // --------------------------------------------------------- allocatable vs requested

    public ObservableCollection<NodeResourceLineViewModel> ResourceLines { get; } = [];

    /// <summary>
    /// True once the pods on the node have been read. Until then the requested figures
    /// would all be zero, which reads as an empty node rather than as an unanswered
    /// question — so the pane says it is still counting instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResourceSummary))]
    private bool _hasCountedPods;

    public bool HasResourceSummary => HasCountedPods;

    // ---------------------------------------------------------------- pods on this node

    public ObservableCollection<NodePodViewModel> Pods { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodsCaption))]
    private bool _isLoadingPods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPodsError))]
    private string? _podsError;

    public bool HasPodsError => !string.IsNullOrEmpty(PodsError);

    /// <summary>Loading / empty / counted — three states, never a blank rectangle (UI rule 9).</summary>
    public string PodsCaption => IsLoadingPods
        ? "Reading the pods on this node…"
        : Pods.Count switch
        {
            0 => "No pods are scheduled on this node.",
            1 => "1 pod on this node",
            var n => $"{n} pods on this node",
        };

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceRowViewModel.Resource) or nameof(ResourceRowViewModel.Status))
        {
            RefreshFromRow();
        }
    }

    /// <summary>
    /// Re-reads everything the node object itself carries. Cheap and idempotent, so it
    /// runs on every watch tick rather than trying to work out what changed — a node
    /// object is a few kilobytes and a tick is seconds apart at worst.
    /// </summary>
    private void RefreshFromRow()
    {
        var node = _row.Resource;
        StatusText = _row.Status;
        StatusHealth = _row.StatusHealth;
        IsCordoned = NodeActions.IsCordoned(node);
        Roles = _row.Details;
        Info = NodeResources.Info(node);

        // Rebuilt only when something in it changed: the rows are SelectableTextBlocks,
        // and replacing the list on every watch tick would drop a selection someone is
        // in the middle of copying an IP out of.
        var systemRows = BuildSystemRows(Info, DateTimeOffset.UtcNow);
        if (!systemRows.SequenceEqual(SystemRows))
        {
            SystemRows = systemRows;
        }

        Conditions.Clear();
        foreach (var condition in NodeResources.Conditions(node))
        {
            Conditions.Add(new NodeConditionViewModel(condition));
        }

        Taints.Clear();
        foreach (var taint in NodeResources.Taints(node))
        {
            Taints.Add(taint);
        }

        OnPropertyChanged(nameof(HasNoTaints));
        RecomputeResources();
    }

    /// <summary>
    /// The System card's lines, in the order someone reads a machine: what it runs, where
    /// it is, how it is addressed, when it joined. Anything the node did not report is
    /// left out rather than shown blank.
    /// </summary>
    internal static IReadOnlyList<NodeSystemRow> BuildSystemRows(NodeInfo info, DateTimeOffset now)
    {
        var rows = new List<NodeSystemRow>();

        void Add(string label, string value)
        {
            if (value.Length > 0)
            {
                rows.Add(new NodeSystemRow(label, value));
            }
        }

        Add("Kubelet", info.KubeletVersion);
        Add("OS image", info.OsImage);
        Add("Platform", (info.OperatingSystem, info.Architecture) switch
        {
            ({ Length: > 0 } os, { Length: > 0 } arch) => $"{os}/{arch}",
            ({ Length: > 0 } os, _) => os,
            (_, var arch) => arch,
        });
        Add("Kernel", info.KernelVersion);
        Add("Runtime", info.ContainerRuntime);

        // Every address, labelled with the node's own type name: InternalIP, ExternalIP,
        // Hostname, InternalDNS… A node can report several of one type (dual-stack gives
        // two InternalIPs), and those read as one line rather than two with the same label.
        foreach (var group in info.Addresses.GroupBy(a => a.Type, StringComparer.Ordinal))
        {
            Add(group.Key, string.Join(", ", group.Select(a => a.Address)));
        }

        Add(info.PodCidrs.Count > 1 ? "Pod CIDRs" : "Pod CIDR", string.Join(", ", info.PodCidrs));
        Add("Zone", info.Region.Length > 0 && info.Zone.Length > 0 ? $"{info.Zone} ({info.Region})" : info.Zone);
        if (info.Zone.Length == 0)
        {
            Add("Region", info.Region);
        }

        Add("Instance type", info.InstanceType);
        Add("Provider ID", info.ProviderId);
        Add("Created", info.Created is { } created
            ? $"{created.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · {RelativeTime.Compact(now - created)} ago"
            : "");

        return rows;
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

    /// <summary>
    /// True when the node has no recorded events — its own sentence, not an empty list
    /// (UI rule 9). Events expire (an hour by default), so a quiet node is the normal case
    /// and needs saying so rather than looking like a fetch that never returned.
    /// </summary>
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
    /// Reads the events about this node. A one-shot with an explicit Refresh, like the
    /// Pods tab and pod detail's Events: a node's events arrive minutes apart, and a
    /// second watch per open pane is the wrong trade for them.
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
                : DemoEvents();

            Events.Clear();
            foreach (var evt in events)
            {
                Events.Add(new EventRowViewModel(evt));
            }
        }
        catch (OperationCanceledException)
        {
            // Tab closed while the list was in flight.
        }
        catch (Exception ex)
        {
            // Listing events across namespaces is a permission plenty of roles lack; the
            // server's own sentence names the verb and the subject.
            EventsError = ex.Message;
        }
        finally
        {
            IsLoadingEvents = false;
            OnPropertyChanged(nameof(EventsCaption));
            OnPropertyChanged(nameof(HasNoEvents));
        }
    }

    /// <summary>The same kind-and-name match the API server makes, over the shipped dataset.</summary>
    private IReadOnlyList<DynamicResource> DemoEvents() =>
        [.. DemoData.Events
            .Where(e => e.InvolvedObject() is { Kind: "Node" } involved
                && string.Equals(involved.Name, NodeName, StringComparison.Ordinal))
            .OrderByDescending(e => e.LastTimestamp() ?? DateTimeOffset.MinValue)];

    // ------------------------------------------------------------------------ usage

    /// <summary>
    /// This node's measured usage over time. Its own ring rather than the list row's,
    /// because the row stops being polled the moment the list moves to another kind and
    /// this pane has to keep going; it starts as a copy of the row's so a node that has
    /// been on screen for ten minutes opens with ten minutes of chart.
    /// </summary>
    public UsageHistory History { get; } = new();

    [ObservableProperty]
    private IReadOnlyList<double?> _cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double?> _memorySeries = [];

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private string _peakCpuText = "—";

    [ObservableProperty]
    private string _peakMemoryText = "—";

    /// <summary>
    /// " (31% of allocatable)" — measured usage against what the scheduler may hand out,
    /// with its own leading space and parentheses so a node that reported no allocatable
    /// renders nothing rather than an empty "()".
    /// </summary>
    [ObservableProperty]
    private string _cpuShareText = "";

    [ObservableProperty]
    private string _memoryShareText = "";

    [ObservableProperty]
    private string _cpuTooltip = "";

    [ObservableProperty]
    private string _memoryTooltip = "";

    [ObservableProperty]
    private string _usageWindowCaption = "collecting…";

    /// <summary>True once at least one sample has landed; until then the tab says it is collecting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectingUsage))]
    private bool _hasUsageSamples;

    /// <summary>
    /// No usable metrics.k8s.io here. Kept apart from "nothing has arrived yet", which
    /// looks the same and has the opposite next step (install metrics-server vs wait).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectingUsage))]
    private bool _isMetricsUnavailable;

    public bool IsCollectingUsage => !HasUsageSamples && !IsMetricsUnavailable;

    public string UsagePollHint =>
        $"metrics.k8s.io has no watch endpoint, so usage is polled every {MetricsPollInterval.TotalSeconds:0}s. "
        + "History is kept for this session only — kubeNimbus is a viewer, not a time-series store.";

    partial void OnIsMetricsUnavailableChanged(bool value)
    {
        if (value)
        {
            UsageWindowCaption = "";
        }
    }

    private void SeedUsageFromRow()
    {
        for (var i = 0; i < _row.History.Count; i++)
        {
            History.Add(_row.History[i]);
        }

        if (History.Latest is { } latest && (latest.CpuNanocores is not null || latest.MemoryBytes is not null))
        {
            HasUsageSamples = true;
        }

        PublishUsage();
    }

    /// <summary>
    /// Records one poll of this node's usage — or a gap, when both are null. The entry
    /// point a real poll and the demo replay share, which is why it takes a timestamp.
    /// </summary>
    public void ApplyMetrics(long? cpuNanocores, long? memoryBytes, DateTimeOffset? at = null)
    {
        History.Add(cpuNanocores, memoryBytes, at);
        if (cpuNanocores is not null || memoryBytes is not null)
        {
            HasUsageSamples = true;
        }

        PublishUsage();
    }

    private void PublishUsage()
    {
        var latest = History.Latest;
        CpuSeries = History.CpuSeries();
        MemorySeries = History.MemorySeries();
        CpuText = Quantity.FormatCpu(latest?.CpuNanocores);
        MemoryText = Quantity.FormatMemory(latest?.MemoryBytes);
        PeakCpuText = Quantity.FormatCpu(History.PeakCpuNanocores);
        PeakMemoryText = Quantity.FormatMemory(History.PeakMemoryBytes);
        UpdateUsageShares();
        CpuTooltip = UsageFormat.Tooltip("Node CPU", CpuText, PeakCpuText, History);
        MemoryTooltip = UsageFormat.Tooltip("Node Mem", MemoryText, PeakMemoryText, History);
        if (!IsMetricsUnavailable)
        {
            UsageWindowCaption = UsageFormat.WindowCaption(History);
        }
    }

    /// <summary>
    /// Usage as a share of allocatable, the same denominator the requested bars use — so
    /// "requested 80%, used 12%" on the two tabs are figures of one node, comparable
    /// directly. Re-run when the node object changes as well as when a poll lands.
    /// </summary>
    private void UpdateUsageShares()
    {
        var latest = History.Latest;
        CpuShareText = Share(latest?.CpuNanocores / 1_000_000_000d, _summary?.Cpu.Allocatable);
        MemoryShareText = Share(latest?.MemoryBytes, _summary?.Memory.Allocatable);
    }

    internal static string Share(double? used, double? allocatable) =>
        used is { } u && allocatable is { } a && a > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({u * 100d / a:0}% of allocatable)")
            : "";

    /// <summary>
    /// Polls this one node's usage. Scoped to the pane's own token, so closing the tab
    /// ends it — the second documented poll pattern after the list's, and for the same
    /// reason: metrics.k8s.io has no watch.
    /// </summary>
    private async Task PollMetricsAsync(ClusterClient client, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(MetricsPollInterval);
            do
            {
                try
                {
                    var metrics = await client.GetNodeMetricsAsync(NodeName, token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (metrics is not null)
                        {
                            ApplyMetrics(metrics.CpuNanocores, metrics.MemoryBytes);
                        }
                        else if (HasUsageSamples)
                        {
                            // Not scraped this round: a gap in the line, never a zero.
                            ApplyMetrics(null, null);
                        }
                    });
                }
                catch (MetricsUnavailableException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => IsMetricsUnavailable = true);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A transient failure (a dropped connection, a slow aggregated API)
                    // skips this tick; the next one tries again.
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Pane closed.
        }
    }

    /// <summary>
    /// Lists the pods scheduled here. Server-side <c>fieldSelector</c>, so this is one
    /// small response rather than every pod in the cluster filtered locally.
    /// </summary>
    [RelayCommand]
    private async Task RefreshPodsAsync()
    {
        IsLoadingPods = true;
        PodsError = null;
        try
        {
            var pods = _client is { } client && _podDescriptor is { } descriptor
                ? await client.ListPodsOnNodeAsync(descriptor, NodeName, _cts.Token)
                : DemoPods();

            Pods.Clear();
            foreach (var pod in pods.OrderBy(p => p.Namespace, StringComparer.Ordinal)
                         .ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                Pods.Add(new NodePodViewModel(pod));
            }

            _podsOnNode = pods;
            HasCountedPods = true;
            RecomputeResources();
        }
        catch (OperationCanceledException)
        {
            // Tab closed while the list was in flight.
        }
        catch (Exception ex)
        {
            // An RBAC 403 on pods is the common one and its sentence names the subject
            // and the verb; the pane says so rather than showing an empty node.
            PodsError = ex.Message;
        }
        finally
        {
            IsLoadingPods = false;
            OnPropertyChanged(nameof(PodsCaption));
        }
    }

    private IReadOnlyList<DynamicResource> _podsOnNode = [];

    /// <summary>
    /// The demo cluster's stand-in for the field-selected list: the same
    /// <c>spec.nodeName</c> match the API server would make, over the shipped dataset.
    /// Everything downstream of it — the arithmetic, the bars, the pod rows — is the
    /// production code path (demo rule 4).
    /// </summary>
    private IReadOnlyList<DynamicResource> DemoPods() =>
        [.. DemoData.Pods.Where(p =>
            string.Equals(NodeActions.NodeNameOf(p), NodeName, StringComparison.Ordinal))];

    private NodeResourceSummary? _summary;

    private void RecomputeResources()
    {
        var summary = NodeResources.Summarize(_row.Resource, _podsOnNode);
        _summary = summary;
        var lines = new[]
        {
            new NodeResourceLineViewModel("CPU", summary.Cpu, FormatCores),
            new NodeResourceLineViewModel("Memory", summary.Memory, FormatBytes),
            new NodeResourceLineViewModel("Pods", summary.Pods, static v => v.ToString("0", CultureInfo.InvariantCulture)),
        };

        ResourceLines.Clear();
        foreach (var line in lines)
        {
            ResourceLines.Add(line);
        }

        UpdateUsageShares();
    }

    /// <summary>
    /// The arithmetic is in base units (cores, bytes) and the app's formatters take the
    /// integer units metrics.k8s.io reports in (nanocores, bytes), so the conversion
    /// happens here, once, rather than inside a formatter that would then have two
    /// meanings.
    /// </summary>
    internal static string FormatCores(double cores) =>
        Quantity.FormatCpu((long)Math.Round(cores * 1_000_000_000d));

    internal static string FormatBytes(double bytes) => Quantity.FormatMemory((long)Math.Round(bytes));

    /// <summary>Opens one of the pods on this node, through the same resolver owner chips use.</summary>
    [RelayCommand]
    private async Task OpenPodAsync(NodePodViewModel? pod)
    {
        if (pod is null || _openPod is null)
        {
            return;
        }

        await _openPod(new OwnerRef("v1", "Pod", pod.Name, null, false), pod.Namespace);
    }

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}

/// <summary>
/// One resource's allocatable-vs-requested line, formatted. The formatter is passed in
/// rather than switched on inside, because CPU and memory read in units this app already
/// formats one way everywhere (<see cref="Quantity.FormatCpu"/> /
/// <see cref="Quantity.FormatMemory"/>) and pod count reads as a plain number.
/// </summary>
public sealed class NodeResourceLineViewModel
{
    public NodeResourceLineViewModel(string label, NodeResourceLine line, Func<double, string> format)
    {
        Label = label;
        Line = line;
        AllocatableText = line.Allocatable is null ? "—" : Format(line.Allocatable, format);
        RequestedText = Format(line.Requested, format);
        LimitText = Format(line.Limit, format);
        FreeText = line.Free is null ? "—" : Format(line.Free, format);
        RequestedPercentText = line.RequestedPercent is { } percent ? $"{percent:0}%" : "—";
        LimitPercentText = line.LimitPercent is { } limit ? $"{limit:0}%" : "";

        // Clamped at 100 even when the requests oversubscribe, which is legitimate: the
        // bar cannot say "112%" and the number printed beside it already does.
        RequestedPercentValue = Math.Clamp(line.RequestedPercent ?? 0, 0, 100);

        // The whole limits half of the row in one string, so a resource with no limit to
        // report (the pods line, always) renders nothing rather than a caption over an
        // empty figure or a dangling separator between two blanks.
        LimitSummaryText = HasLimit
            ? LimitPercentText.Length > 0
                ? $"limits {LimitText} ({LimitPercentText})"
                : $"limits {LimitText}"
            : "";

        Tooltip = line.Allocatable is null
            ? $"{label}: this node did not report an allocatable {line.Resource}."
            : $"{label}\nallocatable {AllocatableText} (capacity {Format(line.Capacity, format)})\n"
              + $"requested {RequestedText} · free {FreeText}"
              + (HasLimit ? $"\nlimits {LimitText} ({LimitPercentText} of allocatable)" : "");
    }

    public string Label { get; }

    public NodeResourceLine Line { get; }

    public string AllocatableText { get; }

    public string RequestedText { get; }

    public string LimitText { get; }

    public string FreeText { get; }

    public string RequestedPercentText { get; }

    public string LimitPercentText { get; }

    public bool HasLimit => LimitText.Length > 0;

    /// <summary>
    /// The limits half of the row, or the empty string when this resource has none — the
    /// pods line, where <c>Limit: null</c> is the normal case rather than missing data.
    /// </summary>
    public string LimitSummaryText { get; }

    /// <summary>0–100, for the bar. The unclamped figure is <see cref="RequestedPercentText"/>.</summary>
    public double RequestedPercentValue { get; }

    /// <summary>
    /// Where the limit marker goes, as a percentage of allocatable, or null for a
    /// resource with no limit. Deliberately <em>unclamped</em>: the meter needs to know
    /// the limit is past the track's end so it can say so, and a value clamped here
    /// would make an oversubscribed node render as exactly full.
    /// </summary>
    public double? LimitPercentValue => HasLimit ? Line.LimitPercent : null;

    public string Tooltip { get; }

    /// <summary>
    /// Over 90% of allocatable requested is the state worth colouring: the scheduler is
    /// nearly out of room, which is both why a node fills up and why a drain of its
    /// neighbour may have nowhere to put things.
    /// </summary>
    public bool IsTight => Line.RequestedPercent is > 90;

    /// <summary>
    /// The limits declared on this node add up to more than the node has. That is
    /// ordinary overcommit rather than a fault — it is how most clusters are run, and it
    /// is one of the things people open this card to find out — so it is warn-coloured on
    /// the limits figure alone and never on the row: colouring the whole line would say
    /// the node is in trouble, which it is not until the pods actually use what they are
    /// allowed to.
    /// </summary>
    public bool IsOvercommitted => Line.LimitPercent is > 100;

    private static string Format(double? value, Func<double, string> format) =>
        value is { } number ? format(number) : "";
}

/// <summary>One line of node detail's System card.</summary>
public sealed record NodeSystemRow(string Label, string Value);

/// <summary>One pod on the node, as the Pods tab lists it.</summary>
public sealed class NodePodViewModel
{
    public NodePodViewModel(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        Namespace = pod.Namespace ?? "";
        Name = pod.Name;

        var summary = ResourceStatusSummary.Summarize(pod);
        Status = summary.Status;
        StatusHealth = summary.Health;

        CpuRequestText = NodeDetailTabViewModel.FormatCores(
            NodeResources.EffectiveRequest(pod, NodeResources.Cpu, "requests"));
        MemoryRequestText = NodeDetailTabViewModel.FormatBytes(
            NodeResources.EffectiveRequest(pod, NodeResources.Memory, "requests"));
        AgeText = pod.CreationTimestamp is { } created
            ? RelativeTime.Compact(DateTimeOffset.UtcNow - created)
            : "";
    }

    public string Namespace { get; }

    public string Name { get; }

    public string Status { get; }

    public string StatusHealth { get; }

    public string CpuRequestText { get; }

    public string MemoryRequestText { get; }

    public string AgeText { get; }
}

/// <summary>
/// One node condition, with the health word the status dot is styled on. A view model
/// rather than the Core record straight from <see cref="NodeResources.Conditions"/>,
/// because <c>ResourceHealth</c>'s vocabulary is the App layer's and Core may not know
/// about it (hard rule 1).
/// </summary>
public sealed class NodeConditionViewModel
{
    public NodeConditionViewModel(NodeCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        Condition = condition;
        Health = condition switch
        {
            { IsUnknown: true } => ResourceHealth.Warn,
            { IsProblem: true } => ResourceHealth.Error,
            _ => ResourceHealth.Ok,
        };
    }

    public NodeCondition Condition { get; }

    public string Type => Condition.Type;

    public string Status => Condition.Status;

    /// <summary>The reason and message together — the message alone is often empty, and the reason alone is a token.</summary>
    public string Message => Condition switch
    {
        { Message.Length: > 0, Reason.Length: > 0 } => $"{Condition.Reason} — {Condition.Message}",
        { Message.Length: > 0 } => Condition.Message,
        _ => Condition.Reason,
    };

    public string Health { get; }
}
