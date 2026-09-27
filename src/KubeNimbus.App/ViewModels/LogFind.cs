namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The "find" half of a log pane's search: which shown lines match, and which one
/// next/previous is on. Shared by pod detail's pane and the multi-pod pane, so the two
/// cannot disagree about what "3 of 17" means.
/// </summary>
/// <remarks>
/// <para>
/// Matches are lines, not occurrences: a line that says "timeout" twice is one stop for
/// next/previous, and both occurrences are highlighted on it. A log is read by the line,
/// and a counter that stepped twice through one line would read as the pane not moving.
/// </para>
/// <para>
/// A new query lands on the <b>newest</b> match. The panes follow the bottom of the stream,
/// and in an incident the question is "when did this last happen" before it is "when did
/// it first happen"; Previous then walks back in time. Both directions wrap. Otherwise the
/// current match is kept across new lines, trims and filter changes for as long as it is
/// still shown, so a following stream never drags the reader off the line they are on.
/// </para>
/// </remarks>
public sealed class LogFind
{
    private readonly List<LogLineViewModel> _matches = [];
    private LogLineViewModel? _current;

    /// <summary>How many shown lines match.</summary>
    public int Count => _matches.Count;

    /// <summary>The line next/previous is on; null when nothing matches or no search is running.</summary>
    public LogLineViewModel? Current => _current;

    /// <summary>1-based position of <see cref="Current"/> among the matches, 0 when there is none.</summary>
    public int Position => _current is null ? 0 : _matches.IndexOf(_current) + 1;

    /// <summary>
    /// Re-reads the matches from the lines on screen. <paramref name="newQuery"/> moves to
    /// the newest match; otherwise the current one is kept if it is still shown, and the
    /// newest is taken if it is not (it was trimmed, or a level or pod was hidden).
    /// </summary>
    public void Update(IEnumerable<LogLineViewModel> shown, string query, bool newQuery)
    {
        _matches.Clear();
        if (query.Length > 0)
        {
            foreach (var line in shown)
            {
                if (line.Contains(query))
                {
                    _matches.Add(line);
                }
            }
        }

        var keep = !newQuery && _current is not null && _matches.Contains(_current);
        MoveTo(keep ? _current : _matches.Count > 0 ? _matches[^1] : null);
    }

    /// <summary>Forgets every match — the search box was emptied or the pane switched to filtering.</summary>
    public void Clear()
    {
        _matches.Clear();
        MoveTo(null);
    }

    /// <summary>The next (later) match, wrapping from the newest to the oldest.</summary>
    public void Next() => Step(+1);

    /// <summary>The previous (earlier) match, wrapping from the oldest to the newest.</summary>
    public void Previous() => Step(-1);

    /// <summary>"3 of 17", or "No matches" — the counter in the search box.</summary>
    public string Summary(string query) =>
        query.Length == 0 ? "" : Count == 0 ? "No matches" : $"{Position:N0} of {Count:N0}";

    private void Step(int direction)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var index = _current is null ? -1 : _matches.IndexOf(_current);
        var next = index < 0
            ? (direction > 0 ? 0 : _matches.Count - 1)
            : ((index + direction) % _matches.Count + _matches.Count) % _matches.Count;
        MoveTo(_matches[next]);
    }

    private void MoveTo(LogLineViewModel? line)
    {
        if (ReferenceEquals(line, _current))
        {
            return;
        }

        if (_current is not null)
        {
            _current.IsCurrentMatch = false;
        }

        _current = line;
        if (line is not null)
        {
            line.IsCurrentMatch = true;
        }
    }
}
