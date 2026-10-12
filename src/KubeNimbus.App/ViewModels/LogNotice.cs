namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The one line both log panes show above their lines while they hold some (#319): that
/// the reader cleared lines, and the pane's state when it is not a healthy live tail (a
/// snapshot, the run before, a stream that ended and why). The same facts are the empty
/// pane's placeholder; this is where they go once there are lines to read, because a
/// placeholder over a full pane is never drawn and a snapshot then looked like a live tail.
/// </summary>
internal static class LogNotice
{
    /// <param name="buffered">Lines in the pane's buffer; with none, the placeholder says it.</param>
    /// <param name="cleared">Lines the reader cleared, when they did.</param>
    /// <param name="status">The pane's state sentence, or null while a stream is healthy.</param>
    public static string? Compose(int buffered, int? cleared, string? status)
    {
        if (buffered == 0)
        {
            return null;
        }

        var what = cleared is { } n
            ? $"Cleared {n:N0} line{(n == 1 ? "" : "s")}; the lines shown arrived since."
            : null;
        return (what, status is { Length: > 0 } ? status : null) switch
        {
            (null, null) => null,
            ({ } c, null) => c,
            (null, { } s) => s,
            ({ } c, { } s) => $"{c} {s}",
        };
    }
}
