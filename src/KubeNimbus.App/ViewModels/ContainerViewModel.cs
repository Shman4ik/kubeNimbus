using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One <c>spec.containers[].ports[]</c> entry worth forwarding to. Only TCP appears
/// here — the port-forward protocol has no UDP.
/// </summary>
/// <param name="Number">The container port.</param>
/// <param name="Name">The port's declared name ("http", "metrics"), or null.</param>
public sealed record ContainerPort(int Number, string? Name)
{
    /// <summary>What the picker shows: "8080 · http", or just the number when unnamed.</summary>
    public string Display => string.IsNullOrEmpty(Name) ? Number.ToString() : $"{Number} · {Name}";
}

/// <summary>
/// Which of a pod's three container lists this one came from. Init and ephemeral
/// containers used to be invisible in pod detail entirely, so an init container that
/// would not start — one of the commonest reasons a pod never runs — could not be
/// inspected at all, and its logs were unreachable.
/// </summary>
public enum ContainerRole
{
    App,
    Init,
    Ephemeral,
}

/// <summary>One container row in the pod detail view (spec + live status + measured usage merged).</summary>
public sealed partial class ContainerViewModel(string name, string image) : ObservableObject
{
    public string Name { get; } = name;

    public string Image { get; } = image;

    public ContainerRole Role { get; init; } = ContainerRole.App;

    /// <summary>Badge on the chip: app containers get none, the other two say what they are.</summary>
    public string RoleLabel => Role switch
    {
        ContainerRole.Init => "init",
        ContainerRole.Ephemeral => "debug",
        _ => "",
    };

    public bool HasRoleLabel => Role != ContainerRole.App;

    [ObservableProperty]
    private bool _ready;

    [ObservableProperty]
    private int _restartCount;

    /// <summary>
    /// The container's live state, preferring the <c>waiting</c>/<c>terminated</c>
    /// reason over the bare state name — "CrashLoopBackOff", not "waiting". It was
    /// parsed and then bound to nothing, so a crash-looping container looked exactly
    /// like a healthy one apart from a restart count, and nothing on screen pointed at
    /// the Previous button, which is the only place its actual failure is written down.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStateLabel))]
    private string _state = "Unknown";

    /// <summary>Worth showing on the chip? A plainly running container needs no label.</summary>
    public bool HasStateLabel => State is not ("running" or "Unknown" or "");

    /// <summary>
    /// The container's declared TCP ports, names included. The name is half the
    /// information — a pod declaring <c>http 8080</c> and <c>metrics 9090</c> is
    /// self-documenting, and picking between two bare numbers is not. These used to
    /// be collected and then thrown away in favour of a hardcoded 8080.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<ContainerPort> _ports = [];

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

    /// <summary>
    /// This container's rolling usage window, behind the per-container charts in
    /// pod detail's Usage tab. Same lifetime as the container row itself.
    /// </summary>
    public UsageHistory History { get; } = new();

    /// <summary>Chart series, republished per poll (a mutated ring buffer raises no notification).</summary>
    [ObservableProperty]
    private IReadOnlyList<double?> _cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double?> _memorySeries = [];

    [ObservableProperty]
    private string _cpuChartTooltip = "";

    [ObservableProperty]
    private string _memoryChartTooltip = "";

    /// <summary>Peak over the window — the number a usage graph is actually read for.</summary>
    [ObservableProperty]
    private string _peakCpuText = "—";

    [ObservableProperty]
    private string _peakMemoryText = "—";

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

    /// <summary><paramref name="at"/> defaults to now; only the screenshot harness stamps samples explicitly.</summary>
    public void ApplyUsage(long? cpuNanocores, long? memoryBytes, DateTimeOffset? at = null)
    {
        CpuNanocores = cpuNanocores;
        MemoryBytes = memoryBytes;

        History.Add(cpuNanocores, memoryBytes, at);
        CpuSeries = History.CpuSeries();
        MemorySeries = History.MemorySeries();
        PeakCpuText = Quantity.FormatCpu(History.PeakCpuNanocores);
        PeakMemoryText = Quantity.FormatMemory(History.PeakMemoryBytes);
        CpuChartTooltip = UsageFormat.Tooltip($"{Name} CPU", Quantity.FormatCpu(cpuNanocores), PeakCpuText, History);
        MemoryChartTooltip = UsageFormat.Tooltip($"{Name} Mem", Quantity.FormatMemory(memoryBytes), PeakMemoryText, History);
    }
}
