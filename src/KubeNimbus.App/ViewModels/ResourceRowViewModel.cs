using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row in the generic live list view — works for any resource kind (built-in
/// or CRD), updated in place as watch events arrive. Columns stay the same
/// across kinds (Namespace/Name/Status/Age); <see cref="Status"/> is a best-effort
/// summary read from whatever status shape the object actually has.
/// </summary>
public sealed partial class ResourceRowViewModel : ObservableObject
{
    public string Key { get; }

    [ObservableProperty]
    private DynamicResource _resource;

    [ObservableProperty]
    private string _namespace;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _statusHealth = "idle"; // ok | warn | error | idle -> Ellipse.statusDot class

    [ObservableProperty]
    private DateTimeOffset? _createdAt;

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

    public ResourceRowViewModel(DynamicResource resource)
    {
        Key = resource.Key;
        _resource = resource;
        _namespace = resource.Namespace ?? "";
        _name = resource.Name;
        _status = "";
        Update(resource);
    }

    public void Update(DynamicResource resource)
    {
        Resource = resource;
        Namespace = resource.Namespace ?? "";
        Name = resource.Name;
        CreatedAt = resource.CreationTimestamp;
        (Status, StatusHealth) = ResourceStatusSummary.Summarize(resource);
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
