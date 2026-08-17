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
    private readonly string _container;

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
    /// Shells to try, in order. <c>/bin/sh</c> alone was hardcoded, which is right for
    /// Alpine and wrong for a lot of images: many carry bash only, BusyBox images
    /// carry ash, and a distroless image carries none of them. The API server does not
    /// report a missing shell on stdout or stderr — it reports it on the error channel
    /// (see <see cref="ExecSession.ReadTerminalStatusAsync"/>), so without reading that
    /// a distroless container presented as a connected, permanently blank terminal.
    /// </summary>
    private static readonly string[] ShellCandidates = ["/bin/bash", "/bin/sh", "/bin/ash"];

    /// <summary>The shell the user asked for, or null to try <see cref="ShellCandidates"/> in order.</summary>
    [ObservableProperty]
    private string _shellCommand = "";

    /// <summary>What the session actually got, for the header and for a reconnect.</summary>
    [ObservableProperty]
    private string _activeShell = "";

    private async Task ConnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var candidates = string.IsNullOrWhiteSpace(ShellCommand)
            ? ShellCandidates
            : [ShellCommand.Trim()];

        StatusMessage = "Connecting…";
        Exception? lastFailure = null;

        foreach (var shell in candidates)
        {
            try
            {
                var session = await _client.ExecAsync(_namespace, _podName, _container, [shell], tty: true, token);

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
                    lastFailure = new InvalidOperationException(rejection);
                    continue;
                }

                _session = session;
                ActiveShell = shell;
                IsConnected = true;
                StatusMessage = $"Connected to {_container} ({shell})";

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
                lastFailure = ex;
            }
        }

        StatusMessage = candidates.Length > 1
            ? $"No usable shell in {_container} — tried {string.Join(", ", candidates)}. {lastFailure?.Message}"
            : $"Exec failed: {lastFailure?.Message}";
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
