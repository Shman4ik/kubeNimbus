using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>Coarse severity read off a log line's text — drives color coding, nothing more (no structured log parsing).</summary>
public enum LogSeverity
{
    None,
    Info,
    Warn,
    Error,
}

/// <summary>
/// One buffered log line. The server is always asked for RFC3339 timestamps
/// (<c>timestamps=true</c>, see <c>ClusterClient.StreamPodLogsAsync</c>) so the
/// timestamp toggle is a pure display concern here — no need to re-stream when
/// it flips, just recompute <see cref="DisplayText"/>.
/// </summary>
public sealed partial class LogLineViewModel : ObservableObject
{
    public string RawLine { get; }

    public string? Timestamp { get; }

    /// <summary>The line with its leading server timestamp stripped, used for search and severity detection.</summary>
    public string Message { get; }

    public LogSeverity Severity { get; }

    [ObservableProperty]
    private bool _showTimestamp;

    public string DisplayText => ShowTimestamp && Timestamp is not null ? RawLine : Message;

    public LogLineViewModel(string rawLine, bool showTimestamp)
    {
        RawLine = rawLine;
        (Timestamp, Message) = SplitTimestamp(rawLine);
        Severity = DetectSeverity(Message);
        _showTimestamp = showTimestamp;
    }

    partial void OnShowTimestampChanged(bool value) => OnPropertyChanged(nameof(DisplayText));

    private static (string? Timestamp, string Message) SplitTimestamp(string line)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0 && DateTimeOffset.TryParse(line[..spaceIndex], out _))
        {
            return (line[..spaceIndex], line[(spaceIndex + 1)..]);
        }

        return (null, line);
    }

    private static LogSeverity DetectSeverity(string message)
    {
        if (ContainsToken(message, "FATAL") || ContainsToken(message, "PANIC") || ContainsToken(message, "ERROR"))
        {
            return LogSeverity.Error;
        }

        if (ContainsToken(message, "WARN") || ContainsToken(message, "WARNING"))
        {
            return LogSeverity.Warn;
        }

        if (ContainsToken(message, "INFO"))
        {
            return LogSeverity.Info;
        }

        return LogSeverity.None;
    }

    /// <summary>
    /// Whether <paramref name="token"/> appears as a whole word. A plain substring test
    /// is what this used to be, and it coloured <c>GET /api/v1/errors 200</c> red and
    /// <c>infofmt</c> blue — a severity heuristic that fires on the request path is
    /// worse than none, because it teaches you to stop trusting the colour.
    /// The boundary is "not a letter or digit", so <c>[ERROR]</c>, <c>level=error</c>,
    /// <c>ERROR:</c> and <c>"level":"warn"</c> all still match.
    /// </summary>
    private static bool ContainsToken(string text, string token)
    {
        var start = 0;
        while (start <= text.Length - token.Length)
        {
            var index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + token.Length;
            var after = afterIndex == text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }
}
