namespace KubeNimbus.Core;

/// <summary>
/// The whole cluster's capacity at a glance (FEAT-25, #206): how many nodes and how many of
/// them are Ready, CPU and memory measured by <c>metrics.k8s.io</c> against allocatable, and
/// the scheduler's requests against allocatable. The strip above the Nodes list reads it.
/// </summary>
/// <remarks>
/// <para>
/// Each of the three inputs can be missing on its own, and a missing one is missing, never a
/// zero: no metrics API (or no permission to read it) leaves <see cref="ClusterResourceTotals.Used"/>
/// null, a pod list the reader may not make leaves <see cref="ClusterResourceTotals.Requested"/>
/// null, and a node that reported no allocatable is left out of the denominator and counted as
/// such. This is the competitive point of the item: the Lens lineage's overview is blank without
/// Prometheus, and this one degrades to whatever the cluster lets it read.
/// </para>
/// <para>
/// The arithmetic is the node pane's (<see cref="NodeResources"/>), summed: allocatable rather
/// than capacity, and requests by the scheduler's own formula over the pods bound to a node,
/// terminal pods excluded. A pod not yet bound to any node is not on any node's books and is
/// not counted — <c>kubectl describe node</c> would not count it either.
/// </para>
/// </remarks>
public static class ClusterCapacity
{
    /// <param name="nodes">Every node, as the Nodes list holds them.</param>
    /// <param name="usage">
    /// The latest measured usage of a node by name (nanocores, bytes), null for a node with no
    /// sample; the whole delegate null when usage cannot be read at all.
    /// </param>
    /// <param name="pods">Every pod in the cluster, or null when they could not be read.</param>
    public static ClusterCapacitySummary Summarize(
        IReadOnlyCollection<DynamicResource> nodes,
        Func<string, (long? CpuNanocores, long? MemoryBytes)?>? usage,
        IReadOnlyCollection<DynamicResource>? pods)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var ready = 0;
        var cpu = new Accumulator();
        var memory = new Accumulator();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            names.Add(node.Name);
            if (NodeResources.Conditions(node).Any(c => c.Type == "Ready" && c.Status == "True"))
            {
                ready++;
            }

            var summary = NodeResources.Summarize(node, []);
            var sample = usage?.Invoke(node.Name);
            cpu.AddNode(summary.Cpu.Allocatable, sample?.CpuNanocores is { } n ? n / 1e9 : null);
            memory.AddNode(summary.Memory.Allocatable, sample?.MemoryBytes);
        }

        if (pods is not null)
        {
            cpu.Requested = 0;
            memory.Requested = 0;
            foreach (var pod in pods)
            {
                var spec = NodeActions.Object(pod.Raw, "spec");
                var phase = NodeActions.Str(NodeActions.Object(pod.Raw, "status"), "phase");
                if (phase is "Succeeded" or "Failed" || !names.Contains(NodeActions.Str(spec, "nodeName")))
                {
                    continue;
                }

                cpu.Requested += NodeResources.EffectiveRequest(spec, NodeResources.Cpu, "requests");
                memory.Requested += NodeResources.EffectiveRequest(spec, NodeResources.Memory, "requests");
            }
        }

        return new ClusterCapacitySummary(nodes.Count, ready, cpu.Totals(usage is not null), memory.Totals(usage is not null));
    }

    private sealed class Accumulator
    {
        private double _allocatable;
        private int _allocatableFrom;
        private double _used;
        private double _usedAllocatable;
        private int _usedFrom;

        public double? Requested { get; set; }

        public void AddNode(double? allocatable, double? used)
        {
            if (allocatable is not { } a)
            {
                return;
            }

            _allocatable += a;
            _allocatableFrom++;

            // Usage is compared with the allocatable of the nodes that reported it, so a node
            // metrics-server has not scraped yet does not make the cluster look emptier.
            if (used is { } u)
            {
                _used += u;
                _usedAllocatable += a;
                _usedFrom++;
            }
        }

        public ClusterResourceTotals Totals(bool usageReadable) => new(
            _allocatableFrom > 0 ? _allocatable : null,
            _allocatableFrom,
            usageReadable && _usedFrom > 0 ? _used : null,
            usageReadable && _usedFrom > 0 ? _usedAllocatable : null,
            usageReadable ? _usedFrom : 0,
            Requested);
    }
}

/// <summary>The cluster's capacity, as <see cref="ClusterCapacity.Summarize"/> read it.</summary>
public sealed record ClusterCapacitySummary(int Nodes, int Ready, ClusterResourceTotals Cpu, ClusterResourceTotals Memory)
{
    /// <summary>Nodes whose Ready condition is not True: False, Unknown, or not reported.</summary>
    public int NotReady => Nodes - Ready;
}

/// <summary>
/// One resource summed over the cluster, in base units (cores, bytes). Every figure is null
/// when it could not be read; none is ever a stand-in zero.
/// </summary>
/// <param name="Allocatable">Summed over the nodes that reported allocatable.</param>
/// <param name="AllocatableFrom">How many nodes reported allocatable.</param>
/// <param name="Used">Measured usage summed over the nodes that have a sample.</param>
/// <param name="UsedAllocatable">The allocatable of those same nodes — the usage's denominator.</param>
/// <param name="UsedFrom">How many nodes have a sample.</param>
/// <param name="Requested">Requests of the pods bound to these nodes; null when the pods were not read.</param>
public sealed record ClusterResourceTotals(
    double? Allocatable, int AllocatableFrom, double? Used, double? UsedAllocatable, int UsedFrom, double? Requested)
{
    /// <summary>Usage as a percentage of the reporting nodes' allocatable.</summary>
    public double? UsedPercent => Percent(Used, UsedAllocatable);

    /// <summary>Requests as a percentage of allocatable. Requests past allocatable are possible and kept.</summary>
    public double? RequestedPercent => Percent(Requested, Allocatable);

    private static double? Percent(double? part, double? whole) =>
        part is { } p && whole is { } w && w > 0 ? p * 100d / w : null;
}
