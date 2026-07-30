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

    /// <summary>Applies one metrics.k8s.io sample; nulls render as "—".</summary>
    public void ApplyUsage(long? cpuNanocores, long? memoryBytes)
    {
        CpuText = Quantity.FormatCpu(cpuNanocores);
        MemoryText = Quantity.FormatMemory(memoryBytes);
    }

    /// <summary>Clears usage back to "—" (kind switched away from a metered kind, or metrics went away).</summary>
    public void ClearUsage()
    {
        CpuText = "—";
        MemoryText = "—";
    }
}
