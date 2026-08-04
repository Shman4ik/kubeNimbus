using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Local-port → pod-port forward panel. Local port 0 (the default) asks the OS for
/// an ephemeral one.
/// </summary>
/// <remarks>
/// Two things about this pane are shaped by what it got wrong before:
/// <list type="bullet">
/// <item>It offers the pod's <b>declared</b> ports. They were read off the spec and
/// then discarded for a hardcoded 8080, so forwarding to a pod serving on 9090
/// meant knowing that and typing it — and a forward to a port nothing listens on
/// is indistinguishable from a working one until a client hangs.</item>
/// <item>A forward whose last connection was refused is <b>not</b> shown as healthy.
/// The listener really is still accepting, so "stopped" would be a lie; the state
/// is "listening, and the last connection failed — here is the kubelet's reason",
/// which is the sentence that actually gets someone unstuck.</item>
/// </list>
/// </remarks>
public sealed partial class PortForwardTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient _client;
    private readonly string _namespace;
    private readonly string _podName;
    private PortForwardSession? _session;

    public override string Key { get; }

    /// <summary>The container's declared ports, for the picker. Empty for a pod that declares none.</summary>
    public ObservableCollection<ContainerPort> AvailablePorts { get; } = [];

    /// <summary>Whether the picker is worth showing at all (UI rule 1: nothing to pick, no control).</summary>
    public bool HasDeclaredPorts => AvailablePorts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalUrl))]
    private int _podPort;

    /// <summary>Picker selection. Writing it drives <see cref="PodPort"/>; typing a port clears it.</summary>
    [ObservableProperty]
    private ContainerPort? _selectedPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalUrl))]
    [NotifyPropertyChangedFor(nameof(LocalPortInput))]
    private int _localPort;

    /// <summary>
    /// The Local port box's value, where <c>null</c> is the wire value 0 ("ask the OS
    /// for an ephemeral port"). A box reading <c>0</c> only says that to someone who
    /// has hovered the tooltip; an empty box under an "auto" watermark says it in
    /// place, which is where the question is asked.
    /// </summary>
    public int? LocalPortInput
    {
        get => LocalPort == 0 ? null : LocalPort;
        set => LocalPort = value ?? 0;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPorts))]
    [NotifyPropertyChangedFor(nameof(Health))]
    [NotifyPropertyChangedFor(nameof(IsHealthy))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInBrowserCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Whether <see cref="StatusMessage"/> is a failure ("Local port 8080 is already
    /// in use…") rather than a report ("Stopped."). Both used to render as the same
    /// dim grey caption, so the one sentence that says why nothing happened looked
    /// exactly like the one that says nothing is happening.
    /// </summary>
    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>
    /// The kubelet's last complaint on this forward, kept separate from
    /// <see cref="StatusMessage"/> so "forwarding 127.0.0.1:8080" and "the last
    /// connection was refused" can both be on screen — which is exactly the pair you
    /// need to see at once.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Health))]
    [NotifyPropertyChangedFor(nameof(IsHealthy))]
    [NotifyPropertyChangedFor(nameof(HasConnectionError))]
    private string? _connectionError;

    /// <summary>Drives the status dot: running and clean is ok, running with a failed connection is warn.</summary>
    public string Health => !IsRunning
        ? ResourceHealth.Idle
        : ConnectionError is null
            ? ResourceHealth.Ok
            : ResourceHealth.Warn;

    /// <summary>Severity for the status bar, as two bools the view can bind classes to.</summary>
    public bool IsHealthy => IsRunning && ConnectionError is null;

    public bool HasConnectionError => ConnectionError is not null;

    /// <summary>Ports are configuration, not controls — a running forward's inputs are read-only.</summary>
    public bool CanEditPorts => !IsRunning;

    public string LocalUrl => $"http://127.0.0.1:{LocalPort}";

    public PortForwardTabViewModel(
        ClusterClient client, string @namespace, string podName, IReadOnlyList<ContainerPort> ports)
        : base($"Forward: {podName}")
    {
        _client = client;
        _namespace = @namespace;
        _podName = podName;

        foreach (var port in ports)
        {
            AvailablePorts.Add(port);
        }

        // The first declared port, or 8080 for a pod that declares none — at which
        // point a guess is all anyone has, including kubectl's user.
        _selectedPort = AvailablePorts.FirstOrDefault();
        _podPort = _selectedPort?.Number ?? 8080;

        Key = $"portforward:{@namespace}/{podName}:{Guid.NewGuid():N}";
        // A pane that says nothing on open is indistinguishable from one that failed
        // to load (UI rule 9); the resting state is a sentence, not a blank.
        StatusMessage = "Not forwarding. Pick a port and press Start.";
        UpdateTitle();
    }

    /// <summary>Convenience for callers that know only a port number (the command palette / row menu).</summary>
    public PortForwardTabViewModel(ClusterClient client, string @namespace, string podName, int podPort)
        : this(client, @namespace, podName, [new ContainerPort(podPort, null)])
    {
    }

    partial void OnSelectedPortChanged(ContainerPort? value)
    {
        if (value is not null)
        {
            PodPort = value.Number;
        }
    }

    partial void OnPodPortChanged(int value)
    {
        // Typed a port that no declared entry matches: drop the picker's selection
        // rather than leaving it pointing at a different number than the one in use.
        if (SelectedPort is { } selected && selected.Number != value)
        {
            SelectedPort = AvailablePorts.FirstOrDefault(p => p.Number == value);
        }

        UpdateTitle();
    }

    /// <summary>
    /// The tab header carries the port. Two forwards on the same pod used to be two
    /// tabs both titled "Forward: shop-web" — identical, and the whole reason to have
    /// two of them is that they go to different places.
    /// </summary>
    private void UpdateTitle() => Title = $"Forward: {_podName}:{PodPort}";

    [RelayCommand(CanExecute = nameof(CanEditPorts))]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        ConnectionError = null;
        StatusIsError = false;
        try
        {
            var session = _client.StartPortForward(_namespace, _podName, PodPort, LocalPort);
            session.ConnectionFailed += ex => Dispatcher.UIThread.Post(() => OnConnectionFailed(ex));
            await session.StartAsync();
            _session = session;
            LocalPort = session.LocalPort;
            IsRunning = true;
            // No "Forwarding 127.0.0.1:x → pod:y" sentence any more: while running, the
            // status bar shows the local URL itself (copyable, openable) and the tab
            // header already carries pod:port, so the sentence restated both.
            StatusMessage = null;
        }
        catch (PortForwardException ex)
        {
            // Already a sentence about the local port ("Local port 8080 is already in
            // use — choose a different local port"). A raw SocketException here named
            // neither the port nor the remedy.
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start: {ex.Message}";
            StatusIsError = true;
        }
    }

    private void OnConnectionFailed(Exception ex) =>
        ConnectionError = ex is PortForwardException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        IsRunning = false;
        ConnectionError = null;
        StatusIsError = false;
        StatusMessage = "Stopped.";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CopyUrlAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(LocalUrl);
        StatusIsError = false;
        StatusMessage = $"Copied {LocalUrl} to the clipboard.";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void OpenInBrowser()
    {
        try
        {
            // UseShellExecute is what hands the URL to the default browser; without it
            // this tries to execute "http://…" as a program and throws.
            Process.Start(new ProcessStartInfo(LocalUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open a browser: {ex.Message}";
            StatusIsError = true;
        }
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
