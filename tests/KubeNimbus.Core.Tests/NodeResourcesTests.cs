using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The read-only half of the node surface: conditions, taints, and the
/// allocatable-vs-requested arithmetic.
///
/// <para>
/// The arithmetic is the part worth pinning. It is the number someone decides to drain
/// on, and every way of getting it wrong produces a <em>plausible</em> figure: summing
/// init containers alongside the regular ones overstates a node running Jobs, ignoring
/// them understates a node mid-startup, counting finished pods shows a node full of
/// completed Jobs, and measuring against capacity rather than allocatable overstates the
/// free room by whatever the kubelet has reserved. None of those announce themselves.
/// </para>
/// </summary>
public class NodeResourcesTests
{
    private static DynamicResource Parse(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>8 cores / 16 GiB of capacity, 7.8 cores / ~15.1 GiB allocatable, 110 pods.</summary>
    private const string NodeJson = """
        {
          "apiVersion": "v1",
          "kind": "Node",
          "metadata": { "name": "demo-worker-1", "labels": { "node-role.kubernetes.io/worker": "" } },
          "spec": {
            "podCIDR": "10.42.1.0/24",
            "taints": [
              { "key": "workload", "value": "batch", "effect": "PreferNoSchedule" },
              { "key": "node.kubernetes.io/unschedulable", "effect": "NoSchedule" }
            ]
          },
          "status": {
            "capacity":    { "cpu": "8", "memory": "16Gi", "pods": "110" },
            "allocatable": { "cpu": "7800m", "memory": "15Gi", "pods": "110" },
            "conditions": [
              { "type": "MemoryPressure", "status": "False", "reason": "KubeletHasSufficientMemory", "message": "ok",
                "lastTransitionTime": "2026-07-11T06:05:12Z" },
              { "type": "DiskPressure", "status": "True", "reason": "KubeletHasDiskPressure", "message": "disk is full",
                "lastTransitionTime": "2026-07-30T07:58:02Z" },
              { "type": "Ready", "status": "True", "reason": "KubeletReady", "message": "kubelet is posting ready status",
                "lastTransitionTime": "2026-07-11T06:05:12Z" }
            ],
            "addresses": [ { "type": "Hostname", "address": "demo-worker-1" }, { "type": "InternalIP", "address": "10.0.1.21" } ],
            "nodeInfo": {
              "architecture": "amd64",
              "containerRuntimeVersion": "containerd://1.7.20",
              "kernelVersion": "6.8.0-45-generic",
              "kubeletVersion": "v1.31.2",
              "osImage": "Ubuntu 24.04.1 LTS"
            }
          }
        }
        """;

    private static DynamicResource Pod(string spec, string phase = "Running") =>
        Parse($$"""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "p", "namespace": "payments" },
              "spec": {{spec}},
              "status": { "phase": "{{phase}}" }
            }
            """);

    private static string Containers(params string[] requests) =>
        $$"""{ "containers": [ {{string.Join(",", requests.Select((r, i) =>
            $$"""{ "name": "c{{i}}", "resources": { "requests": {{r}} } }"""))}} ] }""";

    // ------------------------------------------------------------ conditions and taints

    [Test]
    public async Task Conditions_are_read_in_order_with_their_reason_and_message()
    {
        var conditions = NodeResources.Conditions(Parse(NodeJson));

        await Assert.That(conditions.Select(c => c.Type).ToArray())
            .IsEquivalentTo(new[] { "MemoryPressure", "DiskPressure", "Ready" });
        await Assert.That(conditions[1].Reason).IsEqualTo("KubeletHasDiskPressure");
        await Assert.That(conditions[1].Message).IsEqualTo("disk is full");
    }

    /// <summary>
    /// The polarity is read off <c>Ready</c>, the one condition Kubernetes defines as
    /// positive; everything else is a pressure condition and healthy when False. That is
    /// what makes a condition nobody has heard of — a cloud provider's, or
    /// node-problem-detector's — classify the way kubectl classifies it, instead of
    /// defaulting to "fine" because it is not on a list.
    /// </summary>
    [Test]
    public async Task Ready_is_a_problem_when_false_and_a_pressure_condition_when_true()
    {
        var conditions = NodeResources.Conditions(Parse(NodeJson));

        await Assert.That(conditions.Single(c => c.Type == "Ready").IsProblem).IsFalse();
        await Assert.That(conditions.Single(c => c.Type == "DiskPressure").IsProblem).IsTrue();
        await Assert.That(conditions.Single(c => c.Type == "MemoryPressure").IsProblem).IsFalse();

        var unknown = new NodeCondition("Ready", "Unknown", "NodeStatusUnknown", "kubelet stopped posting", null);
        await Assert.That(unknown.IsProblem).IsTrue();
        await Assert.That(unknown.IsUnknown).IsTrue();
    }

    [Test]
    public async Task Taints_render_the_way_kubectl_prints_them()
    {
        var taints = NodeResources.Taints(Parse(NodeJson));

        await Assert.That(taints[0].Display).IsEqualTo("workload=batch:PreferNoSchedule");
        // No value: kubectl prints key:Effect, not key=:Effect.
        await Assert.That(taints[1].Display).IsEqualTo("node.kubernetes.io/unschedulable:NoSchedule");
    }

    [Test]
    public async Task Node_info_reads_the_kubelet_block_and_the_internal_ip()
    {
        var info = NodeResources.Info(Parse(NodeJson));

        await Assert.That(info.KubeletVersion).IsEqualTo("v1.31.2");
        await Assert.That(info.ContainerRuntime).IsEqualTo("containerd://1.7.20");
        // The InternalIP, not simply the first address — this node lists Hostname first.
        await Assert.That(info.InternalIp).IsEqualTo("10.0.1.21");
    }

    // ------------------------------------------------------------ the arithmetic

    /// <summary>
    /// Allocatable, not capacity, is the denominator: capacity includes what
    /// <c>--system-reserved</c> and <c>--kube-reserved</c> hold back, and a headroom
    /// figure against it overstates the room by exactly that much.
    /// </summary>
    [Test]
    public async Task Requests_are_summed_against_allocatable_not_capacity()
    {
        var summary = NodeResources.Summarize(
            Parse(NodeJson),
            [
                Pod(Containers("""{ "cpu": "500m", "memory": "1Gi" }""")),
                Pod(Containers("""{ "cpu": "1300m", "memory": "2Gi" }""")),
            ]);

        await Assert.That(summary.Cpu.Requested).IsEqualTo(1.8).Within(0.0001);
        await Assert.That(summary.Cpu.Allocatable!.Value).IsEqualTo(7.8).Within(0.0001);
        await Assert.That(summary.Cpu.Capacity!.Value).IsEqualTo(8d).Within(0.0001);
        // 1.8 of 7.8 allocatable is ~23%; of 8 capacity it would read 22.5% — the
        // difference is small here and is the whole node's reserve on a small machine.
        await Assert.That(summary.Cpu.RequestedPercent!.Value).IsEqualTo(23.08).Within(0.01);
        await Assert.That(summary.Memory.Requested).IsEqualTo(3d * 1024 * 1024 * 1024).Within(1);
        await Assert.That(summary.PodCount).IsEqualTo(2);
    }

    /// <summary>
    /// An init container runs alone and exits, so it needs its own share while it runs
    /// but adds nothing to the steady state: the scheduler charges
    /// <c>max(sum(containers), max(initContainers))</c>. Summing them all is the easy
    /// mistake, and it overstates every node running Jobs.
    /// </summary>
    [Test]
    public async Task An_init_container_is_a_floor_not_an_addition()
    {
        var spec = """
            {
              "initContainers": [ { "name": "migrate", "resources": { "requests": { "cpu": "2" } } } ],
              "containers": [ { "name": "app", "resources": { "requests": { "cpu": "500m" } } } ]
            }
            """;

        // max(0.5, 2) = 2, not 2.5.
        await Assert.That(NodeResources.EffectiveRequest(Pod(spec), NodeResources.Cpu, "requests"))
            .IsEqualTo(2d).Within(0.0001);

        var smallInit = """
            {
              "initContainers": [ { "name": "wait", "resources": { "requests": { "cpu": "100m" } } } ],
              "containers": [ { "name": "app", "resources": { "requests": { "cpu": "500m" } } } ]
            }
            """;

        await Assert.That(NodeResources.EffectiveRequest(Pod(smallInit), NodeResources.Cpu, "requests"))
            .IsEqualTo(0.5).Within(0.0001);
    }

    /// <summary>
    /// A native sidecar (an init container with <c>restartPolicy: Always</c>, Kubernetes
    /// 1.28+) never exits, so it is part of the steady state and adds rather than floors.
    /// </summary>
    [Test]
    public async Task A_native_sidecar_adds_to_the_running_total()
    {
        var spec = """
            {
              "initContainers": [
                { "name": "proxy", "restartPolicy": "Always", "resources": { "requests": { "cpu": "200m" } } },
                { "name": "migrate", "resources": { "requests": { "cpu": "300m" } } }
              ],
              "containers": [ { "name": "app", "resources": { "requests": { "cpu": "500m" } } } ]
            }
            """;

        // running = 500m + 200m sidecar = 700m; the plain init container's 300m floor
        // does not reach it.
        await Assert.That(NodeResources.EffectiveRequest(Pod(spec), NodeResources.Cpu, "requests"))
            .IsEqualTo(0.7).Within(0.0001);
    }

    /// <summary>Pod overhead is what a runtime class charges for the sandbox itself, on top of everything else.</summary>
    [Test]
    public async Task Pod_overhead_is_added_on_top()
    {
        var spec = """
            {
              "overhead": { "cpu": "250m" },
              "containers": [ { "name": "app", "resources": { "requests": { "cpu": "500m" } } } ]
            }
            """;

        await Assert.That(NodeResources.EffectiveRequest(Pod(spec), NodeResources.Cpu, "requests"))
            .IsEqualTo(0.75).Within(0.0001);
    }

    /// <summary>
    /// Succeeded and Failed pods hold nothing. Counting them shows a node full of
    /// finished Jobs, which is exactly what <c>kubectl describe node</c> avoids.
    /// </summary>
    [Test]
    public async Task Finished_pods_are_excluded_from_the_totals()
    {
        var summary = NodeResources.Summarize(
            Parse(NodeJson),
            [
                Pod(Containers("""{ "cpu": "500m" }""")),
                Pod(Containers("""{ "cpu": "4" }"""), phase: "Succeeded"),
                Pod(Containers("""{ "cpu": "4" }"""), phase: "Failed"),
            ]);

        await Assert.That(summary.Cpu.Requested).IsEqualTo(0.5).Within(0.0001);
        await Assert.That(summary.PodCount).IsEqualTo(1);
        await Assert.That(summary.Pods.Requested).IsEqualTo(1d).Within(0.0001);
    }

    /// <summary>
    /// A container with no requests declared asks for nothing — a BestEffort pod really
    /// is free as far as the scheduler is concerned, and treating an absent request as
    /// anything else would invent load the cluster does not have.
    /// </summary>
    [Test]
    public async Task A_pod_with_no_requests_contributes_nothing_but_still_counts_as_a_pod()
    {
        var summary = NodeResources.Summarize(
            Parse(NodeJson), [Pod("""{ "containers": [ { "name": "app" } ] }""")]);

        await Assert.That(summary.Cpu.Requested).IsEqualTo(0d);
        await Assert.That(summary.PodCount).IsEqualTo(1);
    }

    /// <summary>Limits are tracked separately and legitimately exceed allocatable.</summary>
    [Test]
    public async Task Limits_are_summed_separately_and_may_oversubscribe()
    {
        var spec = """
            {
              "containers": [
                { "name": "app", "resources": { "requests": { "cpu": "500m" }, "limits": { "cpu": "8" } } }
              ]
            }
            """;

        var summary = NodeResources.Summarize(Parse(NodeJson), [Pod(spec)]);

        await Assert.That(summary.Cpu.Limit!.Value).IsEqualTo(8d).Within(0.0001);
        await Assert.That(summary.Cpu.LimitPercent!.Value).IsGreaterThan(100d);
        await Assert.That(summary.Cpu.Free!.Value).IsEqualTo(7.3).Within(0.0001);
    }

    /// <summary>
    /// A node that reports no allocatable at all (a NotReady node whose kubelet has never
    /// posted status) has no percentage to give, and the pane says so rather than
    /// dividing by zero into a plausible-looking figure.
    /// </summary>
    [Test]
    public async Task A_node_with_no_allocatable_reports_no_percentage()
    {
        var bare = Parse("""{ "apiVersion": "v1", "kind": "Node", "metadata": { "name": "n" }, "status": {} }""");

        var summary = NodeResources.Summarize(bare, [Pod(Containers("""{ "cpu": "500m" }"""))]);

        await Assert.That(summary.Cpu.Allocatable).IsNull();
        await Assert.That(summary.Cpu.RequestedPercent).IsNull();
        await Assert.That(summary.Cpu.Free).IsNull();
        await Assert.That(summary.Cpu.Requested).IsEqualTo(0.5).Within(0.0001);
    }
}
