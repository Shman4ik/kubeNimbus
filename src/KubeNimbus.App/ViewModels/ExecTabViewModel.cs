using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Terminal;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Interactive exec session for one container. A line-oriented terminal (no
/// PTY rendering, ANSI escape codes stripped rather than colorized) — enough
/// to run shell commands and read their output, which covers the MVP's
/// "interactive terminal" bar without pulling in a full terminal-emulator control.
/// </summary>
public sealed partial class ExecTabViewModel : InspectorTabViewModelBase
{
    /// <summary>
    /// How often decoded output is folded into the terminal model and pushed to the
    /// view. The pump used to post one awaited dispatcher call per 4 KB read and
    /// re-materialize the whole 200 000-char buffer each time, which melted the UI
    /// thread on <c>cat</c> of a large file; coalescing at frame rate costs nothing
    /// perceptible and bounds the work per tick.
    /// </summary>
    private static readonly TimeSpan OutputFlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly ClusterClient _client;
    private readonly string _namespace;
    private readonly string _podName;
    private readonly string _container;

    /// <summary>
    /// The terminal model. Touched <b>only</b> on the UI thread — the socket pump
    /// hands raw text over through <see cref="_pending"/> instead, because
    /// <see cref="TerminalOutputBuffer"/> is explicitly not thread-safe.
    /// </summary>
    private readonly TerminalOutputBuffer _terminal = new();

    /// <summary>Raw decoded chunks waiting for the next flush. Guarded by its own lock.</summary>
    private readonly List<string> _pending = [];
    private readonly Lock _pendingLock = new();

    private DispatcherTimer? _flushTimer;
    private ExecSession? _session;
    private CancellationTokenSource? _cts;

    public override string Key { get; }

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _statusMessage;

    public ExecTabViewModel(ClusterClient client, string @namespace, string podName, string container)
        : base($"Exec: {podName}/{container}")
    {
        _client = client;
        _namespace = @namespace;
        _podName = podName;
        _container = container;
        Key = $"exec:{@namespace}/{podName}/{container}:{Guid.NewGuid():N}";
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
    [RelayCommand]
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

        _terminal.Reset();
        OutputText = "";
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
    /// Folds everything the pump has read since the last tick into the terminal model
    /// and republishes the bound text. Runs on the UI thread, which is what makes
    /// <see cref="_terminal"/>'s single-threaded contract hold.
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
            _terminal.Feed(chunk);
        }

        // Drain is the "did anything actually move" signal; the view binds one string,
        // so the delta itself isn't applied incrementally yet — that needs the output
        // control to support appending, which is still outstanding.
        if (_terminal.Drain() is not null)
        {
            OutputText = _terminal.Snapshot();
        }
    }

    private async Task PumpOutputAsync(CancellationToken ct)
    {
        if (_session is null)
        {
            return;
        }

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _session.StdOut.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // Hand the raw text to the UI thread and return to the socket at once:
                // decoding, control-code handling and trimming all happen on the flush
                // tick, so a chatty container can't starve the read loop or the UI.
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                lock (_pendingLock)
                {
                    _pending.Add(text);
                }
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

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrEmpty(InputText))
        {
            return;
        }

        var pending = InputText;
        if (await WriteAsync(pending + "\n"))
        {
            // Only clear once the write actually landed — clearing first lost the
            // typed command whenever the send failed.
            InputText = "";
        }
    }

    /// <summary>
    /// Sends a raw control byte. Without these the pane was a one-way street: a
    /// <c>tail -f</c> or a <c>top</c> could be started and then never stopped, so the
    /// session wedged permanently and the only way out was closing the tab.
    /// </summary>
    /// <remarks>
    /// They go straight to stdin as the control characters a PTY expects — the remote
    /// terminal's line discipline turns ^C into SIGINT, ^D into EOF and ^\ into SIGQUIT.
    /// Tab is sent unpaired so the remote shell's own completion answers.
    /// </remarks>
    [RelayCommand]
    private Task SendControlAsync(string? key) => key switch
    {
        // ^C, ^D, ^Z, ^\ — the four a wedged session is reached with.
        "C" => WriteAsync(""),
        "D" => WriteAsync(""),
        "Z" => WriteAsync(""),
        "\\" => WriteAsync(""),

        // Tab carries whatever has been typed so far, un-terminated, so the remote
        // shell completes against it rather than against an empty line.
        "Tab" => SendTabAsync(),
        _ => Task.CompletedTask,
    };

    private async Task SendTabAsync()
    {
        var prefix = InputText;
        if (await WriteAsync(prefix + "\t"))
        {
            // The shell echoes the completed text back, so the local box would
            // otherwise end up holding it twice.
            InputText = "";
        }
    }

    private async Task<bool> WriteAsync(string text)
    {
        if (_session is null || !IsConnected)
        {
            StatusMessage = "Not connected — the session has ended.";
            return false;
        }

        try
        {
            await _session.StdIn.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _session.StdIn.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Tells the remote PTY how wide it is. Core has had <c>ResizeAsync</c> since exec
    /// shipped and nothing ever called it, so every session ran at the default 80×24 —
    /// which is why anything that draws a full-width line (top, htop, a table) wrapped
    /// at 80 columns regardless of how wide the dock actually was.
    /// </summary>
    public async Task ResizeAsync(int columns, int rows)
    {
        if (_session is null || !IsConnected || columns <= 0 || rows <= 0)
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

    public override async Task OnClosingAsync()
    {
        _flushTimer?.Stop();
        _flushTimer = null;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _session?.Dispose();
    }
}
