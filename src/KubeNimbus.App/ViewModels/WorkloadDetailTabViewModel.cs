using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>A workload's rollout, selected pods and events in the inspector dock.</summary>
public sealed partial class WorkloadDetailTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly ResourceRowViewModel _row;
    private readonly Action<InspectorTabViewModelBase> _openTab;
    private readonly Func<RowActionKind, Task> _armAction;
    private readonly Func<OwnerRef, string?, Task> _openOwner;
    private readonly Func<string, bool>? _activateTab;
    private readonly OpenNamedLogs? _openLogs;
    private readonly Action<ResourceRowViewModel>? _rowRefreshed;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watch;
    private readonly Task _initialRefresh;

    /// <summary>
    /// The kinds a double-click opens here rather than in the YAML editor: the three
    /// controllers that roll pods, and a batch Job — whose pods are the whole of what there
    /// is to see about a run, and which is where a CronJob's "Open Job" lands (FEAT-8).
    /// </summary>
    public static bool Supports(ResourceDescriptor descriptor) =>
        (descriptor.Group == "apps" && descriptor.Kind is "Deployment" or "StatefulSet" or "DaemonSet")
        || descriptor is { Group: "batch", Kind: "Job" };
    public static string KeyFor(string cluster, ResourceDescriptor descriptor, string? ns, string name) =>
        $"workload:{cluster}/{descriptor.Group}/{descriptor.Kind}/{ns}/{name}";
    public override string Key { get; }
    public ResourceRowViewModel Workload => _row;
    public bool CanScale => WorkloadActions.SupportsScale(_descriptor);
    public bool CanRestart => WorkloadActions.SupportsRestart(_descriptor, _row.Resource);
    public ObservableCollection<ResourceRowViewModel> Pods { get; } = [];
    public ObservableCollection<WorkloadCondition> Conditions { get; } = [];
    public ObservableCollection<EventRowViewModel> Events { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPodCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShellCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsMaximizedCommand))]
    private ResourceRowViewModel? _selectedPod;
    private bool CanOpenPod => SelectedPod is not null;
    private bool CanOpenPodLogs => SelectedPod is not null && _openLogs is not null;

    /// <summary>
    /// Why the last L / logs-icon open did not open anything — the pod went away between
    /// the watch's last frame and the click, the server refused the read — stated above the
    /// pod list instead of a dead click. Cleared by the next open that works and by Refresh.
    /// </summary>
    [ObservableProperty] private string? _logsNotice;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _rollout = "";
    [ObservableProperty] private string _podsStatus = "Loading pods…";
    [ObservableProperty] private string _eventsStatus = "Loading events…";
    [ObservableProperty] private string? _error;
    public bool HasNoConditions => Conditions.Count == 0;

    public WorkloadDetailTabViewModel(ClusterClient? client, ResourceDescriptor descriptor,
        ResourceRowViewModel row, Action<InspectorTabViewModelBase> openTab, Func<RowActionKind, Task> armAction,
        Func<OwnerRef, string?, Task> openOwner, Func<string, bool>? activateTab = null, OpenNamedLogs? openLogs = null,
        Action<ResourceRowViewModel>? rowRefreshed = null)
        : base($"{descriptor.Kind}/{row.Name}" + (row.ClusterName.Length > 0 ? $" · {row.ClusterName}" : ""), client is null)
    {
        _client = client;
        _descriptor = descriptor;
        _row = row;
        _openTab = openTab;
        _armAction = armAction;
        _openOwner = openOwner;
        _activateTab = activateTab;
        _openLogs = openLogs;
        _rowRefreshed = rowRefreshed;
        Key = KeyFor(row.ClusterName, descriptor, row.Namespace, row.Name);
        row.PropertyChanged += RowChanged;
        ReadStatus();
        _watch = WatchPodsAsync(_cts.Token);
        _initialRefresh = RefreshAsync();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceRowViewModel.Resource)) ReadStatus();
    }

    private void ReadStatus()
    {
        Conditions.Clear();
        var raw = _row.Resource.Raw;
        if (raw.TryGetProperty("status", out var status))
        {
            if (status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
                foreach (var c in conditions.EnumerateArray())
                    Conditions.Add(new(Text(c, "type"), Text(c, "status"), Text(c, "reason"), Text(c, "message")));
            if (_descriptor is { Group: "batch", Kind: "Job" })
            {
                Rollout = JobProgress(raw, status);
                OnPropertyChanged(nameof(HasNoConditions));
                OnPropertyChanged(nameof(CanRestart));
                RestartCommand.NotifyCanExecuteChanged();
                return;
            }

            var daemon = _descriptor.Kind == "DaemonSet";
            var desired = daemon ? Number(status, "desiredNumberScheduled")
                : raw.TryGetProperty("spec", out var spec) ? Number(spec, "replicas", 1) : 1;
            var ready = Number(status, daemon ? "numberReady" : "readyReplicas");
            var updated = Number(status, daemon ? "updatedNumberScheduled" : "updatedReplicas");
            var observed = Number(status, "observedGeneration");
            var generation = raw.TryGetProperty("metadata", out var metadata) ? Number(metadata, "generation") : 0;
            var pending = observed < generation ? " · waiting for controller" : "";
            Rollout = $"{ready}/{desired} ready · {updated} updated{pending}";
        }
        else Rollout = "Waiting for workload status…";
        OnPropertyChanged(nameof(HasNoConditions));
        OnPropertyChanged(nameof(CanRestart));
        RestartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A Job's line: kubectl's COMPLETIONS figure (succeeded of <c>spec.completions</c>,
    /// which defaults to one), then how many pods are running and how many have failed —
    /// the failed count against <c>backoffLimit</c> is what a crash-looping run is read by.
    /// </summary>
    internal static string JobProgress(JsonElement raw, JsonElement status)
    {
        var spec = raw.TryGetProperty("spec", out var s) ? s : default;
        var completions = spec.ValueKind == JsonValueKind.Object ? Number(spec, "completions", 1) : 1;
        var backoff = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("backoffLimit", out var b)
            && b.TryGetInt64(out var limit) ? $" (backoff limit {limit})" : "";
        var suspended = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("suspend", out var sus)
            && sus.ValueKind == JsonValueKind.True ? " · suspended" : "";
        return $"{Number(status, "succeeded")}/{completions} succeeded · {Number(status, "active")} running · "
            + $"{Number(status, "failed")} failed{backoff}{suspended}";
    }

    private static string Text(JsonElement el, string key) => el.TryGetProperty(key, out var v) ? v.ToString() : "";
    private static long Number(JsonElement el, string key, long fallback = 0) =>
        el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : fallback;

    private async Task WatchPodsAsync(CancellationToken token)
    {
        var selector = LabelSelector.ForPodsOf(_row.Resource);
        if (selector is null) { PodsStatus = "This workload has no usable pod selector."; return; }
        if (_client is null)
        {
            foreach (var pod in Demo.DemoData.Pods.Where(p => p.Namespace == _row.Namespace && selector.Matches(p.Labels)))
                Pods.Add(new(pod, _row.ClusterName));
            PodsStatus = Pods.Count == 0 ? "No matching pods." : $"{Pods.Count} pods";
            return;
        }
        try
        {
            await foreach (var evt in _client.WatchResourceAsync(ResourceDescriptor.Pods, _row.Namespace,
                connectionLost: ex => Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) PodsStatus = ex.Message; }),
                cancellationToken: token, labelSelector: selector).ConfigureAwait(false))
                await Dispatcher.UIThread.InvokeAsync(() => { if (!token.IsCancellationRequested) ApplyPod(evt); });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (!token.IsCancellationRequested) PodsStatus = ex.Message; });
        }
    }

    internal void ApplyPod(ResourceEvent<DynamicResource> evt)
    {
        if (evt.Type == ResourceEventType.Reset) { Pods.Clear(); SelectedPod = null; PodsStatus = "Loading pods…"; return; }
        if (evt.Resource is { } resource)
        {
            var previous = Pods.FirstOrDefault(p => p.Name == resource.Name);
            if (evt.Type == ResourceEventType.Deleted)
            {
                if (previous is not null) Pods.Remove(previous);
                if (ReferenceEquals(SelectedPod, previous)) SelectedPod = null;
            }
            else if (previous is not null) previous.Update(resource);
            else Pods.Add(new(resource, _row.ClusterName));
        }
        PodsStatus = Pods.Count == 0 ? "No matching pods." : $"{Pods.Count} pods";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var token = _cts.Token;
        Error = null;
        LogsNotice = null;
        EventsStatus = "Loading events…";
        try
        {
            if (_client is not null)
            {
                var current = await _client.ReadResourceAsync(_descriptor, _row.Namespace, _row.Name, token);
                if (current is null) { Error = "This workload no longer exists."; }
                else ApplyRefreshed(current);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Error = ex.Message; }
        try
        {
            var events = _client is null
                ? Demo.DemoData.Events.Where(e => e.InvolvedObject()?.Name == _row.Name && e.InvolvedObjectNamespace() == _row.Namespace).ToArray()
                : await _client.GetEventsForAsync(_row.Resource, token);
            Events.Clear();
            foreach (var evt in events) Events.Add(new(evt));
            EventsStatus = Events.Count == 0 ? "No recent events for this workload." : $"{Events.Count} events";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EventsStatus = ex.Message; }
    }

    /// <summary>
    /// A Refresh's read of the workload, written into the row the list holds. The row is
    /// updated in place, which no <c>CollectionChanged</c> reports, so the owning list is
    /// told — otherwise a Refresh that heals or breaks a workload leaves the unhealthy-only
    /// view (and a sort by Status) wrong until the watch's own Modified arrives (ENG-33).
    /// </summary>
    internal void ApplyRefreshed(DynamicResource current)
    {
        _row.Update(current);
        _rowRefreshed?.Invoke(_row);
    }

    [RelayCommand]
    private void OpenYaml()
    {
        var key = YamlEditorTabViewModel.KeyFor(_row.ClusterName, _descriptor, _row.Namespace, _row.Name);
        if (_activateTab?.Invoke(key) == true) return;
        _openTab(new YamlEditorTabViewModel(_client, _descriptor, _row.Namespace, _row.Name, _row.Resource.ToYaml(), _row.ClusterName));
    }

    [RelayCommand(CanExecute = nameof(CanScale))] private Task ScaleAsync() => _armAction(RowActionKind.Scale);
    [RelayCommand(CanExecute = nameof(CanRestart))] private Task RestartAsync() => _armAction(RowActionKind.Restart);

    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private void OpenPod()
    {
        if (SelectedPod is not { } pod) return;
        if (_activateTab?.Invoke(PodDetailTabViewModel.KeyFor(_row.ClusterName, pod.Namespace, pod.Name)) == true) return;
        _openTab(new PodDetailTabViewModel(_client, pod, _openTab, _openOwner, clusterName: _row.ClusterName));
    }

    /// <summary>
    /// L, the context menu's Logs and the row's logs icon: the pod's logs, through the
    /// cluster tab's one open-logs path (<see cref="OpenNamedLogs"/>), so the inspector tab
    /// reused and the "Open logs maximized" preference are the resource list's own.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsAsync() => OpenPodLogsAsync(SelectedPod, maximized: false);

    /// <summary>Shift+L: the same logs, full-size.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsMaximizedAsync() => OpenPodLogsAsync(SelectedPod, maximized: true);

    /// <summary>
    /// One pod's logs. Selects the row first — the icon takes the press the grid would
    /// have selected it with, and the list should say which pod the logs are of.
    /// </summary>
    public async Task OpenPodLogsAsync(ResourceRowViewModel? pod, bool maximized)
    {
        if (pod is null || _openLogs is null) return;
        SelectedPod = pod;
        LogsNotice = await _openLogs(
            new OwnerRef("v1", "Pod", pod.Name, pod.Resource.Uid, false), pod.Namespace, maximized, _cts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private void Shell()
    {
        if (SelectedPod is not { } pod) return;
        // kubectl exec's default: the pod's default-container annotation, else the first (FEAT-38).
        var container = PodDetails.DefaultContainer(pod.Resource.Raw) ?? "";
        var shell = new ExecTabViewModel(_client, pod.Namespace, pod.Name, container);
        if (_row.ClusterName.Length > 0) shell.Title += $" · {_row.ClusterName}";
        _openTab(shell);
    }

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= RowChanged;
        await _cts.CancelAsync();
        await Task.WhenAll(_watch, _initialRefresh, RefreshCommand.ExecutionTask ?? Task.CompletedTask);
        _cts.Dispose();
    }
}

public sealed record WorkloadCondition(string Type, string Status, string Reason, string Message);
