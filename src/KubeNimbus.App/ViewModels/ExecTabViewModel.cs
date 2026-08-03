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

    private async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            StatusMessage = "Connecting…";
            _session = await _client.ExecAsync(_namespace, _podName, _container, ["/bin/sh"], tty: true, _cts.Token);
            IsConnected = true;
            StatusMessage = $"Connected to {_container} (/bin/sh)";
            StartFlushTimer();
            _ = PumpOutputAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Exec failed: {ex.Message}";
        }
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
        if (_session is null || string.IsNullOrEmpty(InputText))
        {
            return;
        }

        if (!IsConnected)
        {
            StatusMessage = "Not connected — the session has ended.";
            return;
        }

        var pending = InputText;
        var bytes = Encoding.UTF8.GetBytes(pending + "\n");
        try
        {
            await _session.StdIn.WriteAsync(bytes);
            await _session.StdIn.FlushAsync();
            // Only clear once the write actually landed — clearing first lost the
            // typed command whenever the send failed.
            InputText = "";
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

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _session?.Dispose();
    }
}
