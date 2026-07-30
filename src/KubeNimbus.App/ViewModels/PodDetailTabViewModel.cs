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
    private const int MaxLogLines = 4000;

    /// <summary>Same cadence as the list view — metrics.k8s.io aggregates over ~30s.</summary>
    private static readonly TimeSpan MetricsPollInterval = TimeSpan.FromSeconds(15);

    private readonly ClusterClient _client;
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

    /// <summary>The selected container's <c>envFrom</c> sources — reference-only, no per-key reveal
    /// (the pod spec doesn't declare individual keys for these; open the Secret/ConfigMap's own YAML for that).</summary>
    public ObservableCollection<string> EnvFromSources { get; } = [];

    public IReadOnlyList<OwnerRef> Owners => _row.Resource.OwnerReferences;

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
        ClusterClient client,
        ResourceRowViewModel row,
        Action<InspectorTabViewModelBase> openTab,
        Func<OwnerRef, string?, Task> openOwner,
        string clusterName = "")
        : base(clusterName.Length == 0 ? $"Pod/{row.Name}" : $"Pod/{row.Name} · {clusterName}")
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
        _ = RefreshEventsAsync();
        _ = Task.Run(() => PollMetricsAsync(_metricsCts.Token), _metricsCts.Token);
    }

    /// <summary>
    /// Polls this pod's per-container usage. Silently does nothing on clusters
    /// without metrics-server — the container rows just don't show a usage line.
    /// </summary>
    private async Task PollMetricsAsync(CancellationToken token)
    {
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

    partial void OnSelectedContainerChanged(ContainerViewModel? value) => RefreshEnvironment();

    private void RefreshFromRow()
    {
        var raw = _row.Resource.Raw;
        var statuses = new Dictionary<string, (bool Ready, int Restarts, string State)>(StringComparer.Ordinal);
        if (raw.TryGetProperty("status", out var status) && status.TryGetProperty("containerStatuses", out var cs)
            && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var ready = c.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
                var restarts = c.TryGetProperty("restartCount", out var rc) && rc.TryGetInt32(out var count) ? count : 0;
                var state = "Unknown";
                if (c.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in st.EnumerateObject())
                    {
                        state = prop.Name;
                        break;
                    }
                }

                statuses[name] = (ready, restarts, state);
            }
        }

        if (!raw.TryGetProperty("spec", out var spec) || !spec.TryGetProperty("containers", out var containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in containers.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var image = c.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";
            seen.Add(name);

            var ports = new List<int>();
            if (c.TryGetProperty("ports", out var portsEl) && portsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in portsEl.EnumerateArray())
                {
                    if (p.TryGetProperty("containerPort", out var cp) && cp.TryGetInt32(out var port)
                        && (!p.TryGetProperty("protocol", out var proto) || proto.GetString() is null or "TCP"))
                    {
                        ports.Add(port);
                    }
                }
            }

            var existing = Containers.FirstOrDefault(x => x.Name == name);
            if (existing is null)
            {
                existing = new ContainerViewModel(name, image);
                Containers.Add(existing);
            }

            existing.TcpPorts = ports;

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

        SelectedContainer ??= Containers.FirstOrDefault();
        RefreshEnvironment();
    }

    private void RefreshEnvironment()
    {
        EnvironmentVars.Clear();
        EnvFromSources.Clear();

        if (SelectedContainer is not { } container)
        {
            return;
        }

        var raw = _row.Resource.Raw;
        if (!raw.TryGetProperty("spec", out var spec) || !spec.TryGetProperty("containers", out var containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        JsonElement? containerSpec = null;
        foreach (var c in containers.EnumerateArray())
        {
            if (c.TryGetProperty("name", out var n) && n.GetString() == container.Name)
            {
                containerSpec = c;
                break;
            }
        }

        if (containerSpec is not { } spec2)
        {
            return;
        }

        if (spec2.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in env.EnumerateArray())
            {
                EnvironmentVars.Add(ParseEnvVar(e));
            }
        }

        if (spec2.TryGetProperty("envFrom", out var envFrom) && envFrom.ValueKind == JsonValueKind.Array)
        {
            foreach (var ef in envFrom.EnumerateArray())
            {
                var prefix = ef.TryGetProperty("prefix", out var p) ? p.GetString() : null;
                var prefixSuffix = string.IsNullOrEmpty(prefix) ? "" : $" (prefix \"{prefix}\")";

                if (ef.TryGetProperty("secretRef", out var sr) && sr.TryGetProperty("name", out var srName))
                {
                    EnvFromSources.Add($"All keys from Secret/{srName.GetString()}{prefixSuffix}");
                }
                else if (ef.TryGetProperty("configMapRef", out var cr) && cr.TryGetProperty("name", out var crName))
                {
                    EnvFromSources.Add($"All keys from ConfigMap/{crName.GetString()}{prefixSuffix}");
                }
            }
        }
    }

    private static EnvVarViewModel ParseEnvVar(JsonElement e)
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
            return new EnvVarViewModel(name, null, $"Secret/{refName} · key={key}", "Secret", refName, key);
        }

        if (valueFrom.TryGetProperty("configMapKeyRef", out var cmkr))
        {
            var refName = cmkr.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
            var key = cmkr.TryGetProperty("key", out var rk) ? rk.GetString() ?? "" : "";
            return new EnvVarViewModel(name, null, $"ConfigMap/{refName} · key={key}", "ConfigMap", refName, key);
        }

        if (valueFrom.TryGetProperty("fieldRef", out var fr) && fr.TryGetProperty("fieldPath", out var fp))
        {
            return new EnvVarViewModel(name, null, $"fieldRef: {fp.GetString()}", null, null, null);
        }

        if (valueFrom.TryGetProperty("resourceFieldRef", out var rfr) && rfr.TryGetProperty("resource", out var res))
        {
            return new EnvVarViewModel(name, null, $"resourceFieldRef: {res.GetString()}", null, null, null);
        }

        return new EnvVarViewModel(name, "", null, null, null, null);
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
                fetchTask = _client.ReadResourceAsync(descriptor, PodNamespace, refName);
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

    [RelayCommand]
    private void ToggleFollowLogs()
    {
        if (IsFollowingLogs)
        {
            StopLogs();
        }
        else
        {
            IsShowingPreviousLogs = false;
            StartLogs();
        }
    }

    [RelayCommand]
    private void TogglePreviousLogs()
    {
        if (IsShowingPreviousLogs)
        {
            IsShowingPreviousLogs = false;
            StartLogs();
            return;
        }

        StopLogs();
        ClearLogBuffer();
        IsShowingPreviousLogs = true;
        LoadPreviousLogs();
    }

    private void LoadPreviousLogs()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = new CancellationTokenSource();
        var token = _logCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    PodNamespace, PodName, container.Name, follow: false, tailLines: 1000,
                    previous: true, timestamps: true, cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => AppendLogLine(line));
                }
            }
            catch (OperationCanceledException)
            {
                // normal on stop/close
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => AppendLogLine($"[no previous container logs available: {ex.Message}]"));
            }
        }, token);
    }

    private void StartLogs()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        StopLogs();
        ClearLogBuffer();
        _logCts = new CancellationTokenSource();
        var token = _logCts.Token;
        IsFollowingLogs = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    PodNamespace, PodName, container.Name, follow: true, tailLines: 200,
                    timestamps: true, cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => AppendLogLine(line));
                }
            }
            catch (OperationCanceledException)
            {
                // normal on stop/close
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => AppendLogLine($"[log stream ended: {ex.Message}]"));
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsFollowingLogs = false);
            }
        }, token);
    }

    private void StopLogs()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = null;
        IsFollowingLogs = false;
    }

    private void ClearLogBuffer()
    {
        _allLogLines.Clear();
        LogLines.Clear();
    }

    private void AppendLogLine(string rawLine)
    {
        var line = new LogLineViewModel(rawLine, ShowLogTimestamps);
        _allLogLines.Add(line);
        while (_allLogLines.Count > MaxLogLines)
        {
            var removed = _allLogLines[0];
            _allLogLines.RemoveAt(0);
            LogLines.Remove(removed);
        }

        if (MatchesLogFilter(line))
        {
            LogLines.Add(line);
        }
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
    }

    partial void OnShowLogTimestampsChanged(bool value)
    {
        foreach (var line in _allLogLines)
        {
            line.ShowTimestamp = value;
        }
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var window = GetMainWindow();
        if (window?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(string.Join(Environment.NewLine, LogLines.Select(l => l.DisplayText)));
    }

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
        await writer.WriteAsync(string.Join(Environment.NewLine, LogLines.Select(l => l.DisplayText)));
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;

    [RelayCommand]
    private void Exec()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        _openTab(new ExecTabViewModel(_client, PodNamespace, PodName, container.Name));
    }

    [RelayCommand]
    private void PortForward()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        _openTab(new PortForwardTabViewModel(_client, PodNamespace, PodName, container.TcpPorts.FirstOrDefault(8080)));
    }

    [RelayCommand]
    private Task OpenOwner(OwnerRef owner) => _openOwner(owner, PodNamespace);

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        StopLogs();
        await _metricsCts.CancelAsync();
        _metricsCts.Dispose();
    }
}
