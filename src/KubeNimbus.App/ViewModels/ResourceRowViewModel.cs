using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row in the generic live list view — works for any resource kind (built-in
/// or CRD), updated in place as watch events arrive. The columns are the ones
/// kubectl shows (Namespace/Name/Ready/Status/Restarts/Age, plus a kind-specific
/// Details); which of them apply to the selected kind is decided by
/// <see cref="ResourceStatusSummary"/> and applied in
/// <see cref="Views.ClusterTabView"/>'s code-behind.
/// </summary>
public sealed partial class ResourceRowViewModel : ObservableObject
{
    public string Key { get; }

    /// <summary>
    /// Which cluster served this row, in the aggregated fleet view; empty for an
    /// ordinary single-cluster list. Also part of <see cref="Key"/>, because the same
    /// namespace/name exists on every cluster in a fleet and two of those are two rows.
    /// </summary>
    public string ClusterName { get; }

    [ObservableProperty]
    private DynamicResource _resource;

    [ObservableProperty]
    private string _namespace;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _statusHealth = ResourceHealth.Idle; // -> Ellipse.statusDot / Border.statusPill class

    /// <summary>kubectl's READY column ("2/3") — empty for kinds with no readiness notion.</summary>
    [ObservableProperty]
    private string _readyText = "";

    /// <summary>
    /// kubectl's RESTARTS column, including its "(43m ago)" suffix. A restart count
    /// with no age is nearly useless: 200 restarts that stopped yesterday and 200
    /// still accumulating are the same number and completely different problems.
    /// </summary>
    [ObservableProperty]
    private string _restartsText = "0";

    /// <summary>
    /// What kubectl shows in place of a status for kinds that have none — a Service's
    /// type/cluster-IP/ports, a ConfigMap's key count. Empty (and its column hidden)
    /// for kinds whose Status column already carries the story.
    /// </summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private DateTimeOffset? _createdAt;

    /// <summary>
    /// Compact age ("5m", "2h", "3d", "21d"). Recomputed by
    /// <see cref="RefreshTimes"/> off the list view's shared clock rather than
    /// stored, because age changes with no watch event to trigger it.
    /// </summary>
    [ObservableProperty]
    private string _ageText = "";

    /// <summary>The exact creation timestamp, for the Age cell's tooltip — the
    /// compact form is for scanning, but "when exactly" is a real question.</summary>
    [ObservableProperty]
    private string _ageTooltip = "";

    private int _restarts;
    private DateTimeOffset? _lastRestartAt;

    /// <summary>
    /// Measured CPU/memory for this row, when the kind has any (pods and nodes)
    /// and the cluster runs metrics-server. Rendered as text because the columns
    /// are display-only; "—" is the deliberate stand-in for "not reported yet",
    /// which is different from zero.
    /// </summary>
    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    /// <summary>
    /// This row's rolling usage window (30 min at the 15s poll cadence), behind the
    /// sparkline drawn in the CPU/Memory cells. Lives per row so it survives watch
    /// events (which update the row in place) and dies with the row when the kind or
    /// namespace changes — exactly the lifetime the graph should have.
    /// </summary>
    public UsageHistory History { get; } = new();

    /// <summary>
    /// Chart series, re-published as a fresh array on each poll: the chart binds to
    /// the property, and a ring buffer mutated in place raises no change
    /// notification. 120 doubles per row per poll is cheaper than any observable
    /// collection plumbing would be.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<double?> _cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double?> _memorySeries = [];

    [ObservableProperty]
    private string _cpuTooltip = "No CPU samples yet.";

    [ObservableProperty]
    private string _memoryTooltip = "No memory samples yet.";

    public ResourceRowViewModel(DynamicResource resource, string clusterName = "")
    {
        ClusterName = clusterName;
        Key = KeyFor(clusterName, resource.Key);
        _resource = resource;
        _namespace = resource.Namespace ?? "";
        _name = resource.Name;
        _status = "";
        Update(resource);
    }

    /// <summary>
    /// Row identity: the resource's own <c>namespace/name</c> key, qualified by cluster
    /// when there is one. Shared with the metrics poll so its samples land on the right
    /// rows in fleet mode.
    /// </summary>
    public static string KeyFor(string clusterName, string resourceKey) =>
        clusterName.Length == 0 ? resourceKey : $"{clusterName}/{resourceKey}";

    public void Update(DynamicResource resource)
    {
        Resource = resource;
        Namespace = resource.Namespace ?? "";
        Name = resource.Name;
        CreatedAt = resource.CreationTimestamp;
        AgeTooltip = resource.CreationTimestamp is { } created
            ? $"Created {created.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "";

        var summary = ResourceStatusSummary.Summarize(resource);
        Status = summary.Status;
        StatusHealth = summary.Health;
        ReadyText = summary.Ready;
        Details = summary.Details;
        _restarts = summary.Restarts;
        _lastRestartAt = summary.LastRestartAt;
        RefreshTimes();
    }

    /// <summary>
    /// Recomputes the two cells whose text is a function of wall-clock rather than of
    /// the object — Age, and the "(43m ago)" on Restarts. Driven by one shared timer
    /// in <see cref="Views.ClusterTabView"/>: a timer per row would mean thousands of
    /// them on a busy cluster. Assignments are no-ops when the rendered string hasn't
    /// changed (<c>ObservableObject.SetProperty</c> compares first), so a tick over a
    /// list of day-old pods raises no change notifications at all.
    /// </summary>
    public void RefreshTimes()
    {
        var now = DateTimeOffset.UtcNow;
        AgeText = CreatedAt is { } created ? RelativeTime.Compact(now - created) : "";
        RestartsText = _restarts == 0
            ? "0"
            : _lastRestartAt is { } last
                ? $"{_restarts} ({RelativeTime.Compact(now - last)} ago)"
                : $"{_restarts}";
    }

    /// <summary>
    /// Applies one metrics.k8s.io sample; nulls render as "—". <paramref name="at"/>
    /// defaults to now — it is only passed explicitly by the screenshot harness,
    /// which needs a realistic time axis without waiting out real poll intervals.
    /// </summary>
    public void ApplyUsage(long? cpuNanocores, long? memoryBytes, DateTimeOffset? at = null)
    {
        History.Add(cpuNanocores, memoryBytes, at);
        CpuText = Quantity.FormatCpu(cpuNanocores);
        MemoryText = Quantity.FormatMemory(memoryBytes);
        RefreshSeries();
    }

    /// <summary>
    /// Records that this poll had no sample for the row (the pod is too young to be
    /// aggregated, or it vanished from the metrics response). The reading goes into
    /// the history as a gap rather than a zero, and rather than being dropped — a
    /// subject that stopped reporting should break the line, not shift the graph.
    /// </summary>
    public void ClearUsage(DateTimeOffset? at = null)
    {
        History.Add(null, null, at);
        CpuText = "—";
        MemoryText = "—";
        RefreshSeries();
    }

    private void RefreshSeries()
    {
        CpuSeries = History.CpuSeries();
        MemorySeries = History.MemorySeries();
        CpuTooltip = UsageFormat.Tooltip("CPU", CpuText, Quantity.FormatCpu(History.PeakCpuNanocores), History);
        MemoryTooltip = UsageFormat.Tooltip("Mem", MemoryText, Quantity.FormatMemory(History.PeakMemoryBytes), History);
    }
}

/// <summary>
/// kubectl's AGE column, at one unit instead of two. kubectl's own
/// <c>duration.HumanDuration</c> mixes units below its thresholds ("3d2h",
/// "5m30s"); in a list two hundred rows deep that trailing unit is noise that
/// differs on every row and stops the column lining up, and the exact timestamp
/// is one tooltip away. If exact kubectl parity ever matters more than that,
/// this is the one function to change.
/// </summary>
internal static class RelativeTime
{
    public static string Compact(TimeSpan elapsed)
    {
        // Clock skew, or a creationTimestamp in the future: "0s", never "-3s".
        if (elapsed.Ticks <= 0)
        {
            return "0s";
        }

        return elapsed switch
        {
            { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h",
            { TotalDays: < 365 } => $"{(int)elapsed.TotalDays}d",
            _ => $"{(int)(elapsed.TotalDays / 365)}y",
        };
    }
}
