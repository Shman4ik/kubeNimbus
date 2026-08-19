using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The structured half of <c>kubectl describe pod</c>: conditions, tolerations, node
/// selector, QoS and priority class, and each container's probes.
///
/// <para>
/// Three things here are worth pinning rather than eyeballing, because each has a
/// plausible-looking wrong answer. A condition's polarity: pods are the opposite way
/// round from nodes (mostly positive rather than mostly pressure conditions), and an
/// unclassified type claimed "healthy" is a false reassurance to the one person reading
/// this pane because something is wrong. A toleration's rendering: <c>key:Effect
/// op=Exists for 300s</c> is not obvious from the object, and the two admission-added
/// tolerations that nearly every pod carries are exactly the ones a reader compares
/// against. And a probe's timings: the API server defaults all five, so a probe that
/// prints none of them reads as a probe with no configuration.
/// </para>
/// </summary>
public class PodDetailsTests
{
    private static DynamicResource Parse(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    private const string PodJson = """
        {
          "apiVersion": "v1",
          "kind": "Pod",
          "metadata": { "name": "report-generator-7f9-x7k", "namespace": "payments" },
          "spec": {
            "nodeName": "demo-worker-1",
            "priorityClassName": "payments-critical",
            "priority": 100000,
            "nodeSelector": { "kubernetes.io/os": "linux", "workload-tier": "payments" },
            "tolerations": [
              { "key": "dedicated", "operator": "Equal", "value": "payments", "effect": "NoSchedule" },
              { "key": "node.kubernetes.io/not-ready", "operator": "Exists", "effect": "NoExecute",
                "tolerationSeconds": 300 },
              { "operator": "Exists" }
            ],
            "initContainers": [
              {
                "name": "migrate",
                "image": "registry.internal/payments/migrate:1.2.0",
                "startupProbe": { "tcpSocket": { "port": "admin" }, "periodSeconds": 3, "failureThreshold": 30 }
              }
            ],
            "containers": [
              {
                "name": "app",
                "image": "registry.internal/payments/report-generator:2.14.3",
                "livenessProbe": {
                  "httpGet": { "path": "/livez", "port": 8080, "scheme": "HTTP" },
                  "initialDelaySeconds": 15, "periodSeconds": 20, "timeoutSeconds": 2, "failureThreshold": 6
                },
                "readinessProbe": { "httpGet": { "path": "/healthz", "port": 8080, "scheme": "HTTP" } },
                "startupProbe": { "exec": { "command": ["/bin/sh", "-c", "test -f /tmp/started"] } }
              },
              {
                "name": "envoy-sidecar",
                "image": "envoyproxy/envoy:v1.29.2",
                "livenessProbe": { "grpc": { "port": 9000, "service": "envoy.Health" } }
              }
            ]
          },
          "status": {
            "phase": "Running",
            "qosClass": "Burstable",
            "conditions": [
              { "type": "Initialized", "status": "True", "lastTransitionTime": "2026-07-19T08:12:03Z" },
              { "type": "Ready", "status": "False", "reason": "ContainersNotReady",
                "message": "containers with unready status: [app]",
                "lastTransitionTime": "2026-07-20T09:04:11Z" },
              { "type": "DisruptionTarget", "status": "True", "reason": "EvictionByEvictionAPI",
                "message": "Eviction API: evicting", "lastTransitionTime": "2026-07-20T09:05:00Z" },
              { "type": "cloud.example.com/GpuAttached", "status": "True",
                "lastTransitionTime": "2026-07-19T08:12:00Z" },
              { "type": "PodScheduled", "status": "Unknown", "lastTransitionTime": "2026-07-19T08:12:00Z" }
            ]
          }
        }
        """;

    /// <summary>A pod that has been through no admission plugin and declares nothing optional.</summary>
    private const string BarePodJson = """
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "bare", "namespace": "payments" },
          "spec": { "containers": [ { "name": "app", "image": "busybox" } ] },
          "status": { "phase": "Pending" }
        }
        """;

    // ------------------------------------------------------------------ conditions

    [Test]
    public async Task Conditions_are_read_in_order_with_their_reason_and_message()
    {
        var conditions = PodDetails.Conditions(Parse(PodJson));

        await Assert.That(conditions.Select(c => c.Type).ToArray()).IsEquivalentTo(new[]
        {
            "Initialized", "Ready", "DisruptionTarget", "cloud.example.com/GpuAttached", "PodScheduled",
        });
        await Assert.That(conditions[1].Reason).IsEqualTo("ContainersNotReady");
        await Assert.That(conditions[1].Message).IsEqualTo("containers with unready status: [app]");
        await Assert.That(conditions[1].LastTransition).IsEqualTo(
            DateTimeOffset.Parse("2026-07-20T09:04:11Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A pod's conditions are mostly positive — the opposite of a node's, where
    /// everything but Ready is a pressure condition. <c>DisruptionTarget</c> is the one
    /// Kubernetes defines the other way round.
    /// </summary>
    [Test]
    public async Task Standard_conditions_are_healthy_when_true_and_DisruptionTarget_when_false()
    {
        var conditions = PodDetails.Conditions(Parse(PodJson));

        await Assert.That(conditions.Single(c => c.Type == "Initialized").IsProblem).IsFalse();
        await Assert.That(conditions.Single(c => c.Type == "Ready").IsProblem).IsTrue();
        await Assert.That(conditions.Single(c => c.Type == "DisruptionTarget").IsProblem).IsTrue();

        await Assert.That(PodDetails.PolarityOf("ContainersReady")).IsEqualTo(PodConditionPolarity.Positive);
        await Assert.That(PodDetails.PolarityOf("PodReadyToStartContainers")).IsEqualTo(PodConditionPolarity.Positive);
        await Assert.That(PodDetails.PolarityOf("DisruptionTarget")).IsEqualTo(PodConditionPolarity.Negative);
    }

    /// <summary>
    /// The third answer. A condition type this app does not know is shown and not judged,
    /// and so is one whose status is <c>Unknown</c> — claiming either is fine would be a
    /// false reassurance, and claiming either is broken would put a red dot on a
    /// perfectly ordinary custom condition.
    /// </summary>
    [Test]
    public async Task An_unclassified_type_and_an_Unknown_status_are_neither_healthy_nor_a_problem()
    {
        var conditions = PodDetails.Conditions(Parse(PodJson));

        var custom = conditions.Single(c => c.Type == "cloud.example.com/GpuAttached");
        await Assert.That(PodDetails.PolarityOf(custom.Type)).IsEqualTo(PodConditionPolarity.Unclassified);
        await Assert.That(custom.IsProblem).IsNull();

        var unknown = conditions.Single(c => c.Type == "PodScheduled");
        await Assert.That(unknown.IsUnknown).IsTrue();
        await Assert.That(unknown.IsProblem).IsNull();
    }

    [Test]
    public async Task A_pod_with_no_conditions_reports_none_rather_than_throwing()
    {
        await Assert.That(PodDetails.Conditions(Parse(BarePodJson))).IsEmpty();
    }

    // ----------------------------------------------------------------- placement

    [Test]
    public async Task Placement_reads_qos_priority_and_the_node_selector()
    {
        var placement = PodDetails.Placement(Parse(PodJson));

        await Assert.That(placement.QosClass).IsEqualTo("Burstable");
        await Assert.That(placement.PriorityClassName).IsEqualTo("payments-critical");
        await Assert.That(placement.Priority).IsEqualTo(100000);
        await Assert.That(placement.PriorityDisplay).IsEqualTo("payments-critical (100000)");
        await Assert.That(placement.NodeSelector.Select(t => t.Display).ToArray())
            .IsEquivalentTo(new[] { "kubernetes.io/os=linux", "workload-tier=payments" });
    }

    /// <summary>
    /// The QoS class is read, never derived: an object that never went through an API
    /// server carries none, and inventing one risks disagreeing with the value the
    /// eviction path actually uses.
    /// </summary>
    [Test]
    public async Task A_bare_pod_reports_no_qos_class_and_no_priority_rather_than_a_guess()
    {
        var placement = PodDetails.Placement(Parse(BarePodJson));

        await Assert.That(placement.QosClass).IsEqualTo("");
        await Assert.That(placement.Priority).IsNull();
        await Assert.That(placement.PriorityDisplay).IsEqualTo("");
        await Assert.That(placement.NodeSelector).IsEmpty();
        await Assert.That(placement.Tolerations).IsEmpty();
    }

    /// <summary>
    /// A pod with no priority class still has a priority the scheduler compares, so the
    /// number is shown on its own when the name is absent.
    /// </summary>
    [Test]
    public async Task A_priority_with_no_class_name_still_prints_its_number()
    {
        var placement = PodDetails.Placement(Parse("""
            { "apiVersion": "v1", "kind": "Pod", "metadata": { "name": "p" },
              "spec": { "priority": 0, "containers": [] }, "status": {} }
            """));

        await Assert.That(placement.PriorityDisplay).IsEqualTo("0");
    }

    // --------------------------------------------------------------- tolerations

    /// <summary>
    /// kubectl's own three forms, including the empty-key one that tolerates everything.
    /// Every toleration is listed — the two <c>NoExecute</c>/300s ones the
    /// DefaultTolerationSeconds plugin adds included — because hiding them would make a
    /// pod that genuinely declares one indistinguishable from one that does not.
    /// </summary>
    [Test]
    public async Task Tolerations_render_the_way_kubectl_prints_them()
    {
        var tolerations = PodDetails.Placement(Parse(PodJson)).Tolerations;

        await Assert.That(tolerations.Select(t => t.Display).ToArray()).IsEquivalentTo(new[]
        {
            "dedicated=payments:NoSchedule",
            "node.kubernetes.io/not-ready:NoExecute op=Exists for 300s",
            "op=Exists",
        });
    }

    [Test]
    public async Task A_toleration_with_no_effect_tolerates_every_effect_and_says_so_without_a_dangling_colon()
    {
        var tolerations = PodDetails.Placement(Parse("""
            { "apiVersion": "v1", "kind": "Pod", "metadata": { "name": "p" },
              "spec": { "containers": [], "tolerations": [ { "key": "workload", "operator": "Exists" } ] },
              "status": {} }
            """)).Tolerations;

        await Assert.That(tolerations.Single().Display).IsEqualTo("workload op=Exists");
    }

    // -------------------------------------------------------------------- probes

    /// <summary>
    /// Liveness, Readiness, Startup — kubectl's order, so a probe read here and a probe
    /// read in a terminal are visibly the same probe.
    /// </summary>
    [Test]
    public async Task Probes_are_returned_in_kubectls_own_order()
    {
        var probes = PodDetails.Probes(Parse(PodJson), "app");

        await Assert.That(probes.Select(p => p.Kind).ToArray())
            .IsEquivalentTo(new[] { "Liveness", "Readiness", "Startup" });
    }

    [Test]
    public async Task Every_handler_shape_renders_in_kubectls_shorthand()
    {
        var app = PodDetails.Probes(Parse(PodJson), "app");
        await Assert.That(app[0].Handler).IsEqualTo("http-get http://:8080/livez");
        await Assert.That(app[2].Handler).IsEqualTo("exec [/bin/sh -c test -f /tmp/started]");

        var sidecar = PodDetails.Probes(Parse(PodJson), "envoy-sidecar");
        await Assert.That(sidecar.Single().Handler).IsEqualTo("grpc 9000 envoy.Health");

        // An init container's probes are found too, and a named port is printed as
        // written rather than resolved — a probe pointing at a port name that does not
        // exist is exactly the failure someone is here to find.
        var init = PodDetails.Probes(Parse(PodJson), "migrate");
        await Assert.That(init.Single().Kind).IsEqualTo("Startup");
        await Assert.That(init.Single().Handler).IsEqualTo("tcp-socket :admin");
    }

    /// <summary>
    /// The API server defaults all five timings on admission, so a probe that omits them
    /// has never been through a server — printing what it would be given beats printing
    /// nothing, and matches what <c>kubectl describe</c> ends up showing for the same
    /// object.
    /// </summary>
    [Test]
    public async Task Missing_timings_fall_back_to_the_API_servers_own_defaults()
    {
        var probes = PodDetails.Probes(Parse(PodJson), "app");

        await Assert.That(probes[0].Timing)
            .IsEqualTo("delay=15s timeout=2s period=20s #success=1 #failure=6");
        await Assert.That(probes[1].Timing)
            .IsEqualTo("delay=0s timeout=1s period=10s #success=1 #failure=3");
    }

    [Test]
    public async Task A_container_with_no_probes_and_a_container_that_does_not_exist_both_report_none()
    {
        await Assert.That(PodDetails.Probes(Parse(BarePodJson), "app")).IsEmpty();
        await Assert.That(PodDetails.Probes(Parse(PodJson), "no-such-container")).IsEmpty();
    }

    /// <summary>
    /// A handler this app does not recognize says so rather than rendering an empty line,
    /// which would read as a probe with no configuration at all.
    /// </summary>
    [Test]
    public async Task An_unrecognized_handler_is_named_rather_than_rendered_blank()
    {
        var probes = PodDetails.Probes(Parse("""
            { "apiVersion": "v1", "kind": "Pod", "metadata": { "name": "p" },
              "spec": { "containers": [ { "name": "app", "readinessProbe": { "somethingNew": {} } } ] },
              "status": {} }
            """), "app");

        await Assert.That(probes.Single().Handler).IsEqualTo("unrecognized handler");
    }
}
