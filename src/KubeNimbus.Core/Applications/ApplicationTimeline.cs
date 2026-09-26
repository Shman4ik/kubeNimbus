namespace KubeNimbus.Core.Applications;

public enum TimelineKind
{
    /// <summary>A deploy: an Argo CD sync from <c>status.history</c>, or a ReplicaSet's creation.</summary>
    Deploy,

    /// <summary>A container run that ended — the one termination per container the kubelet records.</summary>
    Termination,

    /// <summary>A Warning Event about one of the app's objects.</summary>
    WarningEvent,
}

/// <summary>One mark on the timeline strip.</summary>
public sealed record TimelineItem(DateTimeOffset At, TimelineKind Kind, string Label, string Detail);

/// <summary>The strip the page draws: a window ending now, the marks inside it, and the deploy before it.</summary>
/// <param name="DeployBefore">The newest deploy older than the window — the caption "5d27ea0 · 4 days ago".</param>
public sealed record TimelineWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<TimelineItem> Items,
    TimelineItem? DeployBefore)
{
    public TimeSpan Span => End - Start;

    /// <summary>Where <paramref name="at"/> falls across the strip, 0 at the left edge and 1 at now.</summary>
    public double Position(DateTimeOffset at) =>
        Span.Ticks <= 0 ? 1 : Math.Clamp((at - Start) / Span, 0, 1);
}

/// <summary>
/// The timeline on the application page, from what the cluster still holds — and only that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three sources, each with its own honest limit.</b> Deploys come from Argo CD's
/// <c>status.history</c> (<c>deployedAt</c> and the revision) and from each ReplicaSet's
/// creation with its <c>deployment.kubernetes.io/revision</c>; a rollout restart creates a
/// ReplicaSet and no Argo history, so both are drawn. Terminations come from the containers'
/// <c>state.terminated</c> and <c>lastState.terminated</c> — which is one termination per
/// container, not a restart history: a container that restarted fourteen times has one
/// dated termination, and drawing fourteen marks would be inventing thirteen. Warning Events
/// come from the page's own read of Events, and Events live about an hour by default.
/// </para>
/// <para>
/// <b>The window adapts</b> to 15, 30, 45 or 60 minutes: the smallest that holds every mark
/// from the last hour, so a burst of recent events is spread across the strip instead of
/// piling up at its right edge, and an hour-old deploy is still inside it.
/// </para>
/// </remarks>
public static class ApplicationTimeline
{
    public static readonly TimeSpan[] Windows =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(60)];

    public static IReadOnlyList<TimelineItem> Collect(ApplicationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = new List<TimelineItem>();

        if (input.Argo is { } argo)
        {
            var chart = ApplicationRules.IsChartSource(argo);
            foreach (var entry in argo.History)
            {
                if (entry.DeployedAt is { } at)
                {
                    items.Add(new TimelineItem(at, TimelineKind.Deploy, ApplicationRules.RevisionLabel(entry.Revision, chart),
                        $"Argo CD sync of {entry.Revision} (history id {entry.Id})"));
                }
            }
        }

        foreach (var rs in input.ReplicaSets)
        {
            if (rs.CreationTimestamp is { } at)
            {
                items.Add(new TimelineItem(at, TimelineKind.Deploy, $"rev {ApplicationRules.Revision(rs)}",
                    $"ReplicaSet {rs.Name} created (revision {ApplicationRules.Revision(rs)})"));
            }
        }

        foreach (var pod in input.Pods.Select(PodFacts.Read))
        {
            foreach (var container in pod.Containers.Concat(pod.InitContainers))
            {
                foreach (var ended in new[] { container.Terminated, container.LastTerminated })
                {
                    if (ended?.FinishedAt is { } at && !(container.IsInit && ended.ExitCode == 0))
                    {
                        items.Add(new TimelineItem(at, TimelineKind.Termination, $"{container.Name} {ended.ExitText}",
                            $"{pod.Name}: container {container.Name} exited with {ended.ExitText}"));
                    }
                }
            }
        }

        foreach (var e in input.Events.Where(e => e.Type() == "Warning"))
        {
            if (e.LastSeen() is { } at)
            {
                var count = e.Occurrences();
                items.Add(new TimelineItem(at, TimelineKind.WarningEvent,
                    count > 1 ? $"{e.Reason()} ×{count}" : e.Reason(),
                    $"{e.ObjectText()}: {e.Message()}"));
            }
        }

        return [.. items.DistinctBy(i => (i.At, i.Kind, i.Label)).OrderBy(i => i.At)];
    }

    public static TimelineWindow Build(IReadOnlyList<TimelineItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);

        var recent = items.Where(i => i.At <= now && now - i.At <= Windows[^1]).ToList();
        var span = Windows[0];
        if (recent.Count > 0)
        {
            var oldest = now - recent.Min(i => i.At);
            span = Windows.FirstOrDefault(w => w >= oldest, Windows[^1]);
        }

        var start = now - span;
        var inside = items.Where(i => i.At >= start && i.At <= now).ToList();
        var before = items
            .Where(i => i.Kind == TimelineKind.Deploy && i.At < start)
            .OrderByDescending(i => i.At)
            .FirstOrDefault();
        return new TimelineWindow(start, now, inside, before);
    }
}
