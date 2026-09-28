using System.Text.Json;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-35: FEAT-43's Overview tab against a real API server. The tab is pure reading over
/// the pod's JSON, pinned by <c>PodDetailsTests</c> against hand-written objects; what only
/// a server can supply is a real failing readiness probe's condition (message and
/// timestamp), the probe timings as <em>admission</em> defaults them, and a real priority
/// class / node selector / toleration combination. The <c>DisruptionTarget</c> condition,
/// which only an eviction produces, is observed in <c>NodeOperationsLiveTests</c>.
/// </summary>
public class PodOverviewLiveTests
{
    /// <summary>
    /// A pod whose readiness probe can never pass. The manifest names the three probes'
    /// handlers and <em>no timings</em>; the server fills in all five for each, and the
    /// Overview reads them as <c>kubectl describe</c> prints them. Ready and ContainersReady
    /// come back False with the kubelet's own reason and message and a transition time.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_failing_readiness_probe_reads_with_the_servers_condition_and_defaulted_timings(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("unready");
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              terminationGracePeriodSeconds: 1
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "trap 'exit 0' TERM; sleep 3600 & wait $!"]
                  readinessProbe:
                    exec:
                      command: ["cat", "/tmp/ready"]
                  livenessProbe:
                    exec:
                      command: ["true"]
                  startupProbe:
                    exec:
                      command: ["true"]
                    failureThreshold: 1000
            """, ct);

        // The manifest said nothing about timings; the stored object has all five, per probe.
        var admitted = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
        var container = PodDetails.ContainerSpec(admitted!.Raw.GetProperty("spec"), "app")!.Value;
        foreach (var probe in new[] { "readinessProbe", "livenessProbe" })
        {
            foreach (var timing in new[] { "periodSeconds", "timeoutSeconds", "successThreshold", "failureThreshold" })
            {
                await Assert.That(container.GetProperty(probe).TryGetProperty(timing, out _)).IsTrue()
                    .Because($"{probe}.{timing} should be defaulted on admission");
            }
        }

        var probes = PodDetails.Probes(admitted, "app");
        await Assert.That(probes.Select(p => p.Kind).ToArray()).IsEquivalentTo(new[] { "Liveness", "Readiness", "Startup" });
        await Assert.That(probes.Single(p => p.Kind == "Readiness").Handler).IsEqualTo("exec [cat /tmp/ready]");
        await Assert.That(probes.Single(p => p.Kind == "Readiness").Timing)
            .IsEqualTo("delay=0s timeout=1s period=10s #success=1 #failure=3");
        await Assert.That(probes.Single(p => p.Kind == "Startup").Handler).IsEqualTo("exec [true]");
        await Assert.That(probes.Single(p => p.Kind == "Startup").Timing)
            .IsEqualTo("delay=0s timeout=1s period=10s #success=1 #failure=1000");

        // Wait for the container to run and its readiness probe to have been tried — the pod
        // then reports every standard condition, and the readiness ones False.
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pod = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
            return PodDetails.ContainerRunOf(pod!.Raw, "app") is { State: ContainerRunState.Running }
                && PodDetails.Conditions(pod).Any(c => c.Type == "Initialized" && c.Status == "True");
        }, TimeSpan.FromSeconds(90), "the container to run", ct);

        var running = (await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct))!;
        var conditions = PodDetails.Conditions(running);

        foreach (var type in new[] { "Ready", "ContainersReady" })
        {
            var condition = conditions.Single(c => c.Type == type);
            await Assert.That(condition.Status).IsEqualTo("False");
            await Assert.That(condition.Reason).IsEqualTo("ContainersNotReady");
            await Assert.That(condition.Message).IsEqualTo("containers with unready status: [app]");
            await Assert.That(condition.LastTransition).IsNotNull();
            await Assert.That(condition.Polarity).IsEqualTo(PodConditionPolarity.Positive);
            await Assert.That(condition.IsProblem).IsTrue();
        }

        foreach (var type in new[] { "PodScheduled", "Initialized" })
        {
            await Assert.That(conditions.Single(c => c.Type == type).IsProblem).IsFalse();
        }

        // Every condition a real pod reports is one the tab can judge; none comes back
        // Unclassified on this server (k3s v1.33 adds PodReadyToStartContainers).
        await Assert.That(conditions.Where(c => c.Polarity == PodConditionPolarity.Unclassified).Select(c => c.Type).ToArray())
            .IsEmpty();

        // The server's QoS verdict is read, not derived: no requests or limits is BestEffort.
        var placement = PodDetails.Placement(running);
        await Assert.That(placement.QosClass).IsEqualTo("BestEffort");
        await Assert.That(placement.Priority).IsEqualTo(0);
        await Assert.That(placement.PriorityDisplay).IsEqualTo("0");

        // The two NoExecute/300s tolerations the DefaultTolerationSeconds admission plugin
        // adds to every pod, in describe's own form.
        var tolerations = placement.Tolerations.Select(t => t.Display).ToArray();
        await Assert.That(tolerations).Contains("node.kubernetes.io/not-ready:NoExecute op=Exists for 300s");
        await Assert.That(tolerations).Contains("node.kubernetes.io/unreachable:NoExecute op=Exists for 300s");
    }

    /// <summary>
    /// The sandbox's <c>shop-web</c> pods carry a real priority class, node selector and
    /// toleration (<c>scripts/manifests/10-shop.yaml</c>). Read-only.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_real_priority_class_node_selector_and_toleration_read_as_describe_prints_them(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var pods = await client.ListResourceOnceAsync(
            ResourceDescriptor.Pods, "demo-shop", cancellationToken: ct,
            labelSelector: new LabelSelector([new LabelRequirement("app", LabelOperator.In, ["shop-web"])]));
        if (pods.Count == 0)
        {
            Skip.Test("the sandbox's demo-shop/shop-web pods are not there");
        }

        var placement = PodDetails.Placement(pods[0]);
        await Assert.That(placement.PriorityClassName).IsEqualTo("demo-shop-critical");
        await Assert.That(placement.Priority).IsEqualTo(100000);
        await Assert.That(placement.PriorityDisplay).IsEqualTo("demo-shop-critical (100000)");
        await Assert.That(placement.NodeSelector.Select(n => n.Display).ToArray()).IsEquivalentTo(new[] { "kubernetes.io/os=linux" });
        await Assert.That(placement.Tolerations.Select(t => t.Display).ToArray()).Contains("dedicated=demo-shop:NoSchedule");
        await Assert.That(placement.QosClass).IsEqualTo(pods[0].Raw.GetProperty("status").GetProperty("qosClass").GetString());
        await Assert.That(placement.QosClass).IsNotEmpty();
    }
}
