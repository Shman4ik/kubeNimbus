using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Interactive exec session for one container, rendered by a real VT emulator
/// (<see cref="TerminalControlModel"/> over XTerm.NET) rather than the
/// ANSI-stripping scrollback this pane used to carry. The transport is unchanged —
/// <see cref="ClusterClient.ExecAsync"/>'s WebSocket and the bash→sh→ash probe
/// below — and everything above it is now bytes in, bytes out: the model owns the
/// screen grid, colour, the alternate buffer, scrollback and selection, so
/// <c>vi</c>, <c>top</c> and <c>mc</c> draw instead of unspooling escape codes.
/// </summary>
public sealed partial class ExecTabViewModel : InspectorTabViewModelBase
{
    /// <summary>
    /// How often decoded output is fed into the terminal and repainted. The pump used
    /// to post one awaited dispatcher call per 4 KB read, which melted the UI thread on
    /// <c>cat</c> of a large file; coalescing at frame rate costs nothing perceptible
    /// and bounds the work per tick. It matters more now, not less: every feed also
    /// rebuilds the emulator's viewport and invalidates the surface.
    /// </summary>
    private static readonly TimeSpan OutputFlushInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Scrollback, in lines. Deliberately not <c>AppSettings.LogBufferLines</c>: that
    /// setting is named, explained and tuned for the pod-log pane, and a terminal's
    /// buffer is a different thing — a full-screen app repaints the same screen
    /// thousands of times and none of it is scrollback anyone wants to keep.
    /// </summary>
    private const int ScrollbackLines = 5000;

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly string _namespace;
    private readonly string _podName;

    /// <summary>The container the pane was opened on — a debug container's target.</summary>
    private readonly string _container;

    /// <summary>
    /// The container the session execs into: <see cref="_container"/>, until a debug
    /// container is started for it, and that container from then on (a reconnect included).
    /// </summary>
    private string _execContainer;

    /// <summary>Null until the pod has been asked; see <see cref="DetectOperatingSystemAsync"/>.</summary>
    private PodOperatingSystem? _operatingSystem;

    /// <summary>
    /// Decoded chunks waiting for the next flush. Guarded by its own lock: the socket
    /// pump fills it off the UI thread and <see cref="FlushOutput"/> drains it on the
    /// UI thread, which is what keeps <see cref="Terminal"/> single-threaded.
    /// </summary>
    private readonly List<string> _pending = [];
    private readonly Lock _pendingLock = new();

    /// <summary>
    /// Stateful UTF-8 decoder. A 4 KB socket read can end in the middle of a multi-byte
    /// character, and a per-read <c>Encoding.UTF8.GetString</c> turns that into U+FFFD
    /// permanently — the same class of bug the old parser's split-escape-sequence
    /// handling existed to avoid. The decoder holds the partial bytes until the rest
    /// arrives, so <see cref="TerminalControlModel.Feed(string)"/> only ever sees whole
    /// characters.
    /// </summary>
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();

    private DispatcherTimer? _flushTimer;
    private ExecSession? _session;
    private CancellationTokenSource? _cts;

    /// <summary>Last geometry reported to the remote PTY, so a layout pass that doesn't change it writes nothing.</summary>
    private (int Columns, int Rows)? _lastReportedSize;

    /// <summary>Tail of the stdin write chain — see <see cref="OnUserInput"/>.</summary>
    private Task _writes = Task.CompletedTask;

    public override string Key { get; }

    /// <summary>
    /// The terminal itself: screen grid, scrollback, selection and input encoding.
    /// The view binds a <c>TerminalControl</c> to it; this view model feeds it the
    /// pod's stdout and forwards its <see cref="TerminalControlModel.UserInput"/>
    /// straight back down the same WebSocket.
    /// </summary>
    /// <remarks>
    /// Touched <b>only</b> on the UI thread. It is an <c>AvaloniaObject</c> and it
    /// repaints on every feed, so the socket pump hands text over through
    /// <see cref="_pending"/> rather than calling it directly.
    /// </remarks>
    public TerminalControlModel Terminal { get; }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// True once anything at all has been drawn. Until then the terminal is a blank
    /// screen, which is indistinguishable from a broken pane — so the view covers it
    /// with the status instead (UI rule 9). After the first byte the scrollback is
    /// worth more than the notice, and the chrome row carries the state on its own.
    /// </summary>
    [ObservableProperty]
    private bool _hasOutput;

