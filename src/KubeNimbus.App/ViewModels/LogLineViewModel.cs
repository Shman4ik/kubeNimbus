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

    /// <summary>
    /// The server's own line, timestamp included, with terminal escape sequences removed
    /// (<see cref="TerminalEscapes"/>) — what Copy and Download write. Removed here rather
    /// than only on screen, so a copied line pasted into a ticket is the line that was read.
    /// </summary>
    public string RawLine { get; }

    public string? Timestamp { get; }

    /// <summary>The line with its leading server timestamp stripped, used for search and severity detection.</summary>
    public string Message { get; }

    public LogSeverity Severity { get; private set; }

    /// <summary>
    /// The severity was taken from the line above (<see cref="InheritFrom"/>), not read off
    /// this line: a stack trace frame. The error jump skips these, so a forty-frame trace is
    /// one stop, on the line that threw, and not forty.
    /// </summary>
    public bool IsInheritedSeverity { get; private set; }

    /// <summary>Where the keyword that decided <see cref="Severity"/> starts in <see cref="Message"/>; -1 when none did.</summary>
    public int LevelIndex { get; }

    public int LevelLength { get; }

    /// <summary>
    /// Where the level keyword starts in <see cref="DisplayText"/>, -1 when there is none.
    /// Only the keyword is coloured, never the line: an ASP.NET pod whose every line is
    /// <c>info:</c> used to turn the whole pane blue, and colour that is on every line
    /// tells the reader nothing. Errors and warnings are marked on the row as well, by the
    /// view; info is the keyword alone.
    /// </summary>
    public int DisplayLevelStart => LevelIndex < 0 ? -1 : MessageOffset + LevelIndex;

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
    [NotifyPropertyChangedFor(nameof(DisplayLevelStart))]
    private bool _showTimestamp;

    /// <summary>
    /// Print the timestamp as the server sent it (RFC3339, UTC, nanoseconds) rather than
    /// in local time. Only matters while <see cref="ShowTimestamp"/> is on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(MessageOffset))]
    [NotifyPropertyChangedFor(nameof(DisplayLevelStart))]
    private bool _utcTimestamp;

    /// <summary>
    /// The match the log search's next/previous is on. The line's matches are drawn in the
    /// stronger highlight, so "3 of 17" can be found on screen.
    /// </summary>
    [ObservableProperty]
    private bool _isCurrentMatch;

    /// <summary>Shown only as context around a match while the search filters (<see cref="LogProjection"/>); drawn dimmer.</summary>
    [ObservableProperty]
    private bool _isContextLine;

    /// <summary>Lines were hidden between this one and the one shown before it — grep's <c>--</c>, drawn as a rule.</summary>
    [ObservableProperty]
    private bool _isGapBefore;

    /// <summary>The error/warning jump (<see cref="LogProblems"/>) is on this line; drawn as a row highlight.</summary>
    [ObservableProperty]
    private bool _isProblemCursor;

    /// <summary>
    /// The message is a JSON object — the shape structured loggers print one per line, and
    /// the most requested thing in this corner of the market (k9s#364, Lens#3045). Read off
    /// the shape only; it is parsed when someone opens it (<see cref="PrettyJson"/>).
    /// </summary>
    public bool IsJson { get; }

    /// <summary>
    /// The JSON object laid out below the line, one field per row. A view of the line, never
    /// a rewrite of it: the line itself, the search, Copy and Save keep the server's bytes —
    /// Headlamp's prettify rewrote its buffer and lost lines doing it.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    private string? _prettyJson;

    /// <summary>The message indented, or a sentence saying it did not parse. Built on first use.</summary>
    public string PrettyJson => _prettyJson ??= FormatJson(Message);

    internal static string FormatJson(string message)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            using var buffer = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
                   {
                       Indented = true,
                       // Non-ASCII and quotes as written, not \u-escaped: this is for reading.
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                document.WriteTo(writer);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"Not valid JSON: {ex.Message}";
        }
    }

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
        RawLine = TerminalEscapes.Strip(rawLine);
        (Timestamp, _at, Message) = SplitTimestamp(RawLine);
        (Severity, LevelIndex, LevelLength) = DetectSeverity(Message);
        var trimmed = Message.AsSpan().Trim();
        IsJson = trimmed.Length > 1 && trimmed[0] == '{' && trimmed[^1] == '}';
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

    /// <summary>Whether the message matches <paramref name="query"/> — the one rule both search modes, the highlight and the ruler use.</summary>
    public bool Matches(LogQuery? query)
    {
        if (query is null)
        {
            return false;
        }

        // Remembered per query: the find, the projection and the overview ruler all ask the
        // same line the same question after every flush, and a regex answer is not free.
        if (!ReferenceEquals(query, _matchedQuery))
        {
            _matched = query.IsMatch(Message);
            _matchedQuery = query;
        }

        return _matched;
    }

    private LogQuery? _matchedQuery;
    private bool _matched;

    /// <summary>Whether the message contains <paramref name="text"/>, ignoring case — a plain query's rule.</summary>
    public bool Contains(string text) =>
        text.Length > 0 && Message.Contains(text, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Whether <paramref name="message"/> reads as the continuation of the line above rather
    /// than a line of its own: indented (a .NET or Java frame, a Go goroutine dump, a Python
    /// traceback's body), a frame line, a chained cause, a trace's elision, or Python's
    /// traceback header. The kubelet stores a multi-line exception as one line per line, so
    /// without this a stack trace under an error line read as a block of plain text and
    /// Errors only kept the one line and threw its trace away.
    /// </summary>
    internal static bool IsContinuation(string message) =>
        message.Length > 0
        && (message[0] is ' ' or '\t'
            || message.StartsWith("at ", StringComparison.Ordinal)
            || message.StartsWith("---", StringComparison.Ordinal)
            || message.StartsWith("Caused by:", StringComparison.Ordinal)
            || message.StartsWith("Traceback (most recent call last):", StringComparison.Ordinal)
            || (message.StartsWith("... ", StringComparison.Ordinal) && message.EndsWith(" more", StringComparison.Ordinal))
            || IsExceptionHeader(message));

    /// <summary>
    /// An exception's type at the start of the line, <c>java.lang.IllegalStateException: …</c>
    /// or <c>System.InvalidOperationException: …</c>: Java and .NET print it flush left, so it
    /// is the one line of a trace an indentation rule misses. A dotted name ending in
    /// Exception or Error, then a colon or the end of the line.
    /// </summary>
    private static bool IsExceptionHeader(string message)
    {
        var end = message.IndexOf(':');
        var name = end < 0 ? message.AsSpan() : message.AsSpan(0, end);
        if (name.Length == 0 || name.IndexOf('.') < 0
            || !(name.EndsWith("Exception", StringComparison.Ordinal) || name.EndsWith("Error", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '$' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Takes <paramref name="previous"/>'s severity when this line has none of its own and
    /// continues it (<see cref="IsContinuation"/>). Called by the panes as a line is
    /// buffered, with the line before it from the same pod, so a trace keeps the error bar
    /// down its whole length. Only an error or a warning is inherited: an indented line
    /// under an info line is just an indented line.
    /// </summary>
    internal void InheritFrom(LogLineViewModel? previous)
    {
        if (previous is null || Severity != LogSeverity.None || LevelIndex >= 0
            || previous.Severity is not (LogSeverity.Error or LogSeverity.Warn)
            || !IsContinuation(Message))
        {
            return;
        }

        Severity = previous.Severity;
        IsInheritedSeverity = true;
    }

    /// <summary>
    /// The level keywords, with the severity each one means. <c>fail:</c> and <c>crit:</c>
    /// are .NET's console logger (Microsoft.Extensions.Logging's four-letter names) and
    /// match only with the colon that logger prints, so "tests fail" in a sentence is not an
    /// error line. <c>ERR</c>/<c>WRN</c>/<c>INF</c> are zerolog's and Seq's short forms.
    /// </summary>
    private static readonly (string Token, char? FollowedBy, LogSeverity Severity)[] LevelTokens =
    [
        ("FATAL", null, LogSeverity.Error),
        ("PANIC", null, LogSeverity.Error),
        ("ERROR", null, LogSeverity.Error),
        ("ERR", null, LogSeverity.Error),
        ("FAIL", ':', LogSeverity.Error),
        ("CRIT", ':', LogSeverity.Error),
        ("WARNING", null, LogSeverity.Warn),
        ("WARN", null, LogSeverity.Warn),
        ("WRN", null, LogSeverity.Warn),
        ("INFO", null, LogSeverity.Info),
        ("INF", null, LogSeverity.Info),
    ];

    /// <summary>
    /// The line's severity and where its level keyword is. <b>The earliest keyword wins</b>:
    /// a logger prints the level before the message, so <c>info: retrying after error</c> is
    /// an info line that mentions an error, not an error line — the rule used to be "any
    /// ERROR anywhere", which coloured such lines red. klog's own prefix (<c>E0928
    /// 10:00:00.000000</c>, what every Kubernetes controller prints) is read first.
    /// </summary>
    internal static (LogSeverity Severity, int Index, int Length) DetectSeverity(string message)
    {
        if (KlogSeverity(message) is { } klog)
        {
            return (klog, 0, 1);
        }

        if (StructuredSeverity(message) is { } structured)
        {
            return structured;
        }

        var best = (Severity: LogSeverity.None, Index: -1, Length: 0);
        foreach (var (token, followedBy, severity) in LevelTokens)
        {
            var index = IndexOfToken(message, token, followedBy);
            if (index >= 0 && (best.Index < 0 || index < best.Index))
            {
                best = (severity, index, token.Length);
            }
        }

        return best;
    }

    /// <summary>klog's header, <c>Lmmdd hh:mm:ss</c>: I, W, E or F, four digits, a space, a time.</summary>
    private static LogSeverity? KlogSeverity(string message)
    {
        if (message.Length < 11
            || !char.IsAsciiDigit(message[1]) || !char.IsAsciiDigit(message[2])
            || !char.IsAsciiDigit(message[3]) || !char.IsAsciiDigit(message[4])
            || message[5] != ' ' || !char.IsAsciiDigit(message[6]) || !char.IsAsciiDigit(message[7]) || message[8] != ':')
        {
            return null;
        }

        return message[0] switch
        {
            'I' => LogSeverity.Info,
            'W' => LogSeverity.Warn,
            'E' or 'F' => LogSeverity.Error,
            _ => null,
        };
    }

    /// <summary>
    /// Where <paramref name="token"/> first appears as a whole word, -1 if nowhere. A plain substring test
    /// is what this used to be, and it coloured <c>GET /api/v1/errors 200</c> red and
    /// <c>infofmt</c> blue — a severity heuristic that fires on the request path is
    /// worse than none, because it teaches you to stop trusting the colour.
    /// The boundary is "not a letter or digit", so <c>[ERROR]</c>, <c>level=error</c>,
    /// <c>ERROR:</c> and <c>"level":"warn"</c> all still match. With
    /// <paramref name="followedBy"/>, the token must be immediately followed by that character.
    /// </summary>
    private static int IndexOfToken(string text, string token, char? followedBy = null)
    {
        var start = 0;
        while (start <= text.Length - token.Length)
        {
            var index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + token.Length;
            var after = followedBy is { } required
                ? afterIndex < text.Length && text[afterIndex] == required
                : afterIndex == text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
            {
                return index;
            }

            start = index + 1;
        }

        return -1;
    }

    /// <summary>
    /// The keys structured loggers write the level under: logfmt (<c>level=</c>,
    /// <c>lvl=</c>), JSON (<c>"level":</c>, <c>"severity":</c>, .NET's <c>"LogLevel":</c>) and
    /// Serilog's compact <c>"@l":</c>. When a line has one, its value is the level and the
    /// rest of the line is only the message — a JSON line whose message says "error" but
    /// whose level is info is an info line.
    /// </summary>
    private static readonly string[] LevelKeys =
        ["\"level\":", "\"lvl\":", "\"severity\":", "\"LogLevel\":", "\"@l\":", "level=", "lvl="];

    private static (LogSeverity Severity, int Index, int Length)? StructuredSeverity(string message)
    {
        foreach (var key in LevelKeys)
        {
            var at = message.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || (at > 0 && char.IsLetterOrDigit(message[at - 1])))
            {
                continue;
            }

            var start = at + key.Length;
            while (start < message.Length && message[start] is ' ' or '"')
            {
                start++;
            }

            var end = start;
            while (end < message.Length && char.IsLetter(message[end]))
            {
                end++;
            }

            if (end == start)
            {
                continue;
            }

            var value = message.AsSpan(start, end - start);
            LogSeverity? severity =
                value.StartsWith("err", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("fatal", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("panic", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("crit", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("emerg", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("alert", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Error
                : value.StartsWith("warn", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Warn
                : value.StartsWith("info", StringComparison.OrdinalIgnoreCase)
                  || value.StartsWith("notice", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Info
                : value.StartsWith("debug", StringComparison.OrdinalIgnoreCase)
                  || value.StartsWith("trace", StringComparison.OrdinalIgnoreCase)
                  || value.StartsWith("verbose", StringComparison.OrdinalIgnoreCase) ? LogSeverity.None
                : null;
            if (severity is { } s)
            {
                return (s, start, end - start);
            }
        }

        return null;
    }
}
