namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Where the palette's log rows stand, for the notes that say so. Read on the UI thread
/// from the tab each time the palette re-reads its source.
/// </summary>
/// <param name="IsConnected">The tab has a cluster (or is the demo cluster).</param>
/// <param name="IsConnecting">Still dialling.</param>
/// <param name="IsLoading">A one-shot list is in flight.</param>
/// <param name="Scope">"payments" or "every namespace".</param>
/// <param name="Result">The last completed look at this scope; null before the first.</param>
public sealed record LogTargetsState(
    bool IsConnected,
    bool IsConnecting,
    bool IsLoading,
    string Scope,
    LogTargetList? Result);

/// <summary>
/// Turns log targets into palette rows, and a <see cref="LogTargetsState"/> into the notes
/// above them. Pure and UI-free (no dispatcher, no Avalonia), so the row building runs on
/// the pool thread that did the listing and the tests read exactly what the palette will.
/// </summary>
public static class LogPaletteRows
{
    public const string PodIcon = "ClockOutlineIconGeometry";

    public const string WorkloadIcon = "LayersIconGeometry";

    /// <summary>
    /// One row per target, in the order given. A pod reads <c>Logs: api-7f9c-x7k2m</c> over
    /// its namespace and status; a workload reads <c>Logs: Deployment/api</c> over its
    /// namespace and readiness, and says it is one stream across its pods — which is the
    /// difference between the two rows a search for "api" turns up. In a fleet each
    /// subtitle ends with the cluster, because the same name exists on every member.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Build(IReadOnlyList<LogTarget> targets, Action<LogTarget> open)
    {
        var rows = new List<PaletteItem>(targets.Count);
        foreach (var target in targets)
        {
            rows.Add(Row(target, open));
        }

        return rows;
    }

    public static PaletteItem Row(LogTarget target, Action<LogTarget> open)
    {
        var resource = target.Resource;
        var ns = resource.Namespace ?? "";
        var cluster = target.ClusterName;
        var onCluster = cluster.Length > 0 ? $" · {cluster}" : "";
        var summary = ResourceStatusSummary.Summarize(resource);

        if (target.IsPod)
        {
            var status = summary.Status.Length > 0 ? summary.Status : "no status yet";
            return new PaletteItem($"Logs: {resource.Name}", $"{ns} · {status}{onCluster}", PodIcon, () => open(target))
            {
                Scope = PaletteScope.Logs,
                SearchText = $"{resource.Name} {ns} {cluster}",
            };
        }

        var kind = target.Descriptor.Kind;
        var state = summary.Ready.Length > 0 ? $"{summary.Ready} ready" : summary.Status;
        var subtitle = state.Length > 0
            ? $"{ns} · {state} · every pod, one stream{onCluster}"
            : $"{ns} · every pod, one stream{onCluster}";
        return new PaletteItem($"Logs: {kind}/{resource.Name}", subtitle, WorkloadIcon, () => open(target))
        {
            Scope = PaletteScope.Logs,
            SearchText = $"{kind}/{resource.Name} {ns} {cluster}",
        };
    }

    /// <summary>
    /// The notes: everything about the log rows that is not a row. At most one of the
    /// connection / loading / empty states, plus one per refused list and one per list
    /// that hit the cap. The palette decides when they are shown; this only words them.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Notes(LogTargetsState state)
    {
        var notes = new List<PaletteItem>();

        if (!state.IsConnected)
        {
            notes.Add(state.IsConnecting
                ? PaletteItem.Note(
                    "Logs: waiting for the cluster to connect",
                    "Pods and workloads are listed as soon as it answers",
                    PaletteScope.Logs,
                    PodIcon)
                : PaletteItem.Note(
                    "Logs: not connected",
                    "Pods and workloads are listed from a connected cluster",
                    PaletteScope.Logs));
            return notes;
        }

        if (state.IsLoading)
        {
            var shown = state.Result?.Targets.Count ?? 0;
            notes.Add(PaletteItem.Note(
                $"Loading pods and workloads in {state.Scope}…",
                shown > 0 ? $"Showing the {shown:N0} from the last look meanwhile — keep typing" : "Keep typing — they appear here as they arrive",
                PaletteScope.Logs,
                PodIcon));
        }

        if (state.Result is not { } result)
        {
            return notes;
        }

        foreach (var problem in result.Problems)
        {
            notes.Add(PaletteItem.Note($"Logs: {problem.Summary}", problem.Detail, PaletteScope.Logs));
        }

        foreach (var truncation in result.Truncations)
        {
            notes.Add(PaletteItem.Note(
                $"Logs: {truncation}",
                "Objects past the cap aren't searched here — pick a namespace to narrow it",
                PaletteScope.Logs));
        }

        if (!state.IsLoading && result.Targets.Count == 0 && result.Problems.Count == 0)
        {
            notes.Add(PaletteItem.Note(
                $"No pods or workloads in {state.Scope}",
                "Nothing here has logs to open — try another namespace",
                PaletteScope.Logs,
                PodIcon));
        }

        return notes;
    }
}
