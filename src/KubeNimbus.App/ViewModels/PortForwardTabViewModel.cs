using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Local-port → pod-port forward panel, to one pod or through a Service. Local port 0 (the
/// default) asks the OS for an ephemeral one.
/// </summary>
/// <remarks>
/// Four things about this pane are shaped by what it got wrong before:
/// <list type="bullet">
/// <item>It offers the pod's <b>declared</b> ports. They were read off the spec and
/// then discarded for a hardcoded 8080, so forwarding to a pod serving on 9090
/// meant knowing that and typing it — and a forward to a port nothing listens on
/// is indistinguishable from a working one until a client hangs.</item>
/// <item>A forward whose last connection was refused is <b>not</b> shown as healthy.
/// The listener really is still accepting, so "stopped" would be a lie; the state
/// is "listening, and the last connection failed — here is the kubelet's reason",
/// which is the sentence that actually gets someone unstuck.</item>
/// <item>A forward <b>outlives its tab</b> (FEAT-7). It used to stop when the tab closed,
/// and nothing listed what was still listening. A running forward is in the window's
/// <see cref="PortForwardRegistry"/>, closing its tab leaves it running (the pane says so
/// before it is closed, and the status bar counts it after), and the registry is where it
/// is reopened or stopped. Closing the <em>cluster</em> tab stops it.</item>
/// <item>A Service forward <b>names the pod it reaches</b> (FEAT-29). The API can only
/// forward to a pod, so one is picked from the service's ready endpoints; the pane says
/// which, and says so again when a failed connection made it pick another.</item>
/// </list>
/// </remarks>
public sealed partial class PortForwardTabViewModel : InspectorTabViewModelBase
{
    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly string _namespace;

    /// <summary>The pod, or for a Service forward the service, this pane forwards to.</summary>
    private readonly string _name;

    private PortForwardSession? _session;

    /// <summary>
    /// Cancelled when the cluster tab closes while a Start is still in flight, and passed to
    /// the session for its lifetime (hard rule 2). Null while nothing is starting or running.
    /// </summary>
    private CancellationTokenSource? _lifetime;

    /// <summary>What the pane says when a Start was cancelled from outside.</summary>
    private string? _cancelledStatus;

    public override string Key { get; }

    /// <summary>The declared ports (the pod's container ports, or the service's TCP ports), for the picker.</summary>
    public ObservableCollection<ContainerPort> AvailablePorts { get; } = [];

    /// <summary>Whether the picker is worth showing at all (UI rule 1: nothing to pick, no control).</summary>
    public bool HasDeclaredPorts => AvailablePorts.Count > 0;

    /// <summary>True for a forward through a Service rather than to one pod.</summary>
    public bool IsService { get; }

    /// <summary>The second field's label: the port is the pod's, or the service's.</summary>
    public string RemotePortLabel => IsService ? "Service port" : "Pod port";

    public string PortsTooltip => IsService ? "Ports this service declares" : "Ports this pod declares";

    /// <summary>The cluster the forward runs on, as the cluster switcher names it — for the forwards list.</summary>
    public string ClusterLabel { get; internal set; } = "";

    /// <summary>The cluster tab that opened this pane, which is where it is reopened. Null in tests.</summary>
    internal ClusterTabViewModel? Owner { get; set; }

    /// <summary>The window's list of running forwards; null where there is none (the screenshot fixtures).</summary>
    internal PortForwardRegistry? Registry { get; set; }

    internal ClusterClient? Client => _client;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalUrl))]
    [NotifyPropertyChangedFor(nameof(TargetDescription))]
    private int _podPort;

    /// <summary>Picker selection. Writing it drives <see cref="PodPort"/>; typing a port clears it.</summary>
    [ObservableProperty]
    private ContainerPort? _selectedPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalUrl))]
    [NotifyPropertyChangedFor(nameof(LocalAddress))]
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
    [NotifyPropertyChangedFor(nameof(KeepsRunningHint))]
    [NotifyPropertyChangedFor(nameof(HasKeepsRunningHint))]
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

    /// <summary>
    /// For a Service forward, the pod connections go to now ("pod shop-api-7f9c-x2k4:8080"),
    /// and null otherwise. The API forwards to a pod, never to a Service, so a Service
    /// forward that did not say which pod it picked would be hiding the one fact that
    /// explains a request answered by the wrong replica.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolvedPod))]
    [NotifyPropertyChangedFor(nameof(TargetDescription))]
    private string? _resolvedPod;

    public bool HasResolvedPod => !string.IsNullOrEmpty(ResolvedPod);

    /// <summary>Said when a Service forward moved to another pod — the reason the requests now land elsewhere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetNotice))]
    private string? _targetNotice;

    public bool HasTargetNotice => !string.IsNullOrEmpty(TargetNotice);

    /// <summary>Drives the status dot: running and clean is ok, running with a failed connection is warn.</summary>
    public string Health => !IsRunning
        ? ResourceHealth.Idle
        : ConnectionError is null
            ? ResourceHealth.Ok
            : ResourceHealth.Warn;

    /// <summary>Severity for the status bar, as two bools the view can bind classes to.</summary>
    public bool IsHealthy => IsRunning && ConnectionError is null;

    public bool HasConnectionError => ConnectionError is not null;

    /// <summary>
    /// Ports are configuration, not controls — a running forward's inputs are
    /// read-only, and so are a demo one's, where Start has nothing to bind to. This
    /// also gates <see cref="StartCommand"/>, so the demo pane offers a locked form
    /// and a disabled button rather than a button that silently does nothing.
    /// </summary>
    public bool CanEditPorts => !IsRunning && !IsDemo;

    /// <summary>
    /// What closing the tab does to a running forward, said before the tab is closed:
    /// it keeps listening (that is the point of the registry), and keeping a local port
    /// open is the one outcome nobody should find out about afterwards.
    /// </summary>
    public string? KeepsRunningHint => IsRunning && Registry is not null
        ? "Closing this tab keeps it forwarding — the status bar counts it, and Stop ends it."
        : null;

    public bool HasKeepsRunningHint => KeepsRunningHint is not null;

    /// <summary>
    /// Why the demo pane is inert. A forward means binding a local TCP port and
    /// tunnelling it to a kubelet; there is no kubelet here, and pretending otherwise
    /// would be the one demo behaviour that could actually mislead someone.
    /// </summary>
    public const string DemoNotice =
        "Port-forwarding opens a real tunnel to a real kubelet, so it is not available in the demo cluster. "
        + "Open a kubeconfig file to forward a port from a pod or a service on one of your own clusters.";

    public string LocalUrl => $"http://127.0.0.1:{LocalPort}";

    /// <summary>The listening address, for the forwards list.</summary>
    public string LocalAddress => $"127.0.0.1:{LocalPort}";

    /// <summary>
    /// What the forward reaches, for the forwards list: "payments/shop-web:8080", or for a
    /// Service "svc payments/checkout:80 → checkout-6d8f-x2k4:8080".
    /// </summary>
    public string TargetDescription => IsService
        ? $"svc {_namespace}/{_name}:{PodPort}" + (HasResolvedPod ? $" → {ResolvedPod}" : "")
        : $"{_namespace}/{_name}:{PodPort}";

    public PortForwardTabViewModel(
        ClusterClient? client, string @namespace, string podName, IReadOnlyList<ContainerPort> ports)
        : this(client, @namespace, podName, ports, isService: false)
    {
    }

    /// <summary>Convenience for callers that know only a port number (the command palette / row menu).</summary>
    public PortForwardTabViewModel(ClusterClient? client, string @namespace, string podName, int podPort)
        : this(client, @namespace, podName, [new ContainerPort(podPort, null)], isService: false)
    {
    }

    private PortForwardTabViewModel(
        ClusterClient? client, string @namespace, string name, IReadOnlyList<ContainerPort> ports, bool isService)
        : base($"Forward: {name}", isDemo: client is null)
    {
        _client = client;
        _namespace = @namespace;
        _name = name;
        IsService = isService;

        foreach (var port in ports)
        {
            AvailablePorts.Add(port);
        }

        // The first declared port, or 8080 for a pod that declares none — at which
        // point a guess is all anyone has, including kubectl's user.
        _selectedPort = AvailablePorts.FirstOrDefault();
        _podPort = _selectedPort?.Number ?? 8080;

        Key = $"portforward:{(isService ? "svc/" : "")}{@namespace}/{name}:{Guid.NewGuid():N}";
        // A pane that says nothing on open is indistinguishable from one that failed
        // to load (UI rule 9); the resting state is a sentence, not a blank.
        StatusMessage = client is null
            ? "Not available in the demo cluster."
            : isService
                ? "Not forwarding. Pick a port and press Start — a ready pod behind the service is picked then, and named."
                : "Not forwarding. Pick a port and press Start.";
        UpdateTitle();
    }

    /// <summary>
    /// A forward through a Service: its TCP ports are the picker, and the pod is resolved on
    /// Start from the service's EndpointSlices (<see cref="ServiceForwards"/>).
    /// </summary>
    public static PortForwardTabViewModel ForService(
        ClusterClient? client, string @namespace, string serviceName, IReadOnlyList<ServicePortInfo> ports) =>
        new(client, @namespace, serviceName,
            [.. ports.Where(p => string.Equals(p.Protocol, "TCP", StringComparison.Ordinal))
                .Select(p => new ContainerPort(p.Port, p.Name.Length > 0 ? p.Name : null))],
            isService: true);

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
    private void UpdateTitle() => Title = IsService ? $"Forward: svc/{_name}:{PodPort}" : $"Forward: {_name}:{PodPort}";

    [RelayCommand(CanExecute = nameof(CanEditPorts))]
    private async Task StartAsync()
    {
        if (IsRunning || _client is null)
        {
            return;
        }

        ConnectionError = null;
        TargetNotice = null;
        StatusIsError = false;
        if (IsService)
        {
            StatusMessage = "Finding a ready pod behind the service…";
        }

        PortForwardSession? session = null;
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        _cancelledStatus = null;
        Registry?.BeginStart(this);
        try
        {
            session = IsService
                ? _client.StartServicePortForward(_namespace, _name, PodPort, LocalPort)
                : _client.StartPortForward(_namespace, _name, PodPort, LocalPort);
            session.ConnectionFailed += ex => Dispatcher.UIThread.Post(() => OnConnectionFailed(ex));
            session.TargetChanged += (from, to) => Dispatcher.UIThread.Post(() => OnTargetChanged(from, to));
            await session.StartAsync(lifetime.Token);

            // The cluster tab may have closed after the listener bound: never register a
            // session whose client is gone.
            lifetime.Token.ThrowIfCancellationRequested();
            _session = session;
            LocalPort = session.LocalPort;
            ResolvedPod = session.ServiceName is not null && session.Target is { } target
                ? $"{target.PodName}:{target.PodPort}"
                : null;
            IsRunning = true;
            Registry?.Add(this);
            // No "Forwarding 127.0.0.1:x → pod:y" sentence any more: while running, the
            // status bar shows the local URL itself (copyable, openable) and the tab
            // header already carries pod:port, so the sentence restated both.
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(session);
            DropLifetime();
            StatusIsError = false;
            StatusMessage = _cancelledStatus ?? "Stopped.";
        }
        catch (PortForwardException ex)
        {
            DropLifetime();
            // Already a sentence: about the local port ("Local port 8080 is already in
            // use — choose a different local port"), or about the service ("None of the 2
            // endpoints of shop-api is ready…"). A raw SocketException here named neither
            // the port nor the remedy.
            await DisposeQuietlyAsync(session);
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (Exception ex)
        {
            DropLifetime();
            await DisposeQuietlyAsync(session);
            StatusMessage = $"Failed to start: {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            Registry?.EndStart(this);
        }
    }

    /// <summary>Cancels a Start in flight; the pane then says <paramref name="status"/>.</summary>
    internal void CancelStart(string status)
    {
        _cancelledStatus = status;
        _lifetime?.Cancel();
    }

    private void DropLifetime()
    {
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private static async Task DisposeQuietlyAsync(PortForwardSession? session)
    {
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    private void OnConnectionFailed(Exception ex) =>
        ConnectionError = ex is PortForwardException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>A Service forward picked another pod: say which, and why, where the old pod was named.</summary>
    internal void OnTargetChanged(PortForwardTarget from, PortForwardTarget to)
    {
        if (!IsRunning)
        {
            return;
        }

        ResolvedPod = $"{to.PodName}:{to.PodPort}";
        TargetNotice = string.Equals(from.PodName, to.PodName, StringComparison.Ordinal)
            ? $"The service now maps port {PodPort} to {to.PodPort} on {to.PodName}."
            : $"Moved to pod {to.PodName}: a connection to {from.PodName} failed, and it is no longer a ready endpoint of the service.";

        // The failure that caused the move is answered by the move.
        ConnectionError = null;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task StopAsync() => StopForwardingAsync("Stopped.");

    /// <summary>
    /// Ends the forward and leaves <paramref name="status"/> as the pane's sentence. The
    /// forwards list and a closing cluster tab stop it through here, so the pane says why
    /// if it is reopened.
    /// </summary>
    internal async Task StopForwardingAsync(string status)
    {
        if (_session is not null)
        {
            var session = _session;
            _session = null;
            await session.DisposeAsync();
        }

        DropLifetime();
        Registry?.Remove(this);
        IsRunning = false;
        ConnectionError = null;
        TargetNotice = null;
        ResolvedPod = null;
        StatusIsError = false;
        StatusMessage = status;
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

    /// <summary>
    /// Closing the tab of a running forward leaves it running when the window keeps a
    /// registry — it is still listed there, and the status bar counts it. Without one (a
    /// screenshot fixture) nothing would list it, so it stops, as it always used to.
    /// </summary>
    public override async Task OnClosingAsync()
    {
        if (IsRunning && Registry is not null)
        {
            return;
        }

        if (_session is not null)
        {
            await StopForwardingAsync("Stopped.");
        }
    }
}
