namespace KubeNimbus.Core.Applications;

/// <summary>Everything the Applications list was built from, at one moment.</summary>
public sealed record ClusterSnapshot(
    IReadOnlyList<ArgoApplication> ArgoApplications,
    IReadOnlyList<DynamicResource> Workloads,
    IReadOnlyList<DynamicResource> ReplicaSets,
    IReadOnlyList<DynamicResource> Jobs,
    IReadOnlyList<DynamicResource> Pods,
    DateTimeOffset Now)
{
    public static ClusterSnapshot Empty(DateTimeOffset now) => new([], [], [], [], [], now);
}

/// <summary>One row of the Applications list, before it is assessed.</summary>
/// <param name="Key">Stable identity: <c>argo:&lt;ns&gt;/&lt;name&gt;</c> or <c>&lt;Kind&gt;:&lt;ns&gt;/&lt;name&gt;</c>.</param>
public sealed record ApplicationEntry(
    string Key,
    string Name,
    string Namespace,
    ArgoApplication? Argo,
    ApplicationInput Input)
{
    public bool IsArgo => Argo is not null;

    /// <summary>"Argo CD", or the workload's kind when no Application tracks it.</summary>
    public string Source => Argo is not null ? "Argo CD" : Input.Workloads.FirstOrDefault()?.Kind ?? "";

    /// <summary>Every namespace the app's workloads (or its Argo destination) live in, first the main one.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];
}

/// <summary>
/// Turns what the cluster holds into the list's rows: one per Argo CD Application, and one
/// per workload no Application tracks.
/// </summary>
/// <remarks>
/// <para>
/// <b>An Argo Application claims a workload by its own status first.</b> Argo lists every
/// object it manages in <c>status.resources</c> by group, kind, namespace and name, so that is
/// read before anything else. Only a workload it does not list falls back to Argo's two
/// tracking marks — the <c>app.kubernetes.io/instance</c> label (Argo's default tracking
/// method) and the <c>argocd.argoproj.io/tracking-id</c> annotation (its annotation method),
/// whose value starts with the Application's name. The fallback covers an Application whose
/// status has not been written yet; it is not a guess across apps.
/// </para>
/// <para>
/// <b>A tracked workload does not get a row of its own</b> — two rows for one thing would
/// double every problem the list is meant to point at.
/// </para>
/// <para>
/// <b>Which workloads are applications.</b> Deployments, StatefulSets, DaemonSets and
/// CronJobs always; a Job only when nothing owns it and it is running or has failed, because
/// a finished standalone Job is history rather than something that is up or down, and a
/// CronJob's Jobs belong to the CronJob. ReplicaSets and pods are never rows: they are what
/// the rows are made of.
/// </para>
/// </remarks>
public static class ApplicationCatalog
{
    public const string InstanceLabel = "app.kubernetes.io/instance";
    public const string TrackingIdAnnotation = "argocd.argoproj.io/tracking-id";

    public static readonly IReadOnlySet<string> WorkloadKinds =
        new HashSet<string>(StringComparer.Ordinal) { "Deployment", "StatefulSet", "DaemonSet", "CronJob", "Job" };

    public static IReadOnlyList<ApplicationEntry> Build(ClusterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var index = new SnapshotIndex(snapshot);
        var candidates = snapshot.Workloads.Where(IsApplicationWorkload).ToList();
        var claimed = new HashSet<DynamicResource>(ReferenceEqualityComparer.Instance);
        var byArgo = new List<(ArgoApplication App, List<DynamicResource> Workloads)>();

        // Pass 1: status.resources, which is Argo's own statement of what it manages.
        foreach (var app in snapshot.ArgoApplications)
        {
            var listed = app.Resources
                .Select(r => (r.Group, r.Kind, r.Namespace, r.Name))
                .ToHashSet();
            var mine = candidates.Where(w => listed.Contains((GroupOf(w), w.Kind, w.Namespace ?? "", w.Name))).ToList();
            foreach (var w in mine)
            {
                claimed.Add(w);
            }

            byArgo.Add((app, mine));
        }

        // Pass 2: Argo's tracking marks, for what no status listed.
        foreach (var (app, mine) in byArgo)
        {
            foreach (var w in candidates.Where(w => !claimed.Contains(w) && IsTrackedBy(w, app)))
            {
                claimed.Add(w);
                mine.Add(w);
            }
        }

        var result = new List<ApplicationEntry>();
        foreach (var (app, mine) in byArgo)
        {
            var namespaces = mine.Select(w => w.Namespace ?? "")
                .Prepend(app.DestinationNamespace)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            result.Add(new ApplicationEntry(
                $"argo:{app.Namespace}/{app.Name}",
                app.Name,
                namespaces.FirstOrDefault() ?? "",
                app,
                InputFor(app, mine, index))
            {
                Namespaces = namespaces,
            });
        }

        foreach (var workload in candidates.Where(w => !claimed.Contains(w)))
        {
            result.Add(new ApplicationEntry(
                $"{workload.Kind}:{workload.Namespace}/{workload.Name}",
                workload.Name,
                workload.Namespace ?? "",
                null,
                InputFor(null, [workload], index))
            {
                Namespaces = [workload.Namespace ?? ""],
            });
        }

        return result;
    }

