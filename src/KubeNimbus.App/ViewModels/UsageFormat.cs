using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Display strings for a <see cref="UsageHistory"/> window — shared by the list
/// rows, the container chips and pod detail's Usage tab so "now / peak / how far
/// back this graph goes" reads identically everywhere.
/// </summary>
internal static class UsageFormat
{
    /// <summary>
    /// How much wall-clock the graph covers, phrased at the coarsest useful unit.
    /// Empty for a window of zero (fewer than two samples) — the caller says
    /// "collecting…" in that case rather than showing "0s".
    /// </summary>
    public static string Window(TimeSpan window) => window switch
    {
        { Ticks: <= 0 } => "",
        { TotalMinutes: < 1 } => $"{window.TotalSeconds:0}s",
        { TotalMinutes: < 60 } => $"{window.TotalMinutes:0} min",
        _ => $"{window.TotalHours:0.#} h",
    };

    /// <summary>"last 7 min" / "collecting…" — the caption under a chart.</summary>
    public static string WindowCaption(UsageHistory history) =>
        Window(history.Window) is { Length: > 0 } window
            ? $"last {window} · {Samples(history.Count)}"
            : "collecting…";

    /// <summary>Tooltip for one measure's chart: current and peak, plus how much history is behind it.</summary>
    public static string Tooltip(string label, string now, string peak, UsageHistory history)
    {
        var window = Window(history.Window);
        var extent = window.Length == 0 ? Samples(history.Count) : $"{Samples(history.Count)} over {window}";
        return $"{label}  now {now} · peak {peak}\n{extent}";
    }

    private static string Samples(int count) => count == 1 ? "1 sample" : $"{count} samples";
}
