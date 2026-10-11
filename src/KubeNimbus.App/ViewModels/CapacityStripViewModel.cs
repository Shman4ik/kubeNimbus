using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>Whether measured usage can be read for the capacity strip.</summary>
public enum CapacityUsageState
{
    /// <summary>The metrics API is there and the first poll has not answered yet.</summary>
    Reading,

    /// <summary>A poll answered; the rows carry what it measured.</summary>
    Available,

    /// <summary>The cluster serves no <c>metrics.k8s.io</c>, or serves it broken.</summary>
    NoMetricsApi,

    /// <summary>The metrics API refused this user (403).</summary>
    Refused,
}

/// <summary>Whether the pods behind the requests figure could be read.</summary>
public enum CapacityPodsState
{
    Reading,
    Read,

    /// <summary>The user may not list pods in every namespace (403) — the narrow-RBAC case.</summary>
    Refused,

    /// <summary>The read failed for another reason; its sentence is kept for the tooltip.</summary>
    Failed,
}

/// <summary>
/// The capacity strip above the Nodes list (FEAT-25, #206): node count and readiness, CPU
/// and memory measured against allocatable, and requests against allocatable. Every part the
/// cluster does not let it read says so in its own place — never a zero, never a blank
/// (UI rules 9, 18 and 20). The numbers come from <see cref="ClusterCapacity"/>; this class
/// only words them.
/// </summary>
public sealed partial class CapacityStripViewModel : ObservableObject
{
    /// <summary>Shown above the Nodes list once it has rows; hidden for every other kind.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>"3 nodes · all Ready" or "3 nodes · 1 not Ready".</summary>
    [ObservableProperty]
    private string _nodesText = "";

    [ObservableProperty]
    private bool _hasNotReady;

    [ObservableProperty]
    private string _cpuUsedText = "";

    [ObservableProperty]
    private string _cpuRequestedText = "";

    [ObservableProperty]
    private string _memoryUsedText = "";

    [ObservableProperty]
    private string _memoryRequestedText = "";

    /// <summary>The usage halves are a state sentence rather than a figure, drawn dim.</summary>
    [ObservableProperty]
    private bool _isUsageStated;

    /// <summary>The requests halves are a state sentence rather than a figure, drawn dim.</summary>
    [ObservableProperty]
    private bool _isRequestsStated;

    /// <summary>Why usage or requests say what they say — the server's sentence where there is one.</summary>
    [ObservableProperty]
    private string? _usageTip;

    [ObservableProperty]
    private string? _requestsTip;

    /// <summary>The figures as last summarized — what the tests read.</summary>
    internal ClusterCapacitySummary? Summary { get; private set; }

    internal const string UsageReading = "usage: reading…";
    internal const string UsageNoMetrics = "usage needs metrics-server";
    internal const string UsageRefused = "usage not permitted";
    internal const string UsageNoneYet = "no node has reported usage yet";
    internal const string RequestsReading = "requests: reading…";
    internal const string RequestsRefused = "requests not permitted";
    internal const string RequestsFailed = "requests could not be read";

    internal void Hide()
    {
        IsVisible = false;
        Summary = null;
    }

