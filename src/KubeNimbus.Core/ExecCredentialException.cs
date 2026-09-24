using System.Collections.Concurrent;
using System.Diagnostics;
using k8s;
using k8s.Exceptions;

namespace KubeNimbus.Core;

/// <summary>
/// The kubeconfig's exec credential plugin (<c>aws eks get-token</c>,
/// <c>kubelogin</c>, <c>gke-gcloud-auth-plugin</c>, …) did not produce a credential.
/// </summary>
/// <remarks>
/// <para>
/// The library reports every such failure the same way, and badly. It ignores the
/// plugin's exit code, tries to deserialize whatever reached stdout — usually nothing,
/// because a failing plugin writes its reason to stderr — and throws
/// <c>external exec failed due to failed deserialization process:
/// System.Text.Json.JsonException: The input does not contain any JSON tokens…</c>
/// with the JSON reader's stack trace inside the message. That was the whole of what
/// a user saw when a cluster behind a VPN was unreachable: the plugin had said exactly
/// why ("could not reach login.example.com"), and the app printed a parser error.
/// </para>
/// <para>
/// The plugin's reason is on stderr, which the library only redirects when someone
/// subscribes to its static <see cref="KubernetesClientConfiguration.ExecStdError"/>
/// event — otherwise the child inherits the app's stderr, which for a GUI process is
/// nowhere. <see cref="ExecCredentialCapture"/> subscribes once and routes each line to
/// the connect that ran the plugin through an <see cref="AsyncLocal{T}"/>, because
/// the event is process-wide while restored tabs connect in parallel.
/// </para>
/// </remarks>
public sealed class ExecCredentialException : Exception
{
    internal ExecCredentialException(string message, string? command, string? pluginOutput, Exception inner)
        : base(message, inner)
    {
        Command = command;
        PluginOutput = pluginOutput;
    }

    /// <summary>The plugin's executable, when it got as far as starting.</summary>
    public string? Command { get; }

    /// <summary>What the plugin wrote to stderr (its tail), or null when it wrote nothing.</summary>
    public string? PluginOutput { get; }
}

/// <summary>
/// Collects an exec credential plugin's stderr for the operation that ran it and turns
/// the library's exec failures into an <see cref="ExecCredentialException"/> — see there.
/// </summary>
internal static class ExecCredentialCapture
{
    private const int MaxLines = 8;
    private const int MaxChars = 600;

    private static readonly AsyncLocal<Capture?> Current = new();

    static ExecCredentialCapture() => KubernetesClientConfiguration.ExecStdError += OnStdError;

    /// <summary>
    /// Runs <paramref name="action"/>, which may run an exec plugin (building the client
    /// config, or refreshing an expired token before a request), and translates an exec
    /// failure into an <see cref="ExecCredentialException"/> that names the plugin and
    /// carries what it said.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        // Set inside this async method, so the value belongs to this flow and its
        // children only — the process's stderr reader is started from inside the
        // library's synchronous run and inherits it, a parallel connect does not.
        var capture = new Capture();
        Current.Value = capture;
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (KubeConfigException ex) when (ex.Message.StartsWith("external exec failed", StringComparison.Ordinal))
        {
            // The library waits for the plugin to exit with a timeout, and that overload of
            // WaitForExit does not wait for the redirected streams to drain — so the stderr
            // lines that explain the failure can still be in flight when it throws. Wait
            // for the stream's end (a null line), briefly; a plugin that never started has
            // no stream, and its message needs nothing from one.
            if (!ex.Message.StartsWith(StartFailurePrefix, StringComparison.Ordinal))
            {
                await Task.WhenAny(capture.Ended.Task, Task.Delay(StderrDrainTimeout)).ConfigureAwait(false);
            }

            throw Translate(ex, capture);
        }
    }

    private const string StartFailurePrefix = "external exec failed due to: ";
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(1);

    public static Task RunAsync(Func<Task> action) =>
        RunAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });

    private static void OnStdError(object? sender, DataReceivedEventArgs e)
    {
        if (Current.Value is not { } capture)
        {
            return;
        }

        if (sender is Process process)
        {
            capture.Command ??= SafeFileName(process);
        }

        if (e.Data is null)
        {
            capture.Ended.TrySetResult();
        }
        else if (!string.IsNullOrWhiteSpace(e.Data))
        {
            capture.Lines.Enqueue(e.Data.Trim());
            while (capture.Lines.Count > MaxLines && capture.Lines.TryDequeue(out _))
            {
            }
        }
    }

    private static string? SafeFileName(Process process)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(process.StartInfo.FileName);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static ExecCredentialException Translate(KubeConfigException ex, Capture capture)
    {
        var output = capture.Lines.IsEmpty ? null : string.Join(" ", capture.Lines);
        if (output is { Length: > MaxChars })
        {
            output = "…" + output[^MaxChars..];
        }

        var plugin = capture.Command is { Length: > 0 } command ? $"The credential plugin \"{command}\"" : "The credential plugin";
        var message = ex.Message switch
        {
            // The plugin could not be started at all: the library's own sentence
            // ("An error occurred trying to start process 'x' … cannot find the file")
            // is already the diagnosis.
            var m when m.StartsWith(StartFailurePrefix, StringComparison.Ordinal) =>
                $"Could not run the kubeconfig's credential plugin: {m[StartFailurePrefix.Length..]}",
            _ when output is not null => $"{plugin} failed: {output}",
            var m when m.Contains("timeout", StringComparison.OrdinalIgnoreCase) =>
                $"{plugin} did not finish in time.",
            _ => $"{plugin} did not return a credential. Run it in a terminal to see what it prints.",
        };

        return new ExecCredentialException(message, capture.Command, output, ex);
    }

    internal sealed class Capture
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Command;
    }
}
