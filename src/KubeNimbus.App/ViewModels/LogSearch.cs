namespace KubeNimbus.App.ViewModels;

/// <summary>What the two log panes' search boxes say, decided once so the panes cannot disagree.</summary>
public static class LogSearch
{
    /// <summary>The context sizes the filter offers: none, then grep-sized to screen-sized.</summary>
    public static IReadOnlyList<int> ContextChoices { get; } = [0, 2, 5, 10, 25];

    /// <summary>
    /// The counter while filtering (or while there is nothing to find): the matching lines,
    /// not the shown ones — context lines are there to be read around a match, and counting
    /// them would make "12 lines" mean something different at every context size. A pattern
    /// that does not parse says so instead.
    /// </summary>
    public static string FilterSummary(string text, string? error, LogQuery? query, IReadOnlyCollection<LogLineViewModel> shown)
    {
        if (text.Length == 0)
        {
            return "";
        }

        if (error is not null)
        {
            return "Invalid pattern";
        }

        if (query is not { HasInclude: true })
        {
            return "";
        }

        var matching = shown.Count(l => !l.IsContextLine);
        return $"{matching:N0} line{(matching == 1 ? "" : "s")}";
    }

    /// <summary>Adds how many lines a <c>!term</c> hid, when it hid any: "3 of 17 · 40 hidden".</summary>
    public static string WithHidden(string summary, int hidden) =>
        hidden == 0 ? summary : summary.Length == 0 ? $"{hidden:N0} hidden" : $"{summary} · {hidden:N0} hidden";

    /// <summary>
    /// Whether a change from <paramref name="before"/> to <paramref name="after"/> changes
    /// which lines are shown, and so needs the projection rebuilt: always while filtering,
    /// and in find mode only when an exclusion is involved on either side — find mode
    /// otherwise keeps every line, and rebuilding the pane on each keystroke is what it
    /// exists to avoid.
    /// </summary>
    public static bool ChangesShownLines(bool filterMode, LogQuery? before, LogQuery? after) =>
        filterMode || before is { HasExcludes: true } || after is { HasExcludes: true };

    /// <summary>The context picker's caption for <paramref name="lines"/>.</summary>
    public static string ContextLabel(int lines) => lines == 0 ? "No context" : $"±{lines} lines";
}
