using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>Local-port → pod-port forward panel. Local port 0 (default) picks an ephemeral port.</summary>
public sealed partial class PortForwardTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient _client;
    private readonly string _namespace;
    private readonly string _podName;
    private PortForwardSession? _session;

    public override string Key { get; }

    [ObservableProperty]
    private int _podPort;

    [ObservableProperty]
    private int _localPort;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _statusMessage;

    public PortForwardTabViewModel(ClusterClient client, string @namespace, string podName, int podPort)
        : base($"Forward: {podName}")
    {
        _client = client;
        _namespace = @namespace;
        _podName = podName;
        _podPort = podPort;
        Key = $"portforward:{@namespace}/{podName}:{Guid.NewGuid():N}";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var session = _client.StartPortForward(_namespace, _podName, PodPort, LocalPort);
            session.ConnectionFailed += ex => Dispatcher.UIThread.Post(() => StatusMessage = $"Connection error: {ex.Message}");
            await session.StartAsync();
            _session = session;
            LocalPort = session.LocalPort;
            IsRunning = true;
            StatusMessage = $"Forwarding 127.0.0.1:{LocalPort} → {_podName}:{PodPort}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        IsRunning = false;
        StatusMessage = "Stopped.";
    }

    public override async Task OnClosingAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}