    /// <summary>The objects the rules need for one app, cut out of the snapshot.</summary>
    public static ApplicationInput InputFor(
        ArgoApplication? app, IReadOnlyList<DynamicResource> workloads, ClusterSnapshot snapshot,
        IReadOnlyList<DynamicResource>? events = null) =>
        InputFor(app, workloads, new SnapshotIndex(snapshot), events);

    private static ApplicationInput InputFor(
        ArgoApplication? app, IReadOnlyList<DynamicResource> workloads, SnapshotIndex index,
        IReadOnlyList<DynamicResource>? events = null)
    {
        var replicaSets = workloads.Where(w => w.Kind == "Deployment").SelectMany(index.OwnedReplicaSets).ToList();
        var jobs = workloads.Where(w => w.Kind == "CronJob").SelectMany(index.OwnedJobs).ToList();
        var jobNames = jobs.Select(j => (j.Namespace, j.Name)).ToHashSet();
        var pods = new List<DynamicResource>();
        var seen = new HashSet<DynamicResource>(ReferenceEqualityComparer.Instance);
        foreach (var workload in workloads)
        {
            var inNamespace = index.PodsIn(workload.Namespace);
            if (workload.Kind == "CronJob")
            {
                pods.AddRange(inNamespace.Where(p => seen.Add(p)
                    && p.OwnerReferences.Any(o => o.Kind == "Job" && jobNames.Contains((p.Namespace, o.Name)))));
            }
            else if (LabelSelector.ForPodsOf(workload) is { } selector)
            {
                pods.AddRange(inNamespace.Where(p => selector.Matches(p.Labels) && seen.Add(p)));
            }
        }

        return new ApplicationInput(app, workloads, replicaSets, jobs, pods, events ?? [], index.Snapshot.Now);
    }

    /// <summary>
    /// Pods by namespace and ReplicaSets/Jobs by owner, built once per list rebuild, so a
    /// cluster of thousands of pods is not scanned once per application.
    /// </summary>
    private sealed class SnapshotIndex(ClusterSnapshot snapshot)
    {
        private readonly ILookup<string, DynamicResource> _pods =
            snapshot.Pods.ToLookup(p => p.Namespace ?? "", StringComparer.Ordinal);

        private readonly ILookup<(string?, string, string), DynamicResource> _replicaSets = ByOwner(snapshot.ReplicaSets);

        private readonly ILookup<(string?, string, string), DynamicResource> _jobs = ByOwner(snapshot.Jobs);

        public ClusterSnapshot Snapshot { get; } = snapshot;

        public IEnumerable<DynamicResource> PodsIn(string? @namespace) => _pods[@namespace ?? ""];

        public IEnumerable<DynamicResource> OwnedReplicaSets(DynamicResource owner) =>
            _replicaSets[(owner.Namespace, owner.Kind, owner.Name)].Where(r => ApplicationRules.IsOwnedBy(r, owner));

        public IEnumerable<DynamicResource> OwnedJobs(DynamicResource owner) =>
            _jobs[(owner.Namespace, owner.Kind, owner.Name)].Where(j => ApplicationRules.IsOwnedBy(j, owner));

        private static ILookup<(string?, string, string), DynamicResource> ByOwner(IEnumerable<DynamicResource> items) =>
            items.SelectMany(i => i.OwnerReferences.Select(o => (Key: (i.Namespace, o.Kind, o.Name), Item: i)))
                .ToLookup(x => x.Key, x => x.Item);
    }

    public static bool IsApplicationWorkload(DynamicResource workload)
    {
        if (!WorkloadKinds.Contains(workload.Kind))
        {
            return false;
        }

        if (workload.Kind != "Job")
        {
            return true;
        }

        if (workload.OwnerReferences.Count > 0)
        {
            return false;
        }

        var status = J.Obj(workload.Raw, "status");
        return (J.Int(status, "active") ?? 0) > 0 || J.Str(J.Condition(status, "Failed"), "status") == "True";
    }

    /// <summary>
    /// Whether <paramref name="workload"/> carries one of Argo's tracking marks for
    /// <paramref name="app"/>. The tracking id is <c>&lt;app&gt;:&lt;group&gt;/&lt;kind&gt;:&lt;ns&gt;/&lt;name&gt;</c>;
    /// only its first field is compared, and only up to the colon, so "checkout" never
    /// claims what "checkout-v2" tracks.
    /// </summary>
    public static bool IsTrackedBy(DynamicResource workload, ArgoApplication app)
    {
        if (workload.Annotations.TryGetValue(TrackingIdAnnotation, out var trackingId))
        {
            var colon = trackingId.IndexOf(':');
            var owner = colon < 0 ? trackingId : trackingId[..colon];
            return owner == app.Name || owner == $"{app.Namespace}_{app.Name}";
        }

        return workload.Labels.TryGetValue(InstanceLabel, out var instance)
            && (instance == app.Name || instance == $"{app.Namespace}_{app.Name}");
    }

    private static string GroupOf(DynamicResource resource)
    {
        var apiVersion = resource.ApiVersion;
        var slash = apiVersion.IndexOf('/');
        return slash < 0 ? "" : apiVersion[..slash];
    }
}
