namespace KubeNimbus.App.ViewModels;

/// <summary>A log request's opening window. The cap applies to retained lines, not to the request.</summary>
public sealed record LogRange(string Label, int? TailLines = null, int? SinceSeconds = null)
{
    public static readonly LogRange Last200 = new("Last 200 lines", TailLines: 200);
    public static readonly LogRange Last1000 = new("Last 1000 lines", TailLines: 1000);
    public static readonly LogRange FiveMinutes = new("Last 5 minutes", SinceSeconds: 300);
    public static readonly LogRange OneHour = new("Last 1 hour", SinceSeconds: 3600);
    public static readonly LogRange OneDay = new("Last 24 hours", SinceSeconds: 86400);
    public static readonly LogRange Everything = new("Everything");

    public static IReadOnlyList<LogRange> Choices { get; } =
        [Last200, Last1000, FiveMinutes, OneHour, OneDay, Everything];

    /// <summary>Multi-pod backfill shares one pane cap; each pod gets its share of a line range.</summary>
    public int? TailForPod(int bufferLines, int podCount) => TailLines is { } requested
        ? Math.Min(requested, Math.Max(WorkloadLogsTabViewModel.MinPerPodTailLines, bufferLines / Math.Max(1, podCount)))
        : null;

    public string EmptyMessage => SinceSeconds is not null
        ? $"No lines in the {Label.ToLowerInvariant()}."
        : TailLines is not null ? "No lines in the selected range." : "No retained log lines.";
}