    /// <summary>True while the terminal is blank and therefore has nothing to say for itself.</summary>
    public bool IsStatusOverlayVisible => !HasOutput && !IsDemo;

    partial void OnHasOutputChanged(bool value) => OnPropertyChanged(nameof(IsStatusOverlayVisible));

    /// <summary>
    /// The one thing a demo cluster genuinely cannot do. Stated in place rather than
    /// left as a terminal that never connects — an evaluator has no way to tell that
    /// apart from the feature being broken, which is the impression this whole demo
    /// exists to avoid.
    /// </summary>
    public const string DemoNotice =
        "Exec needs a real container to run a shell in, so it is not available in the demo cluster. "
        + "Open a kubeconfig file to exec into a pod on one of your own clusters.";

    /// <summary>False on the demo cluster: there is no session to send anything to.</summary>
    private bool IsLive => _client is not null;

    public ExecTabViewModel(ClusterClient? client, string @namespace, string podName, string container)
        : base($"Exec: {podName}/{container}", isDemo: client is null)
    {
        _client = client;
        _namespace = @namespace;
        _podName = podName;
        _container = container;
        _execContainer = container;
        Key = $"exec:{@namespace}/{podName}/{container}:{Guid.NewGuid():N}";

        Terminal = new TerminalControlModel(new TerminalOptions { Scrollback = ScrollbackLines });
        Terminal.UserInput += OnUserInput;
        Terminal.SizeChanged += OnTerminalSizeChanged;

        if (client is null)
        {
            StatusMessage = "Not available in the demo cluster.";
            return;
        }

        _ = ConnectAsync();
    }

    /// <summary>
    /// The shell the user asked for, or empty to try <see cref="ExecShells.Candidates"/> for
    /// the pod's OS, in order. <c>/bin/sh</c> alone used to be hardcoded, which is right for
    /// Alpine and wrong for a lot of images: many carry bash only, BusyBox images carry ash,
    /// a Windows node's carry <c>powershell</c> or only <c>cmd</c>, and a distroless image
    /// carries none of them. The API server does not report a missing shell on stdout or
    /// stderr — it reports it on the error channel (see
    /// <see cref="ExecSession.ReadTerminalStatusAsync"/>), so without reading that a
    /// distroless container presented as a connected, permanently blank terminal.
    /// </summary>
    [ObservableProperty]
    private string _shellCommand = "";

    /// <summary>What the session actually got, for the header and for a reconnect.</summary>
    [ObservableProperty]
    private string _activeShell = "";

