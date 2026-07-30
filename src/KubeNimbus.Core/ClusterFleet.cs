namespace KubeNimbus.Core;

/// <summary>One cluster taking part in an aggregated (fleet-wide) view.</summary>
public sealed record FleetMember(string ClusterName, ClusterClient Client);

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
