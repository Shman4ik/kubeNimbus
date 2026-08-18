using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Node detail: the conditions and taints the node reports about itself, how much of it
/// the scheduler has already promised away (allocatable vs requested), and the pods that
/// are actually on it.
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
        _ = RefreshPodsAsync();
    }

    public static string KeyFor(string clusterName, string name) => $"node:{clusterName}/{name}";

    public override string Key { get; }

    public string NodeName { get; }

    /// <summary>Cluster this node came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    /// <summary>Overview = 0, Pods = 1. Bound by both the segmented strip and the headerless TabControl.</summary>
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

    private void RecomputeResources()
    {
        var summary = NodeResources.Summarize(_row.Resource, _podsOnNode);
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

    public override Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        _cts.Cancel();
        _cts.Dispose();
        return Task.CompletedTask;
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

        Tooltip = line.Allocatable is null
            ? $"{label}: this node did not report an allocatable {line.Resource}."
            : $"{label}\nallocatable {AllocatableText} (capacity {Format(line.Capacity, format)})\n"
              + $"requested {RequestedText} · free {FreeText}"
              + (LimitText.Length > 0 ? $"\nlimits {LimitText}" : "");
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

    /// <summary>0–100, for the bar. The unclamped figure is <see cref="RequestedPercentText"/>.</summary>
    public double RequestedPercentValue { get; }

    public string Tooltip { get; }

    /// <summary>
    /// Over 90% of allocatable requested is the state worth colouring: the scheduler is
    /// nearly out of room, which is both why a node fills up and why a drain of its
    /// neighbour may have nowhere to put things.
    /// </summary>
    public bool IsTight => Line.RequestedPercent is > 90;

    private static string Format(double? value, Func<double, string> format) =>
        value is { } number ? format(number) : "";
}

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
