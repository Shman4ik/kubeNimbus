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
    private const int MaxOutputChars = 200_000;

    private readonly ClusterClient _client;
    private readonly string _namespace;
    private readonly string _podName;
    private readonly string _container;
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
            _session = await _client.ExecAsync(_namespace, _podName, _container, ["/bin/sh"], tty: true, _cts.Token);
            IsConnected = true;
            _ = PumpOutputAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Exec failed: {ex.Message}";
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

                var text = AnsiText.StripEscapeCodes(Encoding.UTF8.GetString(buffer, 0, read));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    OutputText += text;
                    if (OutputText.Length > MaxOutputChars)
                    {
                        OutputText = OutputText[^MaxOutputChars..];
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
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Session ended: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsConnected = false);
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_session is null || string.IsNullOrEmpty(InputText))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(InputText + "\n");
        InputText = "";
        try
        {
            await _session.StdIn.WriteAsync(bytes);
            await _session.StdIn.FlushAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send failed: {ex.Message}";
        }
    }

    public override async Task OnClosingAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _session?.Dispose();
    }
}
