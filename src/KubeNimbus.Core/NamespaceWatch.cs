namespace KubeNimbus.Core;

/// <summary>A watch event tagged with the namespace whose watch delivered it.</summary>
public sealed record NamespacedResourceEvent(string Namespace, ResourceEvent<DynamicResource> Event);

/// <summary>
/// One list+watch per namespace, merged into a single stream — what the Resources list runs
/// when several namespaces are chosen. The fleet views' shape (<see cref="ClusterFleet"/>) with
/// the namespace in the cluster's place.
/// </summary>
/// <remarks>
/// <para>
/// One watch per namespace rather than one across the cluster filtered down, because narrow
/// RBAC is the expected case: a user granted three namespaces and not the cluster can watch
/// each of the three, and a cluster-wide watch would be refused outright.
/// </para>
/// <para>
/// Each event carries its namespace so the consumer can scope a <c>Reset</c> to it: one
/// namespace relisting after a 410 must not blank the others. A namespace whose stream ends
/// in a failure reports it through <c>sourceFailed</c> and then emits a <c>Synced</c> of its
/// own, so a list waiting for every namespace to answer (UI rule 18) is not left waiting for
/// one that never will.
/// </para>
/// </remarks>
public static class NamespaceWatch
{
    public static IAsyncEnumerable<NamespacedResourceEvent> WatchAsync(
        ClusterClient client,
        ResourceDescriptor descriptor,
        IReadOnlyList<string> namespaces,
        Action<string, Exception>? connectionLost = null,
        Action<string, Exception>? sourceFailed = null,
        CancellationToken cancellationToken = default) =>
        AsyncMerge.Merge(
            namespaces.Select(ns => TagAsync(
                client.WatchResourceAsync(
                    descriptor, ns,
                    connectionLost: ex => connectionLost?.Invoke(ns, ex),
                    cancellationToken: cancellationToken),
                ns, sourceFailed, cancellationToken)),
            cancellationToken: cancellationToken);

    /// <summary>Tags a stream's events with its namespace. Internal so the tests can drive it with a stand-in stream.</summary>
    internal static async IAsyncEnumerable<NamespacedResourceEvent> TagAsync(
        IAsyncEnumerable<ResourceEvent<DynamicResource>> stream,
        string @namespace,
        Action<string, Exception>? sourceFailed,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // By hand rather than with await-foreach: a yield cannot sit inside a try/catch, and a
        // failure has to be attributed to this namespace instead of ending the merged stream.
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        var failed = false;
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
                sourceFailed?.Invoke(@namespace, ex);
                failed = true;
                break;
            }

            yield return new NamespacedResourceEvent(@namespace, evt);
        }

        if (failed)
        {
            yield return new NamespacedResourceEvent(@namespace, ResourceEvent<DynamicResource>.Synced);
        }
    }
}
