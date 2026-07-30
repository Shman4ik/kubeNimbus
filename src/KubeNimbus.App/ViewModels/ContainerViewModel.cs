using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One container row in the pod detail view (spec + live status + measured usage merged).</summary>
public sealed partial class ContainerViewModel(string name, string image) : ObservableObject
{
    public string Name { get; } = name;

    public string Image { get; } = image;

    [ObservableProperty]
    private bool _ready;

    [ObservableProperty]
    private int _restartCount;

    [ObservableProperty]
    private string _state = "Unknown";

    [ObservableProperty]
    private IReadOnlyList<int> _tcpPorts = [];

    /// <summary>Measured usage from metrics.k8s.io; null until the first poll (or forever without metrics-server).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageSummary))]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _cpuNanocores;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageSummary))]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _memoryBytes;

    /// <summary>Requests/limits from the pod spec — the context that makes a usage number mean something.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _cpuRequestNanocores;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _cpuLimitNanocores;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _memoryRequestBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesTooltip))]
    private long? _memoryLimitBytes;

    public bool HasUsage => CpuNanocores is not null || MemoryBytes is not null;

    /// <summary>Compact one-liner under the container name: "12m · 45 MiB".</summary>
    public string UsageSummary =>
        HasUsage ? $"{Quantity.FormatCpu(CpuNanocores)} · {Quantity.FormatMemory(MemoryBytes)}" : "";

    /// <summary>Usage against requests/limits, for the container row's tooltip.</summary>
    public string ResourcesTooltip =>
        $"{Name}\nCPU  {Line(Quantity.FormatCpu(CpuNanocores), Quantity.FormatCpu(CpuRequestNanocores), Quantity.FormatCpu(CpuLimitNanocores), Quantity.Percent(CpuNanocores, CpuLimitNanocores))}"
        + $"\nMem  {Line(Quantity.FormatMemory(MemoryBytes), Quantity.FormatMemory(MemoryRequestBytes), Quantity.FormatMemory(MemoryLimitBytes), Quantity.Percent(MemoryBytes, MemoryLimitBytes))}";

    private static string Line(string used, string request, string limit, double? percentOfLimit)
    {
        var text = $"{used}  (request {request}, limit {limit})";
        return percentOfLimit is { } percent ? $"{text} — {percent:0}% of limit" : text;
    }

    public void ApplyUsage(long? cpuNanocores, long? memoryBytes)
    {
        CpuNanocores = cpuNanocores;
        MemoryBytes = memoryBytes;
    }
}
