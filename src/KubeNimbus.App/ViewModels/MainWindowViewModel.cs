using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Phase-1 shell: pick a kubeconfig context, connect, and show a live
/// (watch-driven) pod list. Deliberately minimal — the sidebar tree, YAML
/// editor, exec and port-forward hang off this later.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private ClusterClient? _client;
    private CancellationTokenSource? _watchCts;

    /// <summary>Fast lookup for in-place row updates keyed by namespace/name.</summary>
    private readonly Dictionary<string, PodRowViewModel> _podsByKey = new(StringComparer.Ordinal);

    public ObservableCollection<ClusterContext> Contexts { get; } = [];

    public ObservableCollection<PodRowViewModel> Pods { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private ClusterContext? _selectedContext;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = "Loading kubeconfig…";

    /// <summary>Non-null when the watch reports a connection problem; surfaced in the UI.</summary>
    [ObservableProperty]
    private string? _connectionWarning;

    public MainWindowViewModel()
    {
        _ = LoadContextsAsync();
    }

    private async Task LoadContextsAsync()
    {
        try
        {
            var contexts = await Kubeconfig.LoadContextsAsync();
            Contexts.Clear();
            foreach (var ctx in contexts)
            {
                Contexts.Add(ctx);
            }

            SelectedContext = Contexts.FirstOrDefault();
            Status = Contexts.Count == 0
                ? "No kubeconfig contexts found."
                : $"{Contexts.Count} context(s) — pick one and connect.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to read kubeconfig: {ex.Message}";
        }
    }

    private bool CanConnect => SelectedContext is not null && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedContext is not { } context)
        {
            return;
        }

        await DisconnectAsync();

        IsConnecting = true;
        ConnectionWarning = null;
        Status = $"Connecting to {context.Name}…";

        try
        {
            var client = ClusterClient.Connect(context);
            var version = await client.GetServerVersionAsync();
            _client = client;
            IsConnected = true;
            Status = $"Connected to {context.Name} (Kubernetes {version.GitVersion}).";
            StartPodWatch(context.Namespace);
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void StartPodWatch(string? @namespace)
    {
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        var client = _client!;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchPodsAsync(
                    @namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() => ConnectionWarning = ex.Message),
                    cancellationToken: token))
                {
                    // Apply on the UI thread — the collections are bound to the view.
                    await Dispatcher.UIThread.InvokeAsync(() => Apply(evt));
                }
            }
            catch (OperationCanceledException)
            {
                // normal on disconnect
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Status = $"Watch ended: {ex.Message}");
            }
        }, token);
    }

    private void Apply(ResourceEvent<k8s.Models.V1Pod> evt)
    {
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                Pods.Clear();
                _podsByKey.Clear();
                ConnectionWarning = null;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } pod:
                var key = PodRowViewModel.KeyFor(pod);
                if (_podsByKey.TryGetValue(key, out var existing))
                {
                    existing.Update(pod);
                }
                else
                {
                    var row = new PodRowViewModel(pod);
                    _podsByKey[key] = row;
                    Pods.Add(row);
                }

                break;

            case ResourceEventType.Deleted when evt.Resource is { } pod:
                var delKey = PodRowViewModel.KeyFor(pod);
                if (_podsByKey.Remove(delKey, out var removed))
                {
                    Pods.Remove(removed);
                }

                break;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_watchCts is { } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
            _watchCts = null;
        }

        _client?.Dispose();
        _client = null;
        IsConnected = false;
        Pods.Clear();
        _podsByKey.Clear();
    }
}
