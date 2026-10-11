using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-25 (#206): the cluster's capacity summed from its nodes, its pods and its metrics.
/// The point of the item is degradation — a part that could not be read is null, never a
/// zero — so most of these pin what is left out as carefully as what is added up.
/// </summary>
public class ClusterCapacityTests
{
    private static DynamicResource Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static DynamicResource Node(string name, string ready, string? cpu = "4", string? memory = "8Gi")
    {
        var allocatable = string.Join(",", new[]
        {
            cpu is null ? null : $"\"cpu\":\"{cpu}\"",
            memory is null ? null : $"\"memory\":\"{memory}\"",
        }.Where(s => s is not null));
        return Parse(
            "{\"kind\":\"Node\",\"metadata\":{\"name\":\"" + name + "\"},\"status\":{"
            + "\"conditions\":[{\"type\":\"MemoryPressure\",\"status\":\"False\"},{\"type\":\"Ready\",\"status\":\"" + ready + "\"}],"
            + "\"allocatable\":{" + allocatable + "}}}");
    }

    private static DynamicResource Pod(string? node, string cpu, string memory, string phase = "Running", string? initCpu = null)
    {
        var bound = node is null ? "" : $"\"nodeName\":\"{node}\",";
        var init = initCpu is null
            ? ""
            : "\"initContainers\":[{\"name\":\"init\",\"resources\":{\"requests\":{\"cpu\":\"" + initCpu + "\"}}}],";
        return Parse(
            "{\"kind\":\"Pod\",\"metadata\":{\"name\":\"p\",\"namespace\":\"shop\"},\"spec\":{" + bound + init
            + "\"containers\":[{\"name\":\"app\",\"resources\":{\"requests\":{\"cpu\":\"" + cpu + "\",\"memory\":\"" + memory + "\"}}}]},"
            + "\"status\":{\"phase\":\"" + phase + "\"}}");
    }

    [Test]
    public async Task Nodes_are_counted_by_their_ready_condition()
    {
        var summary = ClusterCapacity.Summarize(
            [Node("a", "True"), Node("b", "False"), Node("c", "Unknown")], usage: null, pods: null);

        await Assert.That(summary.Nodes).IsEqualTo(3);
        await Assert.That(summary.Ready).IsEqualTo(1);
        await Assert.That(summary.NotReady).IsEqualTo(2);
    }

    [Test]
    public async Task Usage_and_requests_are_summed_against_allocatable()
    {
        var summary = ClusterCapacity.Summarize(
            [Node("a", "True"), Node("b", "True")],
            name => name == "a" ? (1_000_000_000L, 2L << 30) : (500_000_000L, 1L << 30),
            [Pod("a", "1", "1Gi"), Pod("b", "500m", "512Mi")]);

        await Assert.That(summary.Cpu.Allocatable).IsEqualTo(8d);
        await Assert.That(summary.Cpu.Used).IsEqualTo(1.5d);
        await Assert.That(summary.Cpu.UsedPercent).IsEqualTo(18.75d);
        await Assert.That(summary.Cpu.Requested).IsEqualTo(1.5d);
        await Assert.That(summary.Memory.Allocatable).IsEqualTo(16d * (1L << 30));
        await Assert.That(summary.Memory.Used).IsEqualTo(3d * (1L << 30));
        await Assert.That(summary.Memory.Requested).IsEqualTo(1.5d * (1L << 30));
    }

    /// <summary>The central degradation: what could not be read is null, never zero.</summary>
    [Test]
    public async Task Without_metrics_or_pods_the_figures_are_absent_not_zero()
    {
        var summary = ClusterCapacity.Summarize([Node("a", "True")], usage: null, pods: null);

        await Assert.That(summary.Cpu.Allocatable).IsEqualTo(4d);
        await Assert.That(summary.Cpu.Used).IsNull();
        await Assert.That(summary.Cpu.UsedPercent).IsNull();
        await Assert.That(summary.Cpu.UsedFrom).IsEqualTo(0);
        await Assert.That(summary.Cpu.Requested).IsNull();
        await Assert.That(summary.Cpu.RequestedPercent).IsNull();
        await Assert.That(summary.Memory.Used).IsNull();
        await Assert.That(summary.Memory.Requested).IsNull();
    }

    [Test]
    public async Task A_node_without_a_sample_is_out_of_both_sides_of_the_usage_share()
    {
        var summary = ClusterCapacity.Summarize(
            [Node("a", "True"), Node("b", "True")],
            name => name == "a" ? (2_000_000_000L, null) : null,
            pods: null);

        await Assert.That(summary.Cpu.UsedFrom).IsEqualTo(1);
        await Assert.That(summary.Cpu.UsedAllocatable).IsEqualTo(4d);
        await Assert.That(summary.Cpu.UsedPercent).IsEqualTo(50d);
        await Assert.That(summary.Cpu.Allocatable).IsEqualTo(8d);
    }

    [Test]
    public async Task Requests_count_only_running_pods_bound_to_a_listed_node_by_the_schedulers_formula()
    {
        var summary = ClusterCapacity.Summarize(
            [Node("a", "True")],
            usage: null,
            [
                Pod("a", "1", "1Gi"),
                Pod("a", "2", "1Gi", phase: "Succeeded"),   // finished: holds nothing
                Pod(null, "3", "1Gi", phase: "Pending"),    // not bound to any node yet
                Pod("gone", "4", "1Gi"),                    // bound to a node not in the list
                Pod("a", "250m", "0", initCpu: "1500m"),    // an init container floors the pod at 1.5
            ]);

        await Assert.That(summary.Cpu.Requested).IsEqualTo(2.5d);
        await Assert.That(summary.Cpu.RequestedPercent).IsEqualTo(62.5d);
    }

    [Test]
    public async Task A_node_with_no_allocatable_is_left_out_of_the_denominator()
    {
        var summary = ClusterCapacity.Summarize(
            [Node("a", "True"), Node("b", "True", cpu: null, memory: null)], usage: null, pods: []);

        await Assert.That(summary.Cpu.Allocatable).IsEqualTo(4d);
        await Assert.That(summary.Cpu.AllocatableFrom).IsEqualTo(1);
        await Assert.That(summary.Cpu.Requested).IsEqualTo(0d);
        await Assert.That(summary.Cpu.RequestedPercent).IsEqualTo(0d);
    }
}
