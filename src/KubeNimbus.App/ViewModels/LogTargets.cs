using System.Net;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One thing whose logs can be opened: a pod (its detail pane, on the Logs tab) or an
/// object that names the pods it owns (the one-stream multi-pod pane). The single
/// currency of every "open logs" gesture — the list's L key and its menu, the palette's
/// rows — so that whatever route reached it, <see cref="ClusterTabViewModel.OpenLogsForAsync"/>
/// makes the same decision about which pane opens and which tab it reuses.
/// </summary>
/// <param name="Resource">The object itself, as a list or watch returned it.</param>
/// <param name="Descriptor">Its kind, as the cluster that served it reported it.</param>
/// <param name="ClusterName">The fleet member it came from; empty for the tab's own
/// cluster. Part of every inspector-tab key, so it has to match what the list uses or
/// an already-open pane is not found and a second one opens beside it.</param>
/// <param name="Client">The client of the cluster it came from, null on the demo
/// cluster. Carried rather than looked up because a palette row can name a fleet member
/// that serves nothing the list is currently showing.</param>
public sealed record LogTarget(
    DynamicResource Resource,
    ResourceDescriptor Descriptor,
    string ClusterName,
    ClusterClient? Client)
{
    /// <summary>A pod opens its own detail pane; anything else aggregates the pods it owns.</summary>
    public bool IsPod => Descriptor is { Kind: "Pod", Group: "" };
}

/// <summary>
/// One cluster the palette's log rows are listed from — the tab's own, or one member of
/// a fleet. The two list operations are delegates rather than a <see cref="ClusterClient"/>
/// so the demo cluster (no client at all) and the tests (a stand-in that answers slowly or
/// with a 403) go through exactly the code a real cluster does.
/// </summary>
/// <param name="ClusterName">Empty for the tab's own cluster; the member's name in fleet mode.</param>
/// <param name="Client">Handed on to each <see cref="LogTarget"/>; null on the demo cluster.</param>
/// <param name="Catalog">The cluster's discovery catalog, for the workload kinds' descriptors.</param>
/// <param name="List">A capped one-shot list of one kind in one namespace (null = every namespace).</param>
/// <param name="KnownPods">The pods the tab's own list already holds, when it is showing
/// Pods in this scope — then no pod list is issued at all. Null when it is not.</param>
public sealed record LogTargetSource(
    string ClusterName,
    ClusterClient? Client,
    Func<CancellationToken, Task<IReadOnlyList<ResourceDescriptor>>> Catalog,
    Func<ResourceDescriptor, string?, int, CancellationToken, Task<CappedResourceList>> List,
    IReadOnlyList<DynamicResource>? KnownPods = null)
{
    /// <summary>A source backed by a real cluster.</summary>
    public static LogTargetSource For(string clusterName, ClusterClient client, IReadOnlyList<DynamicResource>? knownPods = null) =>
        new(
            clusterName,
            client,
            ct => client.GetResourceCatalogAsync(ct),
            client.ListResourceCappedAsync,
            knownPods);

    /// <summary>
    /// The demo cluster: the shipped dataset through the same filter the demo list uses.
    /// Every task it returns is already complete, which is what lets the tab apply the
    /// result inline with no dispatcher round trip.
    /// </summary>
    public static LogTargetSource Demo(IReadOnlyList<DynamicResource>? knownPods = null) =>
        new(
            "",
            null,
            _ => Task.FromResult(KubeNimbus.App.Demo.DemoData.BuildCatalog()),
            (descriptor, @namespace, cap, _) =>
            {
                var all = KubeNimbus.App.Demo.DemoData.ResourcesFor(descriptor, @namespace);
                return Task.FromResult(new CappedResourceList([.. all.Take(cap)], all.Count > cap));
            },
            knownPods);
}