    /// <summary>
    /// True when every shell tried was refused because it is not in the image — not for a
    /// 403 or a container that is not running, which another shell would not fix either.
    /// It is what offers the debug container.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugOfferVisible))]
    [NotifyCanExecuteChangedFor(nameof(StartDebugContainerCommand))]
    private bool _isShellMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugOfferVisible))]
    [NotifyCanExecuteChangedFor(nameof(StartDebugContainerCommand))]
    private bool _isStartingDebugContainer;

    /// <summary>
    /// The image the debug container runs. Not persisted: <see cref="DebugContainers.DefaultImage"/>
    /// is right wherever Docker Hub is reachable, and an air-gapped cluster's mirror is typed
    /// here, once per pane.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDebugContainerCommand))]
    private string _debugImage = DebugContainers.DefaultImage;

    /// <summary>The debug container this pane is attached to, or null while it is on <see cref="_container"/>.</summary>
    [ObservableProperty]
    private string? _debugContainerName;

    /// <summary>
    /// False when Pod Security refused <c>SYS_PTRACE</c> and the debug container was added
    /// without it, which the connected line then says (see <see cref="DebugContainers"/>).
    /// </summary>
    private bool _debugCanTrace = true;

    /// <summary>
    /// The offer under "no shell": an image box and a button. Not on a Windows node, which
    /// has no ephemeral containers to offer, and not while one is already being started.
    /// </summary>
    public bool IsDebugOfferVisible =>
        IsLive && IsShellMissing && !IsStartingDebugContainer && _operatingSystem != PodOperatingSystem.Windows;

    private bool CanStartDebugContainer() => IsDebugOfferVisible && !string.IsNullOrWhiteSpace(DebugImage);

    /// <summary>What an empty shell box will try, for its placeholder.</summary>
    public string ShellPlaceholder => _operatingSystem switch
    {
        PodOperatingSystem.Linux => "auto (bash/sh/ash)",
        PodOperatingSystem.Windows => "auto (powershell/cmd)",
        _ => "auto",
    };

    public string ShellTip =>
        $"Shell to exec. Leave empty to try {string.Join(", ", ExecShells.Candidates(_operatingSystem ?? PodOperatingSystem.Unknown))}.";

    /// <summary>
    /// How long a debug container may take to start. Most of it is the image pull, which on
    /// a cold node and a slow registry is tens of seconds; three minutes is past any pull
    /// that is going to finish.
    /// </summary>
    private static readonly TimeSpan DebugStartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The pod's OS, asked once per pane. A pod that cannot be read (RBAC, a network blip)
    /// is <see cref="PodOperatingSystem.Unknown"/>: the exec can still work, and both
    /// families of shells are then tried.
    /// </summary>
    private async Task DetectOperatingSystemAsync(ClusterClient client, CancellationToken ct)
    {
        try
        {
            _operatingSystem = await client.GetPodOperatingSystemAsync(_namespace, _podName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _operatingSystem = PodOperatingSystem.Unknown;
        }

        OnPropertyChanged(nameof(ShellPlaceholder));
        OnPropertyChanged(nameof(ShellTip));
        OnPropertyChanged(nameof(IsDebugOfferVisible));
    }

    private async Task ConnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsShellMissing = false;
        StatusMessage = "Connecting…";

        var automatic = string.IsNullOrWhiteSpace(ShellCommand);
        try
        {
            if (automatic && _operatingSystem is null)
            {
                await DetectOperatingSystemAsync(_client, token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IReadOnlyList<string> candidates = automatic
            ? ExecShells.Candidates(_operatingSystem ?? PodOperatingSystem.Unknown)
            : [ShellCommand.Trim()];

        var failures = new List<string>();

        foreach (var shell in candidates)
        {
            try
            {
                var session = await _client.ExecAsync(_namespace, _podName, _execContainer, [shell], tty: true, token);

                // The websocket upgrading says nothing about the command: an image with
                // no such shell answers on channel 3 and then closes. Started once and
                // reused — for the probe below, and then for the rest of the session —
                // because two concurrent reads of the same channel would race for the
                // one status document it carries.
                var status = session.ReadTerminalStatusAsync(token);

                var rejection = await ProbeAsync(status, token);
                if (rejection is not null)
                {
                    session.Dispose();
                    failures.Add(rejection);
                    continue;
                }

                _session = session;
                ActiveShell = shell;
                IsConnected = true;
                StatusMessage = DebugContainerName is { } debugger
                    ? $"Connected to debug container {debugger} ({shell}). It shares {_container}'s processes; "
                      + (_debugCanTrace
                          ? $"{_container}'s files are under /proc/1/root"
                          : $"Pod Security refused SYS_PTRACE, so {_container}'s files under /proc/1/root are readable only if both run as one user")
                    : $"Connected to {_execContainer} ({shell})";

                // The control sized the model during layout, before there was a session
                // to tell. Report it now or the shell runs at the engine's 80×24 default
                // however wide the dock is — which is the whole reason `top` used to wrap.
                _lastReportedSize = null;
                ReportSize(Terminal.Terminal.Cols, Terminal.Terminal.Rows);

                StartFlushTimer();
                _ = PumpOutputAsync(token);
                _ = WatchTerminalStatusAsync(status, token);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
        }

        StatusMessage = DescribeFailure(candidates, failures, automatic);
    }

    /// <summary>
    /// The sentence for an exec that found nothing to run. "No shell" is said only when
    /// every attempt said the command does not exist; otherwise the first failure that is
    /// about something else — a 403, a container that is not running — is the one that
    /// matters, and it used to be buried under whichever shell happened to be tried last.
    /// </summary>
    private string DescribeFailure(IReadOnlyList<string> candidates, List<string> failures, bool automatic)
    {
        var tried = string.Join(", ", candidates);
        if (failures.Count > 0 && failures.TrueForAll(ExecShells.IsMissingExecutable))
        {
            if (!automatic)
            {
                return $"{candidates[0]} is not in {_execContainer}'s image. Clear the shell box to try the usual shells.";
            }

            IsShellMissing = true;
            if (_operatingSystem == PodOperatingSystem.Windows)
            {
                return $"No shell in {_execContainer} — tried {tried}, and the image has none of them.";
            }

            return DebugContainerName is { } debugger
                ? $"No shell in debug container {debugger} either — tried {tried}. Start one from an image that has a shell."
                : $"{_execContainer} has no shell — tried {tried}. The image is probably distroless, "
                  + "which leaves nothing to exec into; a debug container can bring a shell.";
        }

        var cause = failures.Find(f => !ExecShells.IsMissingExecutable(f)) ?? failures.LastOrDefault();
        return candidates.Count > 1
            ? $"Could not exec into {_execContainer} (tried {tried}): {cause}"
            : $"Exec failed: {cause}";
    }

    /// <summary>
    /// What the pane concludes from a set of refused execs, through the same
    /// <see cref="DescribeFailure"/> a live connect uses. For the screenshot fixtures,
    /// whose offline client fails at the socket and so never reaches the runtime's own
    /// "no such file or directory" — the one failure the debug offer exists for.
    /// </summary>
    internal void PresentExecFailures(PodOperatingSystem os, IReadOnlyList<string> failures)
    {
        _operatingSystem = os;
        OnPropertyChanged(nameof(ShellPlaceholder));
        OnPropertyChanged(nameof(ShellTip));
        IsShellMissing = false;
        StatusMessage = DescribeFailure(ExecShells.Candidates(os), [.. failures], automatic: true);
        OnPropertyChanged(nameof(IsDebugOfferVisible));
    }

    /// <summary>
    /// <c>kubectl debug -it &lt;pod&gt; --image=&lt;image&gt; --target=&lt;container&gt;</c>, then a
    /// shell in it. The click is the confirmation: the offer is shown only after the exec
    /// found no shell, names the pod, and says the container stays in it — the strip the
    /// resource list arms (UI rule 17) would be a second question about the same thing.
    /// A running debug container from the same image for the same target is opened rather
    /// than adding another, because none of them can be removed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDebugContainer))]
    private async Task StartDebugContainerAsync()
    {
        if (_client is null || _cts is null)
        {
            return;
        }

        var client = _client;
        var token = _cts.Token;
        var image = DebugImage.Trim();
        IsStartingDebugContainer = true;
        try
        {
            StatusMessage = $"Adding a debug container ({image}) to {_podName}…";
            var pod = await client.ReadResourceAsync(ResourceDescriptor.Pods, _namespace, _podName, token);
            var name = pod is null ? null : DebugContainers.FindReusable(pod.Raw, _container, image);
            _debugCanTrace = true;
            if (name is null)
            {
                var added = await client.AddDebugContainerAsync(_namespace, _podName, _container, image, cancellationToken: token);
                name = added.Name;
                _debugCanTrace = added.CanTrace;
                StatusMessage = $"Starting {name} — the node pulls {image} first if it does not have it…";

                var started = name;
                var state = await client.WaitForDebugContainerAsync(
                    _namespace,
                    _podName,
                    name,
                    DebugStartTimeout,
                    s => Dispatcher.UIThread.Post(() => StatusMessage = $"Starting {started} — {s.Describe()}"),
                    token);

                if (state.Phase != DebugContainerPhase.Running)
                {
                    StatusMessage = $"Debug container {name} did not start — {state.Describe()}";
                    return;
                }
            }

            _execContainer = name;
            DebugContainerName = name;
            Title = $"Debug: {_podName}/{name}";

            // A debug image is a Linux one: ephemeral containers do not exist on Windows nodes.
            _operatingSystem = PodOperatingSystem.Linux;
            OnPropertyChanged(nameof(ShellPlaceholder));
            OnPropertyChanged(nameof(ShellTip));
            ShellCommand = "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add a debug container to {_podName}: {ex.Message}";
            return;
        }
        finally
        {
            IsStartingDebugContainer = false;
        }

        await ReconnectAsync();
    }

    /// <summary>
    /// How long to wait for the API server to reject the exec before treating it as
    /// live. It answers within a round trip when it is going to answer at all, and a
    /// working shell says nothing here — so this is dead time only on success, and
    /// short enough not to be felt.
    /// </summary>
    private static readonly TimeSpan ShellProbeTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// The error channel's verdict, or null if it stayed quiet within the window
    /// (i.e. the shell started).
    /// </summary>
    /// <remarks>
    /// The timeout is a <see cref="Task.WhenAny(Task[])"/> race and emphatically NOT a
    /// cancellation token passed into the read. <c>StreamDemuxer</c>'s per-channel
    /// streams do not observe the token, so a cancelled probe never returned and the
    /// pane sat on "Connecting…" forever — which is exactly what this method exists to
    /// prevent, and what it did on its first live run. Losing the race abandons
    /// nothing: <paramref name="status"/> is the same task the session watcher goes on
    /// to await.
    /// </remarks>
    private static async Task<string?> ProbeAsync(Task<string?> status, CancellationToken ct)
    {
        var finished = await Task.WhenAny(status, Task.Delay(ShellProbeTimeout, ct));
        if (finished != status)
        {
            return null;
        }

        try
        {
            return await status;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Awaits the error channel for the rest of the session. It carries exactly one
    /// status document — "command terminated with exit code 137", say — and that
    /// sentence is the difference between "the shell exited" and knowing why.
    /// </summary>
    private async Task WatchTerminalStatusAsync(Task<string?> status, CancellationToken ct)
    {
        try
        {
            var text = await status;
            if (text is null || ct.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Session ended — {text}");
        }
        catch (Exception)
        {
            // The channel closing with nothing on it is the ordinary case.
        }
    }

    /// <summary>Reconnects, honouring whatever is in <see cref="ShellCommand"/> now.</summary>
    [RelayCommand(CanExecute = nameof(IsLive))]
    private async Task ReconnectAsync()
    {
        _flushTimer?.Stop();
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        _session?.Dispose();
        _session = null;
        IsConnected = false;

        // A hard reset of the emulator, not a scroll. The previous session may have
        // died inside `vi` — i.e. with the alternate buffer active, the cursor hidden
        // and a colour still set — and inheriting that makes the new shell's first
        // prompt invisible. The explicit buffer switch is belt to RIS's braces: the
        // alternate buffer is the one piece of state that, left behind, renders the
        // whole pane blank rather than merely odd.
        Terminal.Terminal.SwitchToNormalBuffer();
        Terminal.Feed("\u001bc");
        HasOutput = false;
        lock (_pendingLock)
        {
            _pending.Clear();
        }

        await ConnectAsync();
    }

    private void StartFlushTimer()
    {
        _flushTimer?.Stop();
        _flushTimer = new DispatcherTimer { Interval = OutputFlushInterval };
        _flushTimer.Tick += (_, _) => FlushOutput();
        _flushTimer.Start();
    }

    /// <summary>
    /// Feeds output the way the socket pump does — through the same pending buffer and
    /// the same <see cref="TerminalControlModel.Feed(string)"/> the live session uses.
    /// It exists for the screenshot fixtures, which have no server to read from;
    /// feeding the emulator behind this view model's back would let what a screenshot
    /// shows drift from what a real session renders.
    /// </summary>
    public void Feed(string text)
    {
        Enqueue(text);
        FlushOutput();
    }

    private void Enqueue(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_pendingLock)
        {
            _pending.Add(text);
        }
    }

    /// <summary>
    /// Folds everything the pump has read since the last tick into the emulator. Runs
    /// on the UI thread, which is what makes <see cref="Terminal"/>'s single-threaded
    /// contract hold; the control repaints from the model's own <c>UpdateUI</c> hook.
    /// </summary>
    private void FlushOutput()
    {
        string[] chunks;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            chunks = [.. _pending];
            _pending.Clear();
        }

        foreach (var chunk in chunks)
        {
            Terminal.Feed(chunk);
        }

        HasOutput = true;
    }

    private async Task PumpOutputAsync(CancellationToken ct)
    {
        if (_session is null)
        {
            return;
        }

        var buffer = new byte[4096];

        // One char per byte is the UTF-8 worst case (ASCII); multi-byte sequences and
        // surrogate pairs both decode to fewer. The spare slot is insurance against a
        // fallback character emitted for bytes held over from the previous read.
        var characters = new char[buffer.Length + 1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _session.StdOut.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // Hand the decoded text to the UI thread and return to the socket at
                // once: the emulation, the repaint and the trimming all happen on the
                // flush tick, so a chatty container can't starve the read loop or the UI.
                var decoded = _decoder.GetChars(buffer, 0, read, characters, 0);
                Enqueue(new string(characters, 0, decoded));
            }
        }
        catch (OperationCanceledException)
        {
            // normal on stop/close
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Session ended: {ex.Message}");
        }
        finally
        {
            // Drain whatever the last read produced before announcing the end, and say
            // so explicitly: the pane used to just stop, leaving a grey dot over frozen
            // text with no way to tell a quiet shell from a dead one (UI rule 9).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _flushTimer?.Stop();
                FlushOutput();
                IsConnected = false;
                StatusMessage ??= "Session ended.";
                if (StatusMessage.StartsWith("Connected", StringComparison.Ordinal))
                {
                    StatusMessage = "Session ended — the shell exited.";
                }
            });
        }
    }

    /// <summary>
    /// Everything typed into the terminal, already encoded by the emulator: printable
    /// text as UTF-8, Ctrl+C as <c>0x03</c>, Ctrl+D as <c>0x04</c>, Tab as <c>0x09</c>,
    /// arrows and function keys as the escape sequences the remote <c>TERM=xterm</c>
    /// expects. The pane no longer parses or assembles any of that itself — which is
    /// what makes a full-screen tool's keyboard work at all.
    /// </summary>
    private void OnUserInput(object? sender, TerminalUserInputEventArgs e) =>
        // Chained rather than fired: two keystrokes arriving inside one write's flight
        // would otherwise race for the stream and could reach the shell out of order,
        // which reads as the terminal scrambling what was typed. The event is always
        // raised on the UI thread, so appending to the chain preserves keystroke order
        // by construction. WriteAsync swallows its own failures, so this never faults.
        _writes = _writes.ContinueWith(_ => WriteAsync(e.Data), TaskScheduler.Default).Unwrap();

    /// <summary>
    /// The emulator's own geometry, reported after every layout change. Core has had
    /// <c>ResizeAsync</c> since exec shipped and for a long time nothing called it, so
    /// every session ran at the default 80×24 — which is why anything drawing a
    /// full-width line wrapped regardless of how wide the dock was. The columns and
    /// rows are the terminal's real ones now, not a division by an assumed cell size.
    /// </summary>
    private void OnTerminalSizeChanged(object? sender, TerminalSizeChangedEventArgs e) => ReportSize(e.Cols, e.Rows);

    private void ReportSize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || _lastReportedSize == (columns, rows))
        {
            return;
        }

        _lastReportedSize = (columns, rows);
        _ = ResizeAsync(columns, rows);
    }

    /// <summary>Tells the remote PTY how wide it is. A failure here is cosmetic and must not take out the session.</summary>
    private async Task ResizeAsync(int columns, int rows)
    {
        if (_session is null || !IsConnected)
        {
            return;
        }

        try
        {
            await _session.ResizeAsync(columns, rows);
        }
        catch (Exception)
        {
            // A resize that doesn't land is cosmetic; it must not take out the session.
        }
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> payload)
    {
        if (_session is null || !IsConnected)
        {
            StatusMessage = "Not connected — the session has ended.";
            return;
        }

        try
        {
            await _session.StdIn.WriteAsync(payload);
            await _session.StdIn.FlushAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send failed: {ex.Message}";
        }
    }

    public override async Task OnClosingAsync()
    {
        _flushTimer?.Stop();
        _flushTimer = null;

        Terminal.UserInput -= OnUserInput;
        Terminal.SizeChanged -= OnTerminalSizeChanged;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _session?.Dispose();
    }
}
