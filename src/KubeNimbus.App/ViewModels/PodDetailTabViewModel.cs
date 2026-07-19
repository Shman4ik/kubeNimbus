using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Pod detail: containers, live status, live log streaming (follow/container
/// picker/cancel) and events. Tracks the same <see cref="ResourceRowViewModel"/>
/// instance the live list uses, so container status stays current without a
/// second watch — only logs and events are fetched independently.
/// </summary>
public sealed partial class PodDetailTabViewModel : InspectorTabViewModelBase
{
    private const int MaxLogLines = 4000;

    private readonly ClusterClient _client;
    private readonly ResourceRowViewModel _row;
    private readonly Action<InspectorTabViewModelBase> _openTab;
    private readonly Func<OwnerRef, string?, Task> _openOwner;
    private CancellationTokenSource? _logCts;

    public override string Key { get; }

    public string PodNamespace { get; }

    public string PodName { get; }

    public ObservableCollection<ContainerViewModel> Containers { get; } = [];

    [ObservableProperty]
    private ContainerViewModel? _selectedContainer;

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    private bool _isFollowingLogs;

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    public IReadOnlyList<OwnerRef> Owners => _row.Resource.OwnerReferences;

    public PodDetailTabViewModel(
        ClusterClient client,
        ResourceRowViewModel row,
        Action<InspectorTabViewModelBase> openTab,
        Func<OwnerRef, string?, Task> openOwner)
        : base($"Pod/{row.Name}")
    {
        _client = client;
        _row = row;
        _openTab = openTab;
        _openOwner = openOwner;
        PodNamespace = row.Namespace;
        PodName = row.Name;
        Key = $"pod:{PodNamespace}/{PodName}";

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();
        _ = RefreshEventsAsync();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceRowViewModel.Resource) or null)
        {
            Dispatcher.UIThread.Post(RefreshFromRow);
        }
    }

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
            if (statuses.TryGetValue(name, out var st))
            {
                existing.Ready = st.Ready;
                existing.RestartCount = st.Restarts;
                existing.State = st.State;
            }
        }

        SelectedContainer ??= Containers.FirstOrDefault();
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
    private void ToggleFollowLogs()
    {
        if (IsFollowingLogs)
        {
            StopLogs();
        }
        else
        {
            StartLogs();
        }
    }

    private void StartLogs()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        StopLogs();
        LogLines.Clear();
        _logCts = new CancellationTokenSource();
        var token = _logCts.Token;
        IsFollowingLogs = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    PodNamespace, PodName, container.Name, follow: true, tailLines: 200, cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LogLines.Add(line);
                        while (LogLines.Count > MaxLogLines)
                        {
                            LogLines.RemoveAt(0);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // normal on stop/close
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => LogLines.Add($"[log stream ended: {ex.Message}]"));
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

    public override Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        StopLogs();
        return Task.CompletedTask;
    }
}
