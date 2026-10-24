namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The line a keyboard user is on in a log pane (#236, <c>CommandId.LogLineKeys</c> and
/// <c>CommandId.CopyLogLine</c>). A log list is one Tab stop and none of its lines is one
/// (ENG-4), so before this a keyboard user reached the lines and no further: selecting one,
/// copying it and opening a JSON line's fields were pointer gestures. The arrow keys, Page Up/Down,
/// Home and End move this cursor over the lines on screen; Copy takes the line as it is drawn;
/// Enter opens or closes a JSON line. The first key lands on the newest line, where a
/// following pane is. A line the buffer has dropped, or the search has filtered out, is no
/// longer under the cursor, and the next key starts again from the newest line.
/// </summary>
public sealed class LogLineCursor
{
    public enum Step
    {
        Previous,
        Next,
        PageUp,
        PageDown,
        First,
        Last,
    }

    /// <summary>The line the keys are on, or null before the first key.</summary>
    public LogLineViewModel? Current { get; private set; }

    /// <summary>
    /// Moves over <paramref name="lines"/> (the pane's shown lines) and returns the line now
    /// under the cursor, or null when there are none. <paramref name="page"/> is how many
    /// lines Page Up and Page Down move, the lines the pane has on screen.
    /// </summary>
    public LogLineViewModel? Move(IReadOnlyList<LogLineViewModel> lines, Step step, int page = 20)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            Set(null);
            return null;
        }

        var last = lines.Count - 1;
        var index = Current is { } current ? IndexOf(lines, current) : -1;
        var target = index < 0
            ? (step == Step.First ? 0 : last)
            : step switch
            {
                Step.Previous => index - 1,
                Step.Next => index + 1,
                Step.PageUp => index - Math.Max(1, page),
                Step.PageDown => index + Math.Max(1, page),
                Step.First => 0,
                _ => last,
            };

        Set(lines[Math.Clamp(target, 0, last)]);
        return Current;
    }

    /// <summary>What Copy puts on the clipboard: the line as the pane draws it, or null with no line.</summary>
    public string? CopyText(IReadOnlyList<LogLineViewModel> lines) =>
        Current is { } line && IndexOf(lines, line) >= 0 ? line.DisplayText : null;

    /// <summary>Opens or closes the line's JSON fields; false when the line is not a JSON line.</summary>
    public bool ToggleJson(IReadOnlyList<LogLineViewModel> lines)
    {
        if (Current is not { IsJson: true } line || IndexOf(lines, line) < 0)
        {
            return false;
        }

        line.IsExpanded = !line.IsExpanded;
        return true;
    }

    private void Set(LogLineViewModel? line)
    {
        if (ReferenceEquals(Current, line))
        {
            return;
        }

        if (Current is { } previous)
        {
            previous.IsKeyLine = false;
        }

        Current = line;
        if (line is not null)
        {
            line.IsKeyLine = true;
        }
    }

    private static int IndexOf(IReadOnlyList<LogLineViewModel> lines, LogLineViewModel line)
    {
        // From the end: the cursor is usually near the newest lines, and the buffer holds
        // thousands.
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(lines[i], line))
            {
                return i;
            }
        }

        return -1;
    }
}