    /// <summary>Re-words the strip from the Nodes list's rows and what could be read beside them.</summary>
    internal void Update(
        IReadOnlyCollection<DynamicResource> nodes,
        Func<string, (long? CpuNanocores, long? MemoryBytes)?> usage,
        CapacityUsageState usageState,
        string? usageReason,
        IReadOnlyCollection<DynamicResource>? pods,
        CapacityPodsState podsState,
        string? podsReason)
    {
        if (nodes.Count == 0)
        {
            // An empty or refused node list has its own state in the list below; a strip
            // over it would only repeat that (UI rule 20).
            Hide();
            return;
        }

        var summary = ClusterCapacity.Summarize(
            nodes,
            usageState == CapacityUsageState.Available ? usage : null,
            podsState == CapacityPodsState.Read ? pods : null);
        Summary = summary;

        NodesText = $"{summary.Nodes} {(summary.Nodes == 1 ? "node" : "nodes")} · "
            + (summary.NotReady == 0
                ? summary.Nodes == 1 ? "Ready" : "all Ready"
                : $"{summary.NotReady} not Ready");
        HasNotReady = summary.NotReady > 0;

        (CpuUsedText, MemoryUsedText, IsUsageStated, UsageTip) = usageState switch
        {
            CapacityUsageState.Reading => (UsageReading, UsageReading, true, "Waiting for the first answer from metrics.k8s.io."),
            CapacityUsageState.NoMetricsApi => (UsageNoMetrics, UsageNoMetrics, true,
                usageReason ?? "This cluster serves no metrics.k8s.io, so nothing measures what the nodes use. Requests are read from the pods and need no metrics."),
            CapacityUsageState.Refused => (UsageRefused, UsageRefused, true,
                $"metrics.k8s.io refused this user: {usageReason ?? "forbidden"}"),
            _ when summary.Cpu.UsedFrom == 0 => (UsageNoneYet, UsageNoneYet, true,
                "metrics-server answered with no node in it; a node that just joined is scraped within a minute."),
            _ => (UsedText(summary.Cpu, FormatCores, FormatCoresTotal, summary.Nodes), UsedText(summary.Memory, FormatBytes, FormatBytes, summary.Nodes), false,
                "Measured by metrics-server over its last window, against allocatable."),
        };

        (CpuRequestedText, MemoryRequestedText, IsRequestsStated, RequestsTip) = podsState switch
        {
            CapacityPodsState.Reading => (RequestsReading, RequestsReading, true, "Reading the pods in every namespace."),
            CapacityPodsState.Refused => (RequestsRefused, RequestsRefused, true,
                $"Requests are summed over the pods on these nodes, and this user may not list pods in every namespace: {podsReason ?? "forbidden"}"),
            CapacityPodsState.Failed => (RequestsFailed, RequestsFailed, true, podsReason),
            _ => (RequestedText(summary.Cpu, FormatCores, FormatCoresTotal), RequestedText(summary.Memory, FormatBytes, FormatBytes), false,
                "What the pods bound to these nodes request, by the scheduler's own formula, against allocatable."),
        };

        IsVisible = true;
    }

    private static string UsedText(ClusterResourceTotals totals, Func<double, string> format, Func<double, string> formatTotal, int nodes)
    {
        var used = format(totals.Used!.Value);
        var text = totals.UsedAllocatable is { } of
            ? $"{used} of {formatTotal(of)} used ({Percent(totals.UsedPercent)})"
            : $"{formatTotal(totals.Used!.Value)} used";

        // A node metrics-server has not scraped is out of both sides of the share, and the
        // sentence says the share is of fewer nodes than the count beside it.
        return totals.UsedFrom < nodes ? $"{text} on {totals.UsedFrom} of {nodes} nodes" : text;
    }

    private static string RequestedText(ClusterResourceTotals totals, Func<double, string> format, Func<double, string> formatTotal) =>
        totals.Allocatable is { } allocatable
            ? $"{format(totals.Requested!.Value)} of {formatTotal(allocatable)} requested ({Percent(totals.RequestedPercent)})"
            : $"{formatTotal(totals.Requested!.Value)} requested; no node reported allocatable";

    private static string Percent(double? percent) => percent switch
    {
        null => "—",
        > 0 and < 1 => "<1%",
        { } p => string.Create(CultureInfo.InvariantCulture, $"{p:0}%"),
    };

    /// <summary>A CPU figure as the list's CPU column prints it: "250m" below a core, "1.25" above.</summary>
    private static string FormatCores(double cores) => Quantity.FormatCpu((long)Math.Round(cores * 1e9));

    /// <summary>The same with its unit, for the figure that ends the phrase: "8 cores", "500m".</summary>
    private static string FormatCoresTotal(double cores) =>
        cores >= 1 ? $"{FormatCores(cores)} {(FormatCores(cores) == "1" ? "core" : "cores")}" : FormatCores(cores);

    private static string FormatBytes(double bytes) => Quantity.FormatMemory((long)Math.Round(bytes));
}
