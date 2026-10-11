namespace KubeNimbus.Core;

/// <summary>One cluster taking part in an aggregated (fleet-wide) view.</summary>
/// <param name="Environment">
/// The environment that member's tab is treated as (the user's assignment, else the name
/// guess). Carried here because a fleet row's mutating action has to be gated and coloured by
/// its <em>own</em> cluster's environment, not by the tab that happens to be showing it.
/// </param>
public sealed record FleetMember(
    string ClusterName, ClusterClient Client, ClusterEnvironment Environment = ClusterEnvironment.Unknown);

/// <summary>
/// A member paired with the descriptor <em>that member's own</em> discovery
/// reported for the requested kind.
/// </summary>
/// <remarks>
/// The descriptor is resolved per cluster rather than reused from whichever tab
/// asked: two clusters can serve the same CRD kind at different versions
/// (<c>v1beta1</c> here, <c>v1</c> there), and reusing one cluster's descriptor
/// against another would silently query a path that cluster doesn't have.
/// </remarks>
public sealed record FleetTarget(FleetMember Member, ResourceDescriptor Descriptor);

/// <summary>One Application in a fleet read, with the cluster it is on and that cluster's own descriptor for the kind.</summary>
public sealed record ArgoFleetApplication(string ClusterName, ResourceDescriptor Descriptor, ArgoApplication Application);

/// <summary>
/// What a fleet read of Argo CD Applications found: the Applications, how many members serve
/// the kind out of how many were asked, and the members that could not be read.
/// </summary>
public sealed record ArgoFleetRead(
    IReadOnlyList<ArgoFleetApplication> Applications,
    int Serving,
    int Members,
    IReadOnlyList<(string Cluster, Exception Error)> Failed)
{
    /// <summary>
    /// The seven counts summed across the fleet (owner decision, 2026-10-10, #195): an
    /// Application is one Application whichever cluster it is on, so the totals are the same
    /// numbers the per-cluster dashboard shows, added up.
    /// </summary>
    public ArgoSummary Counts => ArgoSummary.Of(Applications.Select(a => a.Application));

    /// <summary>"2 of 3 clusters run Argo CD" — stated, because partial is normal in a fleet.</summary>
    public string Summary => $"{Serving} of {Members} clusters run Argo CD";
}

/// <summary>An aggregated watch event, tagged with the cluster it came from.</summary>
public sealed record FleetResourceEvent(string ClusterName, ResourceEvent<DynamicResource> Event);

/// <summary>
/// Fans a single resource query out across several connected clusters and merges
/// the results — the engine side of the multi-cluster aggregated views.
/// </summary>
/// <remarks>
/// Deliberately stateless and UI-free (Core rule #1): it composes the existing
/// per-cluster <see cref="ClusterClient"/> streaming primitives instead of
/// introducing a second watch implementation. Everything about partial
/// availability is explicit — a kind absent from a cluster, or a cluster that
/// can't be reached, is reported to the caller rather than swallowed or thrown,
/// because "3 of 5 clusters" is the normal state of a real fleet and the UI has
/// to be able to say so.
/// </remarks>
public static class ClusterFleet
{
    /// <summary>
    /// Resolves which members actually serve <paramref name="kind"/> in
    /// <paramref name="group"/>, using each member's own discovery catalog (cached,
    /// so this is cheap after the first call per cluster).
    /// </summary>
    /// <param name="memberUnavailable">
    /// Called for a member whose catalog couldn't be read (unreachable, RBAC).
    /// Members that are reachable but simply don't have the kind are skipped
    /// silently — that's not a fault, just a different cluster.
    /// </param>
    public static async Task<IReadOnlyList<FleetTarget>> ResolveAsync(
        IReadOnlyList<FleetMember> members,
        string group,
        string kind,
        Action<FleetMember, Exception>? memberUnavailable = null,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<FleetTarget>(members.Count);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var catalog = await member.Client.GetResourceCatalogAsync(cancellationToken).ConfigureAwait(false);
                var descriptor = catalog.FirstOrDefault(d =>
                    string.Equals(d.Group, group, StringComparison.Ordinal)
                    && string.Equals(d.Kind, kind, StringComparison.Ordinal));
                if (descriptor is not null)
                {
                    targets.Add(new FleetTarget(member, descriptor));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                memberUnavailable?.Invoke(member, ex);
            }
        }

        return targets;
    }

    /// <summary>
    /// Every Argo CD Application on every member that serves the kind, read once and in
    /// parallel — the fleet half of the Argo dashboard, which is a read rather than a watch on
    /// one cluster too. Each member resolves its own descriptor (<see cref="ResolveAsync"/>),
    /// and a member that cannot be read is reported in <see cref="ArgoFleetRead.Failed"/> while
    /// the rest are returned: partial is the normal state of a fleet, and the caller says so.
    /// </summary>
    public static async Task<ArgoFleetRead> ListArgoApplicationsAsync(
        IReadOnlyList<FleetMember> members, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);

        var failed = new List<(string Cluster, Exception Error)>();
        var targets = await ResolveAsync(
            members, ArgoCd.Group, "Application",
            (member, ex) => { lock (failed) failed.Add((member.ClusterName, ex)); },
            cancellationToken).ConfigureAwait(false);

        var reads = await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                var applications = await target.Member.Client
                    .ListArgoApplicationsAsync(target.Descriptor, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return applications.Select(a => new ArgoFleetApplication(target.Member.ClusterName, target.Descriptor, a)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (failed)
                {
                    failed.Add((target.Member.ClusterName, ex));
                }

                return [];
            }
        })).ConfigureAwait(false);

        return new ArgoFleetRead([.. reads.SelectMany(r => r)], targets.Count, members.Count, failed);
    }

    /// <summary>
    /// Runs one list+watch per target and interleaves them. Each event carries its
    /// cluster name, and a <see cref="ResourceEventType.Reset"/> applies to that
    /// cluster alone — consumers must clear only that cluster's rows, never the
    /// whole aggregated list, or one cluster's reconnect wipes the others.
    /// </summary>
    public static IAsyncEnumerable<FleetResourceEvent> WatchAsync(
        IReadOnlyList<FleetTarget> targets,
        string? @namespace = null,
        Action<FleetMember, Exception>? connectionLost = null,
        CancellationToken cancellationToken = default) =>
        AsyncMerge.Merge(
            targets.Select(target => TagAsync(target, @namespace, connectionLost, cancellationToken)),
            cancellationToken: cancellationToken);

    private static async IAsyncEnumerable<FleetResourceEvent> TagAsync(
        FleetTarget target,
        string? @namespace,
        Action<FleetMember, Exception>? connectionLost,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = target.Member.Client.WatchResourceAsync(
            target.Descriptor,
            target.Descriptor.Namespaced ? @namespace : null,
            connectionLost: ex => connectionLost?.Invoke(target.Member, ex),
            cancellationToken: cancellationToken);

        // Enumerated by hand rather than with await-foreach because a yield can't sit
        // inside a try/catch: this loop has to attribute a terminal stream failure to
        // *this* member (and then quietly stop contributing) instead of letting it
        // surface as an untagged exception out of the merged stream.
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ResourceEvent<DynamicResource> evt;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                evt = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                connectionLost?.Invoke(target.Member, ex);
                break;
            }

            yield return new FleetResourceEvent(target.Member.ClusterName, evt);
        }
    }
}
