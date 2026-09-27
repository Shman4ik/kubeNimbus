using System.Globalization;
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
    /// <summary>
    /// How a server instant becomes this machine's local time. <see cref="DateTimeOffset.ToLocalTime"/>
    /// rather than a <c>TimeZoneInfo</c> lookup by id, which wants tzdata a NativeAOT
    /// binary on Linux may not have. A seam only so the tests can pin a zone that is not
    /// the machine's; nothing in the app assigns it.
    /// </summary>
    internal static Func<DateTimeOffset, DateTimeOffset> ToLocal { get; set; } = at => at.ToLocalTime();

    /// <summary>The server's own line, timestamp included — what Copy and Download write.</summary>
    public string RawLine { get; }

    public string? Timestamp { get; }

    /// <summary>The line with its leading server timestamp stripped, used for search and severity detection.</summary>
    public string Message { get; }

    public LogSeverity Severity { get; }

    // Three bools rather than one Severity bound through a value converter, and the
    // reason is in Theme.axaml beside the styles they drive: a Foreground *binding*
    // that produces AvaloniaProperty.UnsetValue does not fall back to the inherited
    // foreground, it falls back to TextElement.Foreground's own default of opaque
    // black — which in the dark theme made every line with no severity keyword
    // invisible. A class is set or it is not; an unclassified line carries no
    // Foreground binding at all and inherits normally.
    public bool IsErrorLine => Severity == LogSeverity.Error;

    public bool IsWarnLine => Severity == LogSeverity.Warn;

    public bool IsInfoLine => Severity == LogSeverity.Info;

    /// <summary>
    /// Which pod this line came from, in an aggregated (multi-pod) pane; null in the
    /// single-pod log pane, where the container strip above already names the source and
    /// a prefix on every line would only repeat it.
    /// </summary>
    public LogSourceViewModel? Source { get; }

    public bool HasSource => Source is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(MessageOffset))]
    private bool _showTimestamp;

    /// <summary>
    /// Print the timestamp as the server sent it (RFC3339, UTC, nanoseconds) rather than
    /// in local time. Only matters while <see cref="ShowTimestamp"/> is on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(MessageOffset))]
    private bool _utcTimestamp;

    /// <summary>
    /// The match the log search's next/previous is on. The line's matches are drawn in the
    /// stronger highlight, so "3 of 17" can be found on screen.
    /// </summary>
    [ObservableProperty]
    private bool _isCurrentMatch;

    private readonly DateTimeOffset? _at;
    private string? _localTimestamp;

    /// <summary>
    /// What the pane prints. Local time is <c>2026-07-20 10:41:02.114</c> — no offset, since
    /// every line on screen shares it and the toggle's tooltip names it; UTC is the server's
    /// token untouched, so a line can be matched character for character against another
    /// system's log. Copy and Download ignore both and write <see cref="RawLine"/>.
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (!ShowTimestamp || Timestamp is null)
            {
                return Message;
            }

            if (UtcTimestamp || _at is not { } at)
            {
                return RawLine;
            }

            _localTimestamp ??= FormatLocal(at);
            return $"{_localTimestamp} {Message}";
        }
    }

    /// <summary>
    /// Where <see cref="Message"/> starts inside <see cref="DisplayText"/>. Search matches
    /// the message, never the timestamp, so the highlight has to skip the prefix — or a
    /// search for "08:41" would light up text the match count does not include.
    /// </summary>
    public int MessageOffset => DisplayText.Length - Message.Length;

    public LogLineViewModel(string rawLine, bool showTimestamp, LogSourceViewModel? source = null, bool utcTimestamp = false)
    {
        RawLine = rawLine;
        (Timestamp, _at, Message) = SplitTimestamp(rawLine);
        Severity = DetectSeverity(Message);
        Source = source;
        _showTimestamp = showTimestamp;
        _utcTimestamp = utcTimestamp;
    }

    /// <summary>
    /// The line's server timestamp as a sortable instant, or null when the leading token
    /// was not one. The merge in an aggregated pane orders on this; the single-pod pane
    /// never needs it, since one stream is already in order.
    /// </summary>
    public DateTimeOffset? At => _at;

    /// <summary>Whether the message contains <paramref name="query"/> — the one rule both search modes and the highlight use.</summary>
    public bool Contains(string query) =>
        query.Length > 0 && Message.Contains(query, StringComparison.OrdinalIgnoreCase);

    internal static string FormatLocal(DateTimeOffset at) =>
        ToLocal(at).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// The UTC chip's tooltip. It names the local offset, because local timestamps are
    /// printed without one — every line on screen shares it — and someone lining a line up
    /// against another system's log needs to know what it is.
    /// </summary>
    public static string ZoneTooltip
    {
        get
        {
            var offset = ToLocal(DateTimeOffset.UtcNow).Offset;
            var local = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
            return $"Show timestamps in UTC, as the server sent them. Off: local time ({local}). "
                + "Copy and Download always write the server's own UTC line.";
        }
    }

    private static (string? Timestamp, DateTimeOffset? At, string Message) SplitTimestamp(string line)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0
            && DateTimeOffset.TryParse(line.AsSpan(0, spaceIndex), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return (line[..spaceIndex], at, line[(spaceIndex + 1)..]);
        }

        return (null, null, line);
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
