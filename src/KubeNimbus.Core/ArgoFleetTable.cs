namespace KubeNimbus.Core;

/// <summary>
/// The Argo CD dashboard's fleet list as a live table (#336): one
/// <see cref="ArgoApplicationTable"/> per member cluster, folded from
/// <see cref="ClusterFleet.WatchAsync"/>'s tagged events. A Reset clears only its own cluster's
/// rows, so one member's relist never blanks the others, and every row keeps its own cluster's
/// descriptor, which is what a sync from that row builds its path from.
/// </summary>
/// <remarks>
/// A member whose watch ended (refused, or failed for good) is <see cref="MarkEnded"/>: it has
/// answered, so the fleet list stops waiting for it (UI rule 18), and its rows go, because a
/// list that no longer updates must not sit beside live ones looking current.
/// </remarks>
public sealed class ArgoFleetTable
{
    private readonly Dictionary<string, (FleetTarget Target, ArgoApplicationTable Table)> _clusters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);

    public ArgoFleetTable(IEnumerable<FleetTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
        {
            _clusters[target.Member.ClusterName] = (target, new ArgoApplicationTable());
        }
    }

    /// <summary>The members being watched, by cluster name.</summary>
    public IReadOnlyCollection<string> Clusters => _clusters.Keys;

    /// <summary>Every member's list has synced or ended: nothing more is coming for the first view.</summary>
    public bool HasEveryAnswer => _clusters.All(c => c.Value.Table.IsSynced || _ended.Contains(c.Key));

    /// <summary>What to show: every member's Applications, each with its cluster and that cluster's descriptor.</summary>
    public IReadOnlyList<ArgoFleetApplication> Applications =>
    [
        .. _clusters
            .Where(c => !_ended.Contains(c.Key))
            .SelectMany(c => c.Value.Table.Applications.Select(a => new ArgoFleetApplication(c.Key, c.Value.Target.Descriptor, a))),
    ];

    /// <summary>Folds one tagged event into its own cluster's table. An event from a cluster not watched is ignored.</summary>
    public void Apply(FleetResourceEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_clusters.TryGetValue(change.ClusterName, out var cluster) && !_ended.Contains(change.ClusterName))
        {
            cluster.Table.Apply(change.Event);
        }
    }

    /// <summary>The member's watch has ended for good: it has answered, and its rows leave the list.</summary>
    public void MarkEnded(string clusterName) => _ended.Add(clusterName);
}
