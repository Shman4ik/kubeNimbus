using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Pod detail: containers, live status, live log streaming (follow/container
/// picker/previous/search/timestamps/wrap/copy/download), environment
/// variables (with on-demand Secret/ConfigMap reveal), live CPU/Mem usage plus
/// its over-time graphs (metrics.k8s.io, when present) and events. Tracks the same
/// <see cref="ResourceRowViewModel"/> instance the live list uses, so
/// container status stays current without a second watch.
/// </summary>
public sealed partial class PodDetailTabViewModel : InspectorTabViewModelBase
{
    /// <summary>
    /// Log scrollback cap. Read from settings once per tab rather than held as a
    /// constant: someone reading a crash loop wants far more history than someone
    /// watching a chatty ingress, and 4000 was a guess that suited neither. Read at
    /// construction — a live tab does not re-trim itself when the preference changes,
    /// which would mean a setting could silently discard buffered lines someone was
    /// mid-way through reading.
    /// </summary>
    private readonly int _maxLogLines = App.LoadSettings().LogBufferLines;

    /// <summary>
    /// Same cadence as the list view, and from the same setting — metrics.k8s.io
    /// aggregates over ~30s, so this is a "how quiet do you want to be" dial rather
    /// than a resolution one.
    /// </summary>
    private static TimeSpan MetricsPollInterval =>
        TimeSpan.FromSeconds(App.LoadSettings().MetricsPollSeconds);

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceRowViewModel _row;
    private readonly Action<InspectorTabViewModelBase> _openTab;
    private readonly Func<OwnerRef, string?, Task> _openOwner;
    private readonly List<LogLineViewModel> _allLogLines = [];
    private readonly Dictionary<string, Task<DynamicResource?>> _secretConfigMapCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _logCts;
    private readonly CancellationTokenSource _metricsCts = new();

    public override string Key { get; }

    public string PodNamespace { get; }

    public string PodName { get; }

    /// <summary>Cluster this pod came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    public ObservableCollection<ContainerViewModel> Containers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecCommand))]
    [NotifyCanExecuteChangedFor(nameof(PortForwardCommand))]
    [NotifyPropertyChangedFor(nameof(LogPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasLogPlaceholder))]
    private ContainerViewModel? _selectedContainer;

    /// <summary>Filtered (by <see cref="LogSearchText"/>) view over the buffered log lines — this is what's rendered.</summary>
    public ObservableCollection<LogLineViewModel> LogLines { get; } = [];

    [ObservableProperty]
    private bool _isFollowingLogs;

    /// <summary>True while showing the previous (crashed/restarted) container instance's logs — a one-shot fetch, not a follow.</summary>
    [ObservableProperty]
    private bool _isShowingPreviousLogs;

    [ObservableProperty]
    private string _logSearchText = "";

    [ObservableProperty]
    private bool _showLogTimestamps;

    [ObservableProperty]
    private bool _wrapLogLines;

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    /// <summary>The selected container's env vars — literal values inline, Secret/ConfigMap refs shown
    /// unresolved until <see cref="RevealEnvVarCommand"/> is invoked on that row.</summary>
    public ObservableCollection<EnvVarViewModel> EnvironmentVars { get; } = [];

    /// <summary>
    /// The selected container's <c>envFrom</c> sources. Still reference-only — the pod
    /// spec doesn't declare individual keys for these — but each one now opens the
    /// Secret/ConfigMap it names. It used to be one dead grey line with no way to
    /// reach the object, which is the only thing anyone wanted from it.
    /// </summary>
    public ObservableCollection<EnvFromSourceViewModel> EnvFromSources { get; } = [];

    public IReadOnlyList<OwnerRef> Owners => _row.Resource.OwnerReferences;

    // ------------------------------------------------------------- overview
    //
    // The Overview tab: the pod's own conditions, tolerations, node selector, QoS and
    // priority class, and the selected container's probes. Everything here is read out
    // of the object the list already holds (see PodDetails in Core) — no extra GET, no
    // second watch — so it costs a parse per changed tick and nothing else.

    public ObservableCollection<PodConditionViewModel> Conditions { get; } = [];

    /// <summary>Every toleration, the two the DefaultTolerationSeconds plugin adds included — see <see cref="PodDetails.Placement"/>.</summary>
    public ObservableCollection<PodToleration> Tolerations { get; } = [];

    public ObservableCollection<PodNodeSelectorTerm> NodeSelector { get; } = [];

    /// <summary>The selected container's liveness/readiness/startup probes, in that order.</summary>
    public ObservableCollection<ContainerProbe> Probes { get; } = [];

    /// <summary>Guaranteed / Burstable / BestEffort, as the API server computed it. Empty when the object carries none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQosClass))]
    private string _qosClass = "";

    public bool HasQosClass => QosClass.Length > 0;

    /// <summary>The priority class name and the number the scheduler compares ("high-priority (100000)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPriority))]
    private string _priorityText = "";

    public bool HasPriority => PriorityText.Length > 0;

    [ObservableProperty]
    private bool _hasConditions;

    [ObservableProperty]
    private bool _hasTolerations;

    [ObservableProperty]
    private bool _hasNodeSelector;

    [ObservableProperty]
    private bool _hasProbes;

    /// <summary>
    /// Whole-pod usage over time (sum across containers), behind the Usage tab's two
    /// headline charts. The per-container windows hang off each
    /// <see cref="ContainerViewModel"/>.
    /// </summary>
    public UsageHistory PodHistory { get; } = new();

    [ObservableProperty]
    private IReadOnlyList<double?> _podCpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double?> _podMemorySeries = [];

    [ObservableProperty]
    private string _podCpuText = "—";

    [ObservableProperty]
    private string _podMemoryText = "—";

    [ObservableProperty]
    private string _podPeakCpuText = "—";

    [ObservableProperty]
    private string _podPeakMemoryText = "—";

    [ObservableProperty]
    private string _podCpuTooltip = "";

    [ObservableProperty]
    private string _podMemoryTooltip = "";

    /// <summary>Caption under the charts: how far back the graph goes ("last 7 min · 28 samples").</summary>
    [ObservableProperty]
    private string _usageWindowCaption = "collecting…";

    /// <summary>True once at least one poll landed — the Usage tab shows a "collecting" state until then (UI rule 8).</summary>
    [ObservableProperty]
    private bool _hasUsageSamples;

    /// <summary>
    /// True when this cluster has no usable metrics.k8s.io. Distinguishes "no
    /// metrics-server here" from "samples haven't arrived yet", which look
    /// identical otherwise and lead to very different next steps.
    /// </summary>
    [ObservableProperty]
    private bool _isMetricsUnavailable;

    public string UsagePollHint =>
        $"metrics.k8s.io has no watch endpoint, so usage is polled every {MetricsPollInterval.TotalSeconds:0}s. "
        + "History is kept for this session only — kubeNimbus is a viewer, not a time-series store.";

    /// <summary>Which of the Logs/Env/Events/Usage tabs is showing — lets the screenshot harness (and any future deep link) pick a specific tab.</summary>
    [ObservableProperty]
    private int _selectedDetailTabIndex;

    /// <summary>
    /// Tab identity, qualified by cluster when the row came from an aggregated fleet
    /// list: two clusters routinely hold a pod with the same namespace/name, and
    /// without the qualifier the second one would reuse the first one's tab.
    /// </summary>
    public static string KeyFor(string clusterName, string? @namespace, string name) =>
        clusterName.Length == 0 ? $"pod:{@namespace}/{name}" : $"pod@{clusterName}:{@namespace}/{name}";

    public PodDetailTabViewModel(
        ClusterClient? client,
        ResourceRowViewModel row,
        Action<InspectorTabViewModelBase> openTab,
        Func<OwnerRef, string?, Task> openOwner,
        string clusterName = "")
        : base(
            clusterName.Length == 0 ? $"Pod/{row.Name}" : $"Pod/{row.Name} · {clusterName}",
            isDemo: client is null)
    {
        _client = client;
        _row = row;
        _openTab = openTab;
        _openOwner = openOwner;
        PodNamespace = row.Namespace;
        PodName = row.Name;
        ClusterName = clusterName;
        Key = KeyFor(clusterName, PodNamespace, PodName);

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();

        // Logs start on open, with no click. Double-click on a pod is documented as
        // "pod → logs" (it is in the F1 cheat sheet), and what it actually landed on
        // was a blank card with no message and two toggles that did nothing. Opening
        // straight into the stream is also what kubectl logs, k9s and Lens all do.
        StartLogs();

        if (client is null)
        {
            // No poll and no event fetch on the demo cluster: both usage and events come
            // straight from the shipped dataset, through the same entry points a real
            // poll and a real fetch land on.
            LoadDemoEvents();
            DemoUsage.SeedPod(this);
            return;
        }

        _ = RefreshEventsAsync();
        _ = Task.Run(() => PollMetricsAsync(_metricsCts.Token), _metricsCts.Token);
    }

    /// <summary>Events for this pod, from the demo dataset rather than the API server.</summary>
    private void LoadDemoEvents()
    {
        Events.Clear();
        foreach (var e in DemoData.Events)
        {
            Events.Add(new EventRowViewModel(e));
        }
    }

    /// <summary>
    /// Polls this pod's per-container usage. Silently does nothing on clusters
    /// without metrics-server — the container rows just don't show a usage line.
    /// </summary>
    private async Task PollMetricsAsync(CancellationToken token)
    {
        if (_client is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(MetricsPollInterval);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var metrics = await _client.GetPodMetricsAsync(PodNamespace, PodName, token);
                    if (metrics is not null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ApplyMetrics(metrics));
                    }
                }
                catch (MetricsUnavailableException)
                {
                    // No metrics API on this cluster (or it is registered but dead):
                    // stop asking, and say so in the Usage tab rather than leaving it
                    // looking like samples are still on the way.
                    await Dispatcher.UIThread.InvokeAsync(() => IsMetricsUnavailable = true);
                    return;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // transient — keep the last sample and retry on the next tick
                }

                await timer.WaitForNextTickAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // normal when the tab closes
        }
    }

    /// <summary>
    /// Folds one poll's reading into the per-container and whole-pod windows. Public
    /// because it is the single entry point for a sample — the screenshot harness
    /// feeds it fixture <see cref="PodMetrics"/> rather than re-deriving the chart
    /// state, so what gets rendered offline is what a real poll would produce.
    /// </summary>
    public void ApplyMetrics(PodMetrics metrics, DateTimeOffset? at = null)
    {
        foreach (var container in Containers)
        {
            // A container the response didn't mention (init containers, or one that
            // hasn't been aggregated yet) records a gap, so every container's series
            // stays index-aligned with the same poll ticks.
            var sample = metrics.Containers.FirstOrDefault(c => c.Name == container.Name);
            container.ApplyUsage(sample?.CpuNanocores, sample?.MemoryBytes, at);
        }

        PodHistory.Add(metrics.CpuNanocores, metrics.MemoryBytes, at);
        PodCpuSeries = PodHistory.CpuSeries();
        PodMemorySeries = PodHistory.MemorySeries();
        PodCpuText = Quantity.FormatCpu(metrics.CpuNanocores);
        PodMemoryText = Quantity.FormatMemory(metrics.MemoryBytes);
        PodPeakCpuText = Quantity.FormatCpu(PodHistory.PeakCpuNanocores);
        PodPeakMemoryText = Quantity.FormatMemory(PodHistory.PeakMemoryBytes);
        PodCpuTooltip = UsageFormat.Tooltip("Pod CPU", PodCpuText, PodPeakCpuText, PodHistory);
        PodMemoryTooltip = UsageFormat.Tooltip("Pod Mem", PodMemoryText, PodPeakMemoryText, PodHistory);
        UsageWindowCaption = UsageFormat.WindowCaption(PodHistory);
        HasUsageSamples = true;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceRowViewModel.Resource) or null)
        {
            Dispatcher.UIThread.Post(RefreshFromRow);
        }
    }

    partial void OnSelectedContainerChanged(ContainerViewModel? value)
    {
        RefreshEnvironment();

        // Probes are container-scoped, and the strip above the tabs is already their
        // selector — the same relationship the Environment tab has with it, which is why
        // the Overview tab needs no picker of its own (UI rule 10).
        RefreshOverview();

        // The stream follows the picker. It used not to: switching container left the
        // old container's lines arriving under a header that named the new one, and
        // Download saved them under the new one's filename. Nothing about the pane
        // said which container you were actually reading.
        if (value is null)
        {
            StopLogs();
            return;
        }

        if (IsShowingPreviousLogs)
        {
            LoadPreviousLogs();
        }
        else
        {
            StartLogs();
        }
    }

    /// <summary>
    /// Identity of what <see cref="RefreshOverview"/> last rendered. A watch tick on a
    /// healthy pod is almost always a status refresh that changes none of these fields,
    /// and rebuilding four ItemsControls per tick throws away scroll position and any
    /// text selection someone was in the middle of making — the same reason the
    /// Environment tab is signature-guarded.
    /// </summary>
    private int? _overviewSignature;

    /// <summary>
    /// Rebuilds the Overview tab from the object the row already holds. Conditions do
    /// change with the watch — that is the point of showing them — so this is guarded on
    /// the fields' own text rather than skipped.
    /// </summary>
    private void RefreshOverview()
    {
        var raw = _row.Resource.Raw;
        var spec = raw.TryGetProperty("spec", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var status = raw.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object ? st : default;

        var probeSource = SelectedContainer is { } selected && PodDetails.ContainerSpec(spec, selected.Name) is { } cs
            ? ProbeText(cs)
            : "";

        var signature = HashCode.Combine(
            RawTextOf(status, "conditions"),
            RawTextOf(spec, "tolerations"),
            RawTextOf(spec, "nodeSelector"),
            RawTextOf(status, "qosClass"),
            RawTextOf(spec, "priorityClassName"),
            RawTextOf(spec, "priority"),
            probeSource);

        if (_overviewSignature == signature)
        {
            return;
        }

        _overviewSignature = signature;

        Conditions.Clear();
        foreach (var condition in PodDetails.Conditions(_row.Resource))
        {
            Conditions.Add(new PodConditionViewModel(condition));
        }

        var placement = PodDetails.Placement(_row.Resource);
        QosClass = placement.QosClass;
        PriorityText = placement.PriorityDisplay;

        Tolerations.Clear();
        foreach (var toleration in placement.Tolerations)
        {
            Tolerations.Add(toleration);
        }

        NodeSelector.Clear();
        foreach (var term in placement.NodeSelector)
        {
            NodeSelector.Add(term);
        }

        Probes.Clear();
        if (SelectedContainer is { } container)
        {
            foreach (var probe in PodDetails.Probes(_row.Resource, container.Name))
            {
                Probes.Add(probe);
            }
        }

        HasConditions = Conditions.Count > 0;
        HasTolerations = Tolerations.Count > 0;
        HasNodeSelector = NodeSelector.Count > 0;
        HasProbes = Probes.Count > 0;
    }

    /// <summary>The three probe blocks as text — the half of a container spec the Overview tab reads.</summary>
    private static string ProbeText(JsonElement containerSpec) =>
        string.Concat(
            RawTextOf(containerSpec, "livenessProbe"),
            RawTextOf(containerSpec, "readinessProbe"),
            RawTextOf(containerSpec, "startupProbe"));

    private static string RawTextOf(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
            ? value.GetRawText()
            : "";

    /// <summary>
    /// Re-reads everything this pane derives from the pod object. <c>internal</c> rather
    /// than private for the same reason <c>ClusterTabViewModel.Apply</c> is: the watch's
    /// own path posts it to the UI thread, and a test that cannot pump a dispatcher has to
    /// be able to deliver a tick the way the watch does rather than against a copy of it.
    /// </summary>
    internal void RefreshFromRow()
    {
        var raw = _row.Resource.Raw;
        var status = raw.TryGetProperty("status", out var s) ? s : default;

        // All three status arrays, because all three kinds of container are listed
        // below. An init container that will not start is one of the most common
        // reasons a pod never runs, and its logs were unreachable entirely.
        var statuses = new Dictionary<string, (bool Ready, int Restarts, string State)>(StringComparer.Ordinal);
        ReadContainerStatuses(status, "containerStatuses", statuses);
        ReadContainerStatuses(status, "initContainerStatuses", statuses);
        ReadContainerStatuses(status, "ephemeralContainerStatuses", statuses);

        if (!raw.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            // Conditions live in status, so an object with no spec still has an Overview
            // worth rendering — and one with no spec is exactly the object someone is
            // trying to work out what happened to.
            RefreshOverview();
            return;
        }

        // Order matters: init containers run first and read first. Ephemeral ones are
        // debug attachments and come last.
        ReadContainerSpecs(spec, "initContainers", ContainerRole.Init, statuses);
        ReadContainerSpecs(spec, "containers", ContainerRole.App, statuses);
        ReadContainerSpecs(spec, "ephemeralContainers", ContainerRole.Ephemeral, statuses);

        // The first *app* container, not the first row: with init containers present
        // the list starts on one that has already exited, and `kubectl logs` with no
        // -c picks the first app container for the same reason.
        SelectedContainer ??= Containers.FirstOrDefault(c => c.Role == ContainerRole.App) ?? Containers.FirstOrDefault();

        RefreshEnvironment();
        RefreshOverview();
    }

    private static void ReadContainerStatuses(
        JsonElement status, string arrayName, Dictionary<string, (bool Ready, int Restarts, string State)> into)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var c in array.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var ready = c.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
            var restarts = c.TryGetProperty("restartCount", out var rc) && rc.TryGetInt32(out var count) ? count : 0;

            // state is a one-of: running / waiting / terminated. The waiting reason is
            // the useful half of it ("CrashLoopBackOff" beats "waiting"), and it is what
            // points a user at the Previous button.
            var state = "Unknown";
            if (c.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in st.EnumerateObject())
                {
                    state = prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String
                        && reason.GetString() is { Length: > 0 } reasonText
                            ? reasonText
                            : prop.Name;
                    break;
                }
            }

            into[name] = (ready, restarts, state);
        }
    }

    private void ReadContainerSpecs(
        JsonElement spec,
        string arrayName,
        ContainerRole role,
        Dictionary<string, (bool Ready, int Restarts, string State)> statuses)
    {
        if (!spec.TryGetProperty(arrayName, out var containers) || containers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var c in containers.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var image = c.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";

            var ports = new List<ContainerPort>();
            if (c.TryGetProperty("ports", out var portsEl) && portsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in portsEl.EnumerateArray())
                {
                    if (p.TryGetProperty("containerPort", out var cp) && cp.TryGetInt32(out var port)
                        && (!p.TryGetProperty("protocol", out var proto) || proto.GetString() is null or "TCP"))
                    {
                        var portName = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                        ports.Add(new ContainerPort(port, portName));
                    }
                }
            }

            var existing = Containers.FirstOrDefault(x => x.Name == name);
            if (existing is null)
            {
                existing = new ContainerViewModel(name, image) { Role = role };
                Containers.Add(existing);
            }

            existing.Ports = ports;

            // Requests/limits give the usage numbers a scale to be read against.
            if (c.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
            {
                var (cpuRequest, memoryRequest) = ReadResourceList(resources, "requests");
                var (cpuLimit, memoryLimit) = ReadResourceList(resources, "limits");
                existing.CpuRequestNanocores = cpuRequest;
                existing.MemoryRequestBytes = memoryRequest;
                existing.CpuLimitNanocores = cpuLimit;
                existing.MemoryLimitBytes = memoryLimit;
            }

            if (statuses.TryGetValue(name, out var st))
            {
                existing.Ready = st.Ready;
                existing.RestartCount = st.Restarts;
                existing.State = st.State;
            }
        }
    }

    /// <summary>
    /// Identity of the container currently reflected in <see cref="EnvironmentVars"/>,
    /// so a watch tick that changed nothing about the env doesn't rebuild it.
    /// </summary>
    private (string Container, int SpecHash)? _environmentSignature;

    /// <summary>
    /// Rebuilds the Environment tab — but only when the selected container's env
    /// actually changed. This used to run on every watch tick and clear the
    /// collection unconditionally, so a value you had just revealed disappeared a few
    /// seconds later with no explanation, which is about the worst possible behaviour
    /// for a control whose whole job is "show me this once".
    /// </summary>
    private void RefreshEnvironment()
    {
        if (SelectedContainer is not { } container)
        {
            EnvironmentVars.Clear();
            EnvFromSources.Clear();
            _environmentSignature = null;
            return;
        }

        var raw = _row.Resource.Raw;
        if (!raw.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // The container may be an init or ephemeral one — those carry env too, and
        // looking only in spec.containers left their Environment tab permanently blank.
        if (FindContainerSpec(spec, container.Name) is not { } containerSpec)
        {
            return;
        }

        var env = containerSpec.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Array ? e : default;
        var envFrom = containerSpec.TryGetProperty("envFrom", out var ef) && ef.ValueKind == JsonValueKind.Array ? ef : default;

        // The raw text of both blocks is the signature — a pod object that changed its
        // status (which is what a watch tick almost always is) hashes identically, and
        // the rebuild is skipped along with the revealed values it would have thrown away.
        var signature = (container.Name, HashCode.Combine(
            env.ValueKind == JsonValueKind.Array ? env.GetRawText() : "",
            envFrom.ValueKind == JsonValueKind.Array ? envFrom.GetRawText() : ""));
        if (_environmentSignature == signature)
        {
            return;
        }

        _environmentSignature = signature;
        EnvironmentVars.Clear();
        EnvFromSources.Clear();

        foreach (var item in Elements(env))
        {
            EnvironmentVars.Add(ParseEnvVar(item, _row.Resource.Raw));
        }

        foreach (var source in Elements(envFrom))
        {
            var prefix = source.TryGetProperty("prefix", out var p) ? p.GetString() : null;
            var prefixSuffix = string.IsNullOrEmpty(prefix) ? "" : $" (prefix \"{prefix}\")";
            var optional = source.TryGetProperty("optional", out var opt) && opt.ValueKind == JsonValueKind.True
                ? " · optional"
                : "";

            if (source.TryGetProperty("secretRef", out var sr) && sr.TryGetProperty("name", out var srName))
            {
                EnvFromSources.Add(new EnvFromSourceViewModel(
                    "Secret", srName.GetString() ?? "", $"All keys from Secret/{srName.GetString()}{prefixSuffix}{optional}"));
            }
            else if (source.TryGetProperty("configMapRef", out var cr) && cr.TryGetProperty("name", out var crName))
            {
                EnvFromSources.Add(new EnvFromSourceViewModel(
                    "ConfigMap", crName.GetString() ?? "", $"All keys from ConfigMap/{crName.GetString()}{prefixSuffix}{optional}"));
            }
        }

        // Fire-and-forget: the tab is already usable, and each row fills itself in as
        // its ConfigMap comes back. Guarded by the signature above, so a watch tick
        // that changed nothing doesn't re-issue these. Failures land on their own row
        // (RevealError), which is why nothing is observed here.
        _ = ResolveConfigMapValuesAsync();
    }

    /// <summary>Finds one container's spec across all three container arrays.</summary>
    private static JsonElement? FindContainerSpec(JsonElement spec, string name)
    {
        foreach (var arrayName in (string[])["containers", "initContainers", "ephemeralContainers"])
        {
            if (!spec.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var c in array.EnumerateArray())
            {
                if (c.TryGetProperty("name", out var n) && n.GetString() == name)
                {
                    return c;
                }
            }
        }

        return null;
    }

    /// <summary>Enumerating <c>default(JsonElement)</c> throws; a missing array is just empty.</summary>
    private static IEnumerable<JsonElement> Elements(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            yield return item;
        }
    }

    private static EnvVarViewModel ParseEnvVar(JsonElement e, JsonElement pod)
    {
        var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return new EnvVarViewModel(name, v.GetString(), null, null, null, null);
        }

        if (!e.TryGetProperty("valueFrom", out var valueFrom) || valueFrom.ValueKind != JsonValueKind.Object)
        {
            return new EnvVarViewModel(name, "", null, null, null, null);
        }

        if (valueFrom.TryGetProperty("secretKeyRef", out var skr))
        {
            var refName = skr.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
            var key = skr.TryGetProperty("key", out var rk) ? rk.GetString() ?? "" : "";
            return new EnvVarViewModel(
                name, null, $"Secret/{refName} · key={key}{OptionalSuffix(skr)}", "Secret", refName, key)
            {
                IsOptionalReference = IsOptional(skr),
            };
        }

        if (valueFrom.TryGetProperty("configMapKeyRef", out var cmkr))
        {
            var refName = cmkr.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
            var key = cmkr.TryGetProperty("key", out var rk) ? rk.GetString() ?? "" : "";
            return new EnvVarViewModel(
                name, null, $"ConfigMap/{refName} · key={key}{OptionalSuffix(cmkr)}", "ConfigMap", refName, key)
            {
                IsOptionalReference = IsOptional(cmkr),
            };
        }

        // fieldRef and resourceFieldRef resolve against the pod object we already hold,
        // so showing only the path was withholding an answer that was already in hand.
        // ("fieldRef: status.podIP" tells you nothing; the IP tells you everything.)
        if (valueFrom.TryGetProperty("fieldRef", out var fr) && fr.TryGetProperty("fieldPath", out var fp))
        {
            var path = fp.GetString() ?? "";
            var resolved = ResolveFieldPath(pod, path);
            return new EnvVarViewModel(name, resolved, $"fieldRef: {path}", null, null, null)
            {
                // Resolved from the object rather than typed into the spec — worth
                // saying, since it is not a literal even though it reads like one.
                IsDerivedValue = resolved is not null,
            };
        }

        if (valueFrom.TryGetProperty("resourceFieldRef", out var rfr) && rfr.TryGetProperty("resource", out var res))
        {
            var container = rfr.TryGetProperty("containerName", out var cn) ? cn.GetString() : null;
            var suffix = container is null ? "" : $" (container {container})";
            return new EnvVarViewModel(name, null, $"resourceFieldRef: {res.GetString()}{suffix}", null, null, null);
        }

        return new EnvVarViewModel(name, "", null, null, null, null);
    }

    private static bool IsOptional(JsonElement reference) =>
        reference.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True;

    private static string OptionalSuffix(JsonElement reference) => IsOptional(reference) ? " · optional" : "";

    /// <summary>
    /// Walks a Downward-API <c>fieldPath</c> ("metadata.name", "status.podIP",
    /// "metadata.labels['app']") over the pod object. Only the dotted and
    /// bracket-quoted forms Kubernetes actually accepts here; anything else comes back
    /// null and the row keeps showing the path, which is what it did before.
    /// </summary>
    private static string? ResolveFieldPath(JsonElement pod, string path)
    {
        var current = pod;
        var index = 0;
        while (index < path.Length)
        {
            string segment;
            if (path[index] == '[')
            {
                var close = path.IndexOf(']', index);
                if (close < 0)
                {
                    return null;
                }

                segment = path[(index + 1)..close].Trim('\'', '"');
                index = close + 1;
                if (index < path.Length && path[index] == '.')
                {
                    index++;
                }
            }
            else
            {
                var next = path.IndexOfAny(['.', '['], index);
                segment = next < 0 ? path[index..] : path[index..next];
                index = next < 0 ? path.Length : next < path.Length && path[next] == '.' ? next + 1 : next;
            }

            if (segment.Length == 0
                || current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// The eye toggle on a Secret row. Once fetched the value is kept, so hiding and
    /// showing it again costs nothing and doesn't re-hit the API server — the button
    /// is a visibility control, which is what an eye icon promises.
    /// </summary>
    [RelayCommand]
    private async Task ToggleEnvVarAsync(EnvVarViewModel v)
    {
        if (v.RevealedValue is not null)
        {
            v.IsRevealed = !v.IsRevealed;
            return;
        }

        await RevealEnvVarAsync(v);
    }

    /// <summary>
    /// Resolves every <c>configMapKeyRef</c> row in the current list. ConfigMaps are
    /// ordinary configuration, so their values are on screen without being asked for;
    /// the fetches are sequential because the cache dedupes by object name and a
    /// container referencing eight keys of one ConfigMap should issue one GET, not
    /// eight parallel ones that all miss the cache together.
    /// </summary>
    private async Task ResolveConfigMapValuesAsync()
    {
        foreach (var v in EnvironmentVars.Where(v => v.SecretOrConfigMapKind == "ConfigMap").ToList())
        {
            await RevealEnvVarAsync(v);
        }
    }

    [RelayCommand]
    private async Task RevealEnvVarAsync(EnvVarViewModel v)
    {
        if (!v.CanReveal || v.SecretOrConfigMapName is not { } refName || v.Key is not { } key || v.SecretOrConfigMapKind is not { } kind)
        {
            return;
        }

        v.IsRevealing = true;
        v.RevealError = null;
        var cacheKey = $"{kind}/{refName}";
        try
        {
            if (!_secretConfigMapCache.TryGetValue(cacheKey, out var fetchTask))
            {
                var descriptor = kind == "Secret" ? ResourceDescriptor.Secrets : ResourceDescriptor.ConfigMaps;

                // On the demo cluster the referenced object comes out of the shipped
                // dataset instead of a GET. It still goes through the same cache and the
                // same decode below, so the eye toggle, the base64 decode and the
                // per-row "not found" all behave identically to the live path.
                fetchTask = _client is null
                    ? Task.FromResult(DemoData.ReadObject(kind, PodNamespace, refName))
                    : _client.ReadResourceAsync(descriptor, PodNamespace, refName);
                _secretConfigMapCache[cacheKey] = fetchTask;
            }

            var resource = await fetchTask;
            if (resource is null)
            {
                v.RevealError = $"{kind} \"{refName}\" not found.";
                return;
            }

            if (!resource.Raw.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(key, out var rawValue) || rawValue.ValueKind != JsonValueKind.String)
            {
                v.RevealError = $"Key \"{key}\" not found in {kind}/{refName}.";
                return;
            }

            v.RevealedValue = kind == "Secret" ? DecodeBase64(rawValue.GetString()!) : rawValue.GetString();
            v.IsRevealed = true;
        }
        catch (Exception ex)
        {
            // A stale cached failure (e.g. transient network error) shouldn't block a retry;
            // an RBAC 403 will keep failing the same way, which is the correct behavior.
            _secretConfigMapCache.Remove(cacheKey);
            v.RevealError = ex.Message;
        }
        finally
        {
            v.IsRevealing = false;
        }
    }

    private static string DecodeBase64(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return "<invalid base64>";
        }
    }

    /// <summary>Reads spec.containers[].resources.{requests|limits} as (nanocores, bytes).</summary>
    private static (long? Cpu, long? Memory) ReadResourceList(JsonElement resources, string listName)
    {
        if (!resources.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var cpu = list.TryGetProperty("cpu", out var c) ? Quantity.ParseCpuNanocores(c.GetString()) : null;
        var memory = list.TryGetProperty("memory", out var m) ? Quantity.ParseBytes(m.GetString()) : null;
        return (cpu, memory);
    }

    private async Task RefreshEventsAsync()
    {
        if (_client is null)
        {
            LoadDemoEvents();
            return;
        }

        try
        {
            var events = await _client.GetEventsForAsync(_row.Resource);
            Events.Clear();
            foreach (var e in events)
            {
                Events.Add(new EventRowViewModel(e));
            }
        }
        catch (Exception)
        {
            // events are supplementary; a failure here shouldn't disrupt the rest of the tab
        }
    }

    [RelayCommand]
    private void RefreshEvents() => _ = RefreshEventsAsync();

    [RelayCommand]
    private Task OpenEventInvolvedObject(EventRowViewModel evt) =>
        evt.InvolvedObject is { } involved ? _openOwner(involved, evt.InvolvedObjectNamespace ?? PodNamespace) : Task.CompletedTask;

    // ------------------------------------------------------------------ logs
    //
    // Neither of the two log toggles is a Command, deliberately, and this is the one
    // note to read before touching them. `ToggleButton.IsChecked` is registered
    // two-way, and `ToggleButton.OnClick()` calls `Toggle()` *before*
    // `Button.OnClick()` invokes the `Command`. A ToggleButton wired with both a
    // two-way `IsChecked` binding and a toggling command therefore flips the property
    // twice per click and lands exactly where it started — a guaranteed no-op. That
    // is precisely what "Follow" and "Previous" did: Follow read IsFollowingLogs as
    // already true and called StopLogs(), and Previous started a live follow of the
    // current container instead of fetching the crashed instance's logs, which made
    // LoadPreviousLogs unreachable from the UI. The work lives in the generated
    // On<Property>Changed hooks instead; the view binds IsChecked and nothing else.

    /// <summary>
    /// Guards the two On<c>*</c>Changed hooks against this class's own writes.
    /// StartLogs/StopLogs set the flags to describe what they just did, and those
    /// writes must not re-enter the hook that called them.
    /// </summary>
    private bool _applyingLogState;

    /// <summary>
    /// Bumped by every start/stop. A stream's teardown only writes state when its
    /// generation is still current — otherwise a stream cancelled a moment ago
    /// clears the flag belonging to the one that replaced it, and the pane says
    /// "stopped" over a stream that is very much running.
    /// </summary>
    private int _logGeneration;

    /// <summary>What the running stream is reading, so a redundant restart is skipped.</summary>
    private (string Container, bool Previous)? _streaming;

    /// <summary>
    /// Why the pane looks the way it does — the reason a stream ended, or null while
    /// one is healthy. Rendered next to the state line, because "no lines" and "no
    /// lines because the container has not started yet" need different next steps.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasLogPlaceholder))]
    private string? _logStatus;

    /// <summary>True when <see cref="LogStatus"/> is a failure rather than an ordinary end.</summary>
    [ObservableProperty]
    private bool _isLogStatusProblem;

    /// <summary>
    /// The explicit empty state for the log pane (UI rule 9). Five distinguishable
    /// situations used to render as the same blank card: nothing selected, connected
    /// but silent, stopped, the stream ended with a reason, and a filter that matched
    /// none of the buffered lines. The last one is the nastiest — a filter typo looks
    /// exactly like a container that went quiet — so it reports what it filtered out.
    /// </summary>
    public string? LogPlaceholder
    {
        get
        {
            if (LogLines.Count > 0)
            {
                return null;
            }

            if (SelectedContainer is null)
            {
                return "No container selected.";
            }

            if (_allLogLines.Count > 0)
            {
                return LogSearchText.Length == 0
                    ? null
                    : $"No lines match “{LogSearchText}” — {_allLogLines.Count:N0} line{(_allLogLines.Count == 1 ? "" : "s")} buffered.";
            }

            if (LogStatus is { Length: > 0 } status)
            {
                return status;
            }

            return IsShowingPreviousLogs
                ? $"Fetching the previous instance of {SelectedContainer.Name}…"
                : IsFollowingLogs
                    ? $"Connected to {SelectedContainer.Name} — waiting for output. The container hasn't logged anything yet."
                    : "Log streaming is stopped. Press Follow to start it.";
        }
    }

    public bool HasLogPlaceholder => LogPlaceholder is not null;

    partial void OnIsFollowingLogsChanged(bool value)
    {
        if (_applyingLogState)
        {
            return;
        }

        if (value)
        {
            StartLogs();
        }
        else
        {
            StopLogs("Log streaming is stopped. Press Follow to start it.", problem: false);
        }
    }

    partial void OnIsShowingPreviousLogsChanged(bool value)
    {
        if (_applyingLogState)
        {
            return;
        }

        if (value)
        {
            LoadPreviousLogs();
        }
        else
        {
            StartLogs();
        }
    }

    /// <summary>Writes a log flag without re-entering its own change hook.</summary>
    private void SetLogFlags(bool following, bool previous)
    {
        _applyingLogState = true;
        IsFollowingLogs = following;
        IsShowingPreviousLogs = previous;
        _applyingLogState = false;
        RaiseLogPlaceholder();
    }

    private void LoadPreviousLogs()
    {
        if (SelectedContainer is not { } container)
        {
            SetLogFlags(following: false, previous: false);
            return;
        }

        if (_streaming == (container.Name, true))
        {
            return;
        }

        var token = BeginLogStream(container.Name, previous: true);
        SetLogFlags(following: false, previous: true);
        var generation = _logGeneration;

        if (_client is null)
        {
            _ = ReplayDemoLogsAsync(container.Name, previous: true, generation, token);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    PodNamespace, PodName, container.Name, follow: false, tailLines: 1000,
                    previous: true, timestamps: true, cancellationToken: token))
                {
                    Enqueue(line);
                }

                await EndLogStreamAsync(generation, "Previous logs loaded — this is a snapshot, not a live stream.", problem: false);
            }
            catch (OperationCanceledException)
            {
                // normal on stop/close
            }
            catch (Exception ex)
            {
                // The API server explains this one properly ("previous terminated
                // container \"app\" in pod \"x\" not found"), which is why Core stopped
                // using EnsureSuccessStatusCode — the sentence IS the diagnosis.
                await EndLogStreamAsync(generation, FirstLine(ex.Message), problem: true);
            }
        }, token);
    }

    private void StartLogs()
    {
        if (SelectedContainer is not { } container)
        {
            SetLogFlags(following: false, previous: false);
            return;
        }

        if (_streaming == (container.Name, false))
        {
            return;
        }

        var token = BeginLogStream(container.Name, previous: false);
        SetLogFlags(following: true, previous: false);
        var generation = _logGeneration;

        if (_client is null)
        {
            _ = ReplayDemoLogsAsync(container.Name, previous: false, generation, token);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    PodNamespace, PodName, container.Name, follow: true, tailLines: 200,
                    timestamps: true, cancellationToken: token))
                {
                    Enqueue(line);
                }

                // follow=true returning means the container exited; the API server
                // closes the stream rather than erroring.
                await EndLogStreamAsync(generation, $"Stream ended — {container.Name} exited.", problem: false);
            }
            catch (OperationCanceledException)
            {
                // normal on stop/close
            }
            catch (Exception ex)
            {
                await EndLogStreamAsync(generation, FirstLine(ex.Message), problem: true);
            }
        }, token);
    }

    /// <summary>
    /// The demo cluster's stand-in for a log stream: canned lines fed one at a time
    /// through the same <see cref="Enqueue"/> the socket pump uses, so batching,
    /// trimming, filtering, the timestamp toggle, auto-scroll and every placeholder
    /// state are the real ones — only the source of the bytes differs. A stream that
    /// arrived fully-formed would demonstrate none of that.
    ///
    /// It ends with a stated reason rather than going quiet, because a demo pane that
    /// simply stops is indistinguishable from one that broke.
    /// </summary>
    private async Task ReplayDemoLogsAsync(string container, bool previous, int generation, CancellationToken token)
    {
        var lines = previous ? DemoLogs.Previous(PodName, container) : DemoLogs.For(PodName, container);
        if (lines is null)
        {
            // Exactly what the API server says when a container has never restarted —
            // Previous has to be honest about that here too, or the demo teaches the
            // wrong thing about the most important CrashLoopBackOff gesture in the app.
            await EndLogStreamAsync(
                generation,
                $"previous terminated container \"{container}\" in pod \"{PodName}\" not found",
                problem: true);
            return;
        }

        try
        {
            foreach (var line in lines)
            {
                await Task.Delay(DemoLogs.Interval, token);
                Enqueue(line);
            }
        }
        catch (OperationCanceledException)
        {
            return; // container switched, Follow turned off, or the tab closed
        }

        await EndLogStreamAsync(
            generation,
            previous
                ? "Previous logs loaded — this is a snapshot, not a live stream."
                : lines.Count == 0
                    ? $"{container} has not started yet, so it has produced no logs."
                    : "Demo cluster: the sample stream has finished. A real cluster would keep following.",
            problem: false);
    }

    /// <summary>Cancels whatever is running, clears the buffer and opens a new generation.</summary>
    private CancellationToken BeginLogStream(string container, bool previous)
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = new CancellationTokenSource();
        _logGeneration++;
        _streaming = (container, previous);
        LogStatus = null;
        IsLogStatusProblem = false;
        ClearLogBuffer();
        StartLogFlushTimer();
        return _logCts.Token;
    }

    private async Task EndLogStreamAsync(int generation, string status, bool problem) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _logGeneration)
            {
                return;
            }

            FlushLogLines();
            StopLogFlushTimer();
            _streaming = null;
            LogStatus = status;
            IsLogStatusProblem = problem;
            SetLogFlags(following: false, previous: IsShowingPreviousLogs);
        });

    private void StopLogs(string? status = null, bool problem = false)
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = null;
        _logGeneration++;
        _streaming = null;
        StopLogFlushTimer();
        FlushLogLines();
        LogStatus = status;
        IsLogStatusProblem = problem;
        SetLogFlags(following: false, previous: false);
    }

    private void ClearLogBuffer()
    {
        lock (_pendingLogLock)
        {
            _pendingLogLines.Clear();
        }

        _allLogLines.Clear();
        LogLines.Clear();
        RaiseLogPlaceholder();
    }

    // --- throughput -----------------------------------------------------------
    //
    // The pump used to await one dispatcher call per line and then remove the oldest
    // line from the bound collection with an O(n) Remove(item) — a linear scan of
    // 4000 items plus a collection-changed notification, per line. A pod logging a
    // few hundred lines a second locked the UI. Now the pump only takes a lock and
    // appends a string; folding into the view models, filtering and trimming all
    // happen once per tick on the UI thread, in bulk.

    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<string> _pendingLogLines = [];
    private readonly Lock _pendingLogLock = new();
    private DispatcherTimer? _logFlushTimer;

    private void Enqueue(string rawLine)
    {
        lock (_pendingLogLock)
        {
            _pendingLogLines.Add(rawLine);
        }
    }

    private void StartLogFlushTimer()
    {
        _logFlushTimer ??= CreateLogFlushTimer();
        _logFlushTimer.Start();
    }

    private void StopLogFlushTimer() => _logFlushTimer?.Stop();

    private DispatcherTimer CreateLogFlushTimer()
    {
        var timer = new DispatcherTimer { Interval = LogFlushInterval };
        timer.Tick += (_, _) => FlushLogLines();
        return timer;
    }

    private void FlushLogLines()
    {
        string[] raw;
        lock (_pendingLogLock)
        {
            if (_pendingLogLines.Count == 0)
            {
                return;
            }

            raw = [.. _pendingLogLines];
            _pendingLogLines.Clear();
        }

        foreach (var rawLine in raw)
        {
            var line = new LogLineViewModel(rawLine, ShowLogTimestamps);
            _allLogLines.Add(line);
            if (MatchesLogFilter(line))
            {
                LogLines.Add(line);
            }
        }

        TrimLogBuffer();
        RaiseLogPlaceholder();
    }

    /// <summary>
    /// Drops the oldest lines once past the cap, in one RemoveRange rather than a
    /// scan-and-remove per line. The visible collection is trimmed from the front by
    /// index for the same reason — the dropped lines are always the oldest, so their
    /// position is known and there is nothing to search for.
    /// </summary>
    private void TrimLogBuffer()
    {
        var excess = _allLogLines.Count - _maxLogLines;
        if (excess <= 0)
        {
            return;
        }

        var dropped = _allLogLines.GetRange(0, excess);
        _allLogLines.RemoveRange(0, excess);

        var visible = 0;
        foreach (var line in dropped)
        {
            if (MatchesLogFilter(line))
            {
                visible++;
            }
        }

        for (var i = 0; i < visible && LogLines.Count > 0; i++)
        {
            LogLines.RemoveAt(0);
        }
    }

    private void RaiseLogPlaceholder()
    {
        OnPropertyChanged(nameof(LogPlaceholder));
        OnPropertyChanged(nameof(HasLogPlaceholder));
    }

    private bool MatchesLogFilter(LogLineViewModel line) =>
        LogSearchText.Length == 0 || line.Message.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase);

    partial void OnLogSearchTextChanged(string value) => ApplyLogFilter();

    private void ApplyLogFilter()
    {
        LogLines.Clear();
        foreach (var line in _allLogLines)
        {
            if (MatchesLogFilter(line))
            {
                LogLines.Add(line);
            }
        }

        RaiseLogPlaceholder();
    }

    partial void OnShowLogTimestampsChanged(bool value)
    {
        foreach (var line in _allLogLines)
        {
            line.ShowTimestamp = value;
        }
    }

    /// <summary>Parser/HTTP messages can run to several lines; an inline notice gets the first.</summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var window = GetMainWindow();
        if (window?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(VisibleLogText());
    }

    /// <summary>
    /// What Copy and Download write: the raw server lines, timestamps included,
    /// regardless of the display toggle. Both used to write <c>DisplayText</c>, so a
    /// log saved with timestamps switched off had none — and a log pasted into an
    /// incident ticket without timestamps is close to useless. The filter still
    /// applies, because "copy visible logs" is what the buttons say.
    /// </summary>
    private string VisibleLogText() => string.Join(Environment.NewLine, LogLines.Select(l => l.RawLine));

    [RelayCommand]
    private async Task DownloadLogsAsync()
    {
        var window = GetMainWindow();
        if (window?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save pod logs",
            SuggestedFileName = $"{PodName}-{SelectedContainer?.Name ?? "pod"}.log",
            FileTypeChoices = [new FilePickerFileType("Log file") { Patterns = ["*.log"] }],
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(VisibleLogText());
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;

    /// <summary>
    /// Both buttons need a container. Without a CanExecute they were enabled with
    /// nothing selected and silently did nothing when pressed, which reads as the app
    /// being broken rather than as a missing selection (CLAUDE.md UI rule 9's last
    /// clause: a command that cannot run must be disabled, never a silent no-op).
    /// </summary>
    private bool HasSelectedContainer => SelectedContainer is not null;

    /// <summary>
    /// Exec and port-forward both need a real cluster behind them. Gated rather than
    /// left enabled-and-inert, so the demo cluster's container strip says what it can
    /// and cannot do before anything is clicked (UI rule 9's last clause).
    /// </summary>
    private bool CanReachContainer => SelectedContainer is not null && _client is not null;

    [RelayCommand(CanExecute = nameof(CanReachContainer))]
    private void Exec()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        _openTab(new ExecTabViewModel(_client, PodNamespace, PodName, container.Name));
    }

    [RelayCommand(CanExecute = nameof(CanReachContainer))]
    private void PortForward()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        // The pod's own declared ports, not a hardcoded 8080 — see PortForwardTabViewModel.
        _openTab(new PortForwardTabViewModel(_client, PodNamespace, PodName, container.Ports));
    }

    [RelayCommand]
    private Task OpenOwner(OwnerRef owner) => _openOwner(owner, PodNamespace);

    /// <summary>
    /// Opens the Secret/ConfigMap behind an <c>envFrom</c> entry. Routed through the
    /// same OwnerRef resolve-and-open path owner chips and event navigation already
    /// use — it takes a kind and a name, which is exactly what an envFrom source is.
    /// </summary>
    [RelayCommand]
    private Task OpenEnvFromSource(EnvFromSourceViewModel source) =>
        _openOwner(new OwnerRef("v1", source.Kind, source.Name, Uid: null, Controller: false), PodNamespace);

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        StopLogs();
        await _metricsCts.CancelAsync();
        _metricsCts.Dispose();
    }
}

/// <summary>
/// One pod condition with its dot colour. The mapping is here rather than on
/// <see cref="PodCondition"/> because <c>ResourceHealth</c>'s vocabulary is the App
/// layer's and Core may not know it — the same split <c>NodeConditionViewModel</c> uses.
/// </summary>
public sealed class PodConditionViewModel
{
    public PodConditionViewModel(PodCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        Condition = condition;

        // Three outcomes, not two. A condition type this app does not classify, and one
        // whose status is Unknown, both render grey: claiming a readiness gate nobody has
        // heard of is "fine" is a false reassurance to the one person who is reading this
        // pane precisely because something is not.
        Health = condition.IsProblem switch
        {
            true => ResourceHealth.Error,
            false => ResourceHealth.Ok,
            null => ResourceHealth.Idle,
        };
    }

    public PodCondition Condition { get; }

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
