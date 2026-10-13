using KubeNimbus.App.ViewModels;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The harness renders local times in UTC (<c>LocalTimeText.ToLocal</c>, set at startup), so
/// the published screenshots do not depend on the renderer's zone. This fails the run before
/// anything renders if a local-time path does not go through the seam: the tooltip text, the
/// time of day, and the log pane's own name for it.
/// </summary>
internal static class LocalZoneCheck
{
    internal static void Run()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 1, 54, TimeSpan.FromHours(-7));
        var exact = LocalTimeText.Exact(at);
        var clock = LocalTimeText.Clock(at);
        var log = LogLineViewModel.ToLocal(at);
        if (exact != "2026-10-04 19:01:54 UTC" || clock != "19:01:54 UTC" || log.Offset != TimeSpan.Zero)
            throw new InvalidOperationException(
                $"The harness renders local times in UTC, and got \"{exact}\", \"{clock}\" and a log offset of {log.Offset}.");
    }
}
