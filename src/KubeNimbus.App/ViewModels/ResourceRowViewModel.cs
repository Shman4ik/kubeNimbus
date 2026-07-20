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

    /// <summary>Live "120m · 84Mi" usage readout from metrics.k8s.io — empty when unavailable or (for
    /// non-Pod kinds/most rows before the first refresh tick) simply not populated.</summary>
    [ObservableProperty]
    private string _usageDisplay = "";

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

    /// <summary>Applies a metrics.k8s.io snapshot for this pod (see <see cref="ClusterTabViewModel"/>'s
    /// poll timer), or clears the readout when metrics briefly have no entry for it.</summary>
    public void UpdateMetrics(DynamicResource? podMetrics) =>
        UsageDisplay = podMetrics is null ? "" : ResourceFormat.Combined(podMetrics.TotalCpuCores(), podMetrics.TotalMemoryBytes());
}
