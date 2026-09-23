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
    private readonly Func<OwnerRef, string?, Task>? _openLogs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watch;
    private readonly Task _initialRefresh;

    public static bool Supports(ResourceDescriptor descriptor) => descriptor.Group == "apps"
        && descriptor.Kind is "Deployment" or "StatefulSet" or "DaemonSet";
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
    [NotifyCanExecuteChangedFor(nameof(OpenPodLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShellCommand))]
    private ResourceRowViewModel? _selectedPod;
    private bool CanOpenPod => SelectedPod is not null;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _rollout = "";
    [ObservableProperty] private string _podsStatus = "Loading pods…";
    [ObservableProperty] private string _eventsStatus = "Loading events…";
    [ObservableProperty] private string? _error;
    public bool HasNoConditions => Conditions.Count == 0;

    public WorkloadDetailTabViewModel(ClusterClient? client, ResourceDescriptor descriptor,
        ResourceRowViewModel row, Action<InspectorTabViewModelBase> openTab, Func<RowActionKind, Task> armAction,
        Func<OwnerRef, string?, Task> openOwner, Func<string, bool>? activateTab = null,
        Func<OwnerRef, string?, Task>? openLogs = null)
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
        EventsStatus = "Loading events…";
        try
        {
            if (_client is not null)
            {
                var current = await _client.ReadResourceAsync(_descriptor, _row.Namespace, _row.Name, token);
                if (current is null) { Error = "This workload no longer exists."; }
                else _row.Update(current);
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

    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private Task OpenPodLogsAsync() => OpenPodLogsAsync(SelectedPod);

    public Task OpenPodLogsAsync(ResourceRowViewModel? pod) =>
        pod is null || _openLogs is null ? Task.CompletedTask
        : _openLogs(new OwnerRef("v1", "Pod", pod.Name, pod.Resource.Uid, Controller: false), pod.Namespace);

    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private void Shell()
    {
        if (SelectedPod is not { } pod) return;
        var container = pod.Resource.Raw.TryGetProperty("spec", out var spec)
            && spec.TryGetProperty("containers", out var containers) && containers.GetArrayLength() > 0
            ? Text(containers[0], "name") : "";
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