/// <summary>
/// What one look at the scope found: the targets, and anything that stopped part of it
/// from being complete, each already worded for a reader.
/// </summary>
/// <param name="Targets">Workloads first, then pods; each group ordered by name.</param>
/// <param name="Problems">A failed or refused list, one each. Never silent: a palette
/// that finds nothing because it was not allowed to look has to say so.</param>
/// <param name="Truncations">A list that hit the cap, one sentence each.</param>
public sealed record LogTargetList(
    IReadOnlyList<LogTarget> Targets,
    IReadOnlyList<LogTargetProblem> Problems,
    IReadOnlyList<string> Truncations)
{
    public static readonly LogTargetList Empty = new([], [], []);
}

/// <summary>A list that failed: what could not be listed, and the server's own reason.</summary>
/// <param name="Summary">"not allowed to list pods in payments", prefixed with the cluster in a fleet.</param>
/// <param name="Detail">The API server's message, which names the verb and the subject.</param>
/// <param name="IsForbidden">A 403 — permission, not an error.</param>
public sealed record LogTargetProblem(string Summary, string Detail, bool IsForbidden);

/// <summary>
/// Lists every pod, Deployment, StatefulSet and DaemonSet in a scope — once, not as a
/// watch — for the palette's "Logs: …" rows. No UI types and no dispatcher, so it runs
/// on a pool thread and is tested directly.
/// </summary>
public static class LogTargetLoader
{
    /// <summary>
    /// The most objects of one kind listed from one cluster. Two thousand is past what a
    /// namespace normally holds and past anything a person scrolls; the palette shows
    /// fifty at a time and narrows as you type, so what the cap bounds is the request and
    /// the memory, not what can be found. Hitting it is stated, never silent.
    /// </summary>
    public const int MaxPerKind = 2000;

    /// <summary>
    /// The workload kinds offered, and only these: the controllers someone means by "the
    /// logs of checkout". A ReplicaSet or a Job would also qualify on selector evidence,
    /// and the list's own L key still reaches them — here they would be a second and third
    /// row for the same pods under a Deployment that already has one.
    /// </summary>
    public static readonly IReadOnlyList<string> WorkloadKinds = ["Deployment", "StatefulSet", "DaemonSet"];

    public static async Task<LogTargetList> LoadAsync(
        IReadOnlyList<LogTargetSource> sources,
        string? @namespace,
        int maxPerKind = MaxPerKind,
        CancellationToken cancellationToken = default)
    {
        var perSource = await Task.WhenAll(
            sources.Select(s => LoadSourceAsync(s, @namespace, maxPerKind, cancellationToken))).ConfigureAwait(false);

        var workloads = new List<LogTarget>();
        var pods = new List<LogTarget>();
        var problems = new List<LogTargetProblem>();
        var truncations = new List<string>();
        foreach (var result in perSource)
        {
            foreach (var target in result.Targets)
            {
                (target.IsPod ? pods : workloads).Add(target);
            }

            problems.AddRange(result.Problems);
            truncations.AddRange(result.Truncations);
        }

        return new LogTargetList([.. Ordered(workloads), .. Ordered(pods)], problems, truncations);
    }

    private static IEnumerable<LogTarget> Ordered(IEnumerable<LogTarget> targets) =>
        targets
            .OrderBy(t => t.Resource.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Resource.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.ClusterName, StringComparer.Ordinal);

