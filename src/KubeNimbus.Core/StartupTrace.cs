using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace KubeNimbus.Core;

/// <summary>
/// Opt-in timings of one launch, from process start to the first rows of each list:
/// <c>KUBENIMBUS_STARTUP_TRACE=&lt;file&gt;</c> appends one line per phase to that file, and
/// <c>KUBENIMBUS_STARTUP_TRACE=stderr</c> writes them to standard error. Unset, which is
/// every ordinary launch, every <see cref="Mark"/> returns at its first check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> "It feels slow on the first launch" could not be answered with a
/// number: the connect path runs a credential plugin, <c>/version</c>, discovery, the
/// namespace list and up to eight lists, partly in parallel and partly one after another,
/// and nothing said which of them a user was waiting for. Each phase is marked once, the
/// first time it is reached for its subject (a cluster context), so the file reads as the
/// launch's timeline and a later kind switch or reconnect adds nothing to it.
/// <c>docs/engineering/startup-performance.md</c> says how to read it.
/// </para>
/// <para>
/// <b>What it writes.</b> Milliseconds since the process started, the phase, and its
/// subject — a kubeconfig context name, never a credential, an object or a server
/// address. Nothing is written unless the variable names where to write it, and the app
/// never sets it, so <c>PRIVACY.md</c>'s list of what the app stores is unchanged.
/// </para>
/// </remarks>
public static class StartupTrace
{
    /// <summary>The environment variable that turns the trace on and names where it goes.</summary>
    public const string Variable = "KUBENIMBUS_STARTUP_TRACE";

    private static readonly StartupTraceLog Log = StartupTraceLog.Open(
        Environment.GetEnvironmentVariable(Variable), ProcessAge());

    /// <summary>Whether this launch writes a trace.</summary>
    public static bool IsEnabled => Log.IsEnabled;

    /// <summary>
    /// Records <paramref name="phase"/> for <paramref name="subject"/> the first time it is
    /// reached; later calls with the same pair are ignored. <paramref name="detail"/> is
    /// written but is not part of what makes a mark the first one.
    /// </summary>
    public static void Mark(string phase, string? subject = null, string? detail = null) =>
        Log.Mark(phase, subject, detail);

    /// <summary>
    /// How long the process had been running when this class was first used, so the times
    /// include runtime start-up and not only what follows <c>Main</c>.
    /// </summary>
    private static TimeSpan ProcessAge()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var age = DateTime.Now - process.StartTime;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }
}

/// <summary>The trace's state, separate from the static entry point so tests can drive it.</summary>
internal sealed class StartupTraceLog
{
    private readonly TextWriter? _writer;
    private readonly Func<TimeSpan> _elapsed;
    private readonly ConcurrentDictionary<(string Phase, string Subject), byte> _seen = new();
    private readonly Lock _gate = new();

    internal StartupTraceLog(TextWriter? writer, Func<TimeSpan> elapsed)
    {
        _writer = writer;
        _elapsed = elapsed;
    }

    internal bool IsEnabled => _writer is not null;

    /// <summary>
    /// A log for the variable's value: <c>stderr</c> writes to standard error, any other
    /// non-empty value is a file to append to. A file that cannot be opened turns the trace
    /// off rather than failing the launch it was meant to measure.
    /// </summary>
    internal static StartupTraceLog Open(string? target, TimeSpan processAge)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan Elapsed() => processAge + clock.Elapsed;
        if (string.IsNullOrWhiteSpace(target))
        {
            return new StartupTraceLog(null, Elapsed);
        }

        if (string.Equals(target.Trim(), "stderr", StringComparison.OrdinalIgnoreCase))
        {
            return new StartupTraceLog(Console.Error, Elapsed);
        }

        try
        {
            var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StartupTraceLog(new StreamWriter(stream) { AutoFlush = true }, Elapsed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new StartupTraceLog(null, Elapsed);
        }
    }

    internal void Mark(string phase, string? subject, string? detail)
    {
        if (_writer is null || !_seen.TryAdd((phase, subject ?? ""), 0))
        {
            return;
        }

        var line = Format(_elapsed(), phase, subject, detail);
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // A full disk or a deleted file ends the trace, never the launch.
            }
        }
    }

    /// <summary>One line: milliseconds, phase, then subject and detail when there are any, tab-separated.</summary>
    internal static string Format(TimeSpan elapsed, string phase, string? subject, string? detail)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:F0}\t{phase}");
        if (!string.IsNullOrEmpty(subject) || !string.IsNullOrEmpty(detail))
        {
            line += "\t" + subject;
        }

        if (!string.IsNullOrEmpty(detail))
        {
            line += "\t" + detail;
        }

        return line;
    }
}