    private static async Task<LogTargetList> LoadSourceAsync(
        LogTargetSource source, string? @namespace, int cap, CancellationToken ct)
    {
        var where = Scope(@namespace);
        var prefix = source.ClusterName.Length > 0 ? $"{source.ClusterName}: " : "";

        // Discovery first, for the workload kinds' descriptors. A cluster whose catalog
        // cannot be read still gets its pods listed through the well-known descriptor —
        // that is the half somebody opening the palette for logs most wants.
        IReadOnlyList<ResourceDescriptor> catalog;
        try
        {
            catalog = await source.Catalog(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalog = [];
        }

        var podDescriptor = catalog.FirstOrDefault(d => d is { Group: "", Kind: "Pod" }) ?? ResourceDescriptor.Pods;
        var workloadDescriptors = WorkloadKinds
            .Select(kind => catalog.FirstOrDefault(d => d.Group == "apps" && d.Kind == kind))
            .OfType<ResourceDescriptor>()
            .ToList();

        var podTask = source.KnownPods is { } known
            ? Task.FromResult(new CappedResourceList([.. known.Take(cap)], known.Count > cap))
            : source.List(podDescriptor, @namespace, cap, ct);
        var workloadTasks = workloadDescriptors
            .Select(d => source.List(d, @namespace, cap, ct))
            .ToList();

        var targets = new List<LogTarget>();
        var problems = new List<LogTargetProblem>();
        var truncations = new List<string>();

        try
        {
            var pods = await podTask.ConfigureAwait(false);
            targets.AddRange(pods.Items.Select(p => new LogTarget(p, podDescriptor, source.ClusterName, source.Client)));
            if (pods.IsTruncated)
            {
                truncations.Add($"{prefix}only the first {cap:N0} pods in {where} are listed");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add(Explain(ex, prefix, "pods", where));
        }

        // The workload kinds share one sentence: a user who may not list Deployments here
        // almost certainly may not list StatefulSets either, and three rows saying so is
        // three rows of the same news. It names the kinds that failed rather than saying
        // "workloads", because a Role that grants Deployments alone is common and the
        // Deployments it did list are right there under the note.
        var failedKinds = new List<string>();
        Exception? firstWorkloadFailure = null;
        for (var i = 0; i < workloadTasks.Count; i++)
        {
            try
            {
                var listed = await workloadTasks[i].ConfigureAwait(false);
                foreach (var workload in listed.Items)
                {
                    // Offered on the same evidence the list's own "Logs (all pods)" is:
                    // an object that names its pods. An empty selector names nothing and
                    // is refused rather than read as "every pod" (LabelSelector.ForPodsOf).
                    if (LabelSelector.ForPodsOf(workload) is not null)
                    {
                        targets.Add(new LogTarget(workload, workloadDescriptors[i], source.ClusterName, source.Client));
                    }
                }

                if (listed.IsTruncated)
                {
                    truncations.Add($"{prefix}only the first {cap:N0} {workloadDescriptors[i].Plural} in {where} are listed");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedKinds.Add(workloadDescriptors[i].Plural);
                firstWorkloadFailure ??= ex;
            }
        }

        if (firstWorkloadFailure is not null)
        {
            problems.Add(Explain(firstWorkloadFailure, prefix, JoinKinds(failedKinds), where));
        }

        return new LogTargetList(targets, problems, truncations);
    }

    /// <summary>"deployments", "deployments and daemonsets", "deployments, statefulsets and daemonsets".</summary>
    private static string JoinKinds(IReadOnlyList<string> kinds) => kinds.Count switch
    {
        1 => kinds[0],
        _ => $"{string.Join(", ", kinds.Take(kinds.Count - 1))} and {kinds[^1]}",
    };

    /// <summary>"payments", or "every namespace" for the all-namespaces scope.</summary>
    public static string Scope(string? @namespace) =>
        string.IsNullOrEmpty(@namespace) ? "every namespace" : @namespace;

    /// <summary>
    /// One sentence for a failed list. A 403 is named as a permission rather than as an
    /// error, because it is the one a user can do something about (ask for the right, or
    /// pick a namespace they have it in) and the server's own message names the verb and
    /// the subject.
    /// </summary>
    private static LogTargetProblem Explain(Exception ex, string prefix, string what, string where)
    {
        var detail = ex is KubernetesApiException { ServerMessage: { Length: > 0 } server } ? server : ex.Message;
        var forbidden = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden };
        return new LogTargetProblem(
            forbidden ? $"{prefix}not allowed to list {what} in {where}" : $"{prefix}couldn't list {what} in {where}",
            detail,
            forbidden);
    }
}
