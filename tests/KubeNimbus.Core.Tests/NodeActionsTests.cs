using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the node actions — the cordon patch, the
/// eviction body, the capability rules, and the classification that decides which pods
/// a drain may touch.
///
/// <para>
/// Same argument as <see cref="WorkloadActionsTests"/>, one notch sharper. A cordon
/// patch that names the wrong field is a 200 that changes nothing, indistinguishable
/// from a dead button. And a drain that misclassifies one pod is worse than a dead
/// button: the mirror-pod and <c>emptyDir</c> branches below are exactly the two an
/// open bug in a comparable, CNCF-hosted client reports as silent data loss
/// (kubernetes-sigs/headlamp#7268). Every one of them is decided here, before anything
/// is evicted.
/// </para>
/// </summary>
public class NodeActionsTests
{
    private static DynamicResource Parse(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    private static ResourceDescriptor Node(string[]? verbs = null) =>
        new("", "v1", "Node", "nodes", "node", false, [], []) { Verbs = verbs ?? [] };

    private static ResourceDescriptor Pods(string[]? subresources = null) =>
        new("", "v1", "Pod", "pods", "pod", true, [], []) { Subresources = subresources ?? [] };

    /// <summary>A pod on <c>demo-worker-1</c>, controlled by a ReplicaSet, with no local storage.</summary>
    private static string PodJson(
        string name,
        string? ownerKind = "ReplicaSet",
        string phase = "Running",
        bool emptyDir = false,
        bool mirror = false,
        bool deleting = false)
    {
        var owner = ownerKind is null
            ? ""
            : $$"""
                ,"ownerReferences":[{"apiVersion":"apps/v1","kind":"{{ownerKind}}","name":"owner","uid":"u","controller":true}]
                """;
        var annotations = mirror ? ""","annotations":{"kubernetes.io/config.mirror":"abc123"}""" : "";
        var deletion = deleting ? ",\"deletionTimestamp\":\"2026-08-18T09:00:00Z\"" : "";
        var volumes = emptyDir ? ""","volumes":[{"name":"scratch","emptyDir":{}}]""" : "";

        return $$"""
            {
              "apiVersion": "v1",
              "kind": "Pod",
              "metadata": { "name": "{{name}}", "namespace": "payments"{{annotations}}{{deletion}}{{owner}} },
              "spec": { "nodeName": "demo-worker-1", "containers": [ { "name": "app", "image": "x:1" } ]{{volumes}} },
              "status": { "phase": "{{phase}}" }
            }
            """;
    }

    // ------------------------------------------------------------ patch bodies

    /// <summary>
    /// The exact bytes of a cordon. One field, and the field kubectl sets — anything
    /// else is a 200 that leaves the scheduler placing pods on a node someone believes
    /// they have taken out of service.
    /// </summary>
    [Test]
    public async Task Cordon_patch_sets_spec_unschedulable_true()
    {
        await Assert.That(NodeActions.CordonPatch(unschedulable: true))
            .IsEqualTo("""{"spec":{"unschedulable":true}}""");
    }

    /// <summary>
    /// Uncordon writes an explicit <c>false</c>. A JSON <c>null</c> would <em>remove</em>
    /// the field under RFC 7386, which happens to mean the same thing to the scheduler
    /// and is not what <c>kubectl uncordon</c> leaves behind.
    /// </summary>
    [Test]
    public async Task Uncordon_patch_writes_false_rather_than_removing_the_field()
    {
        var patch = NodeActions.CordonPatch(unschedulable: false);

        await Assert.That(patch).IsEqualTo("""{"spec":{"unschedulable":false}}""");
        await Assert.That(patch).DoesNotContain("null");
    }

    /// <summary>
    /// The eviction body, byte for byte. The API server checks the metadata against the
    /// request path, and the apiVersion decides which Eviction type it decodes — a
    /// mistake in either is a 400 or, worse, an eviction aimed at the wrong pod.
    /// </summary>
    [Test]
    public async Task Eviction_body_names_the_pod_and_the_policy_api_version()
    {
        await Assert.That(NodeActions.EvictionBody("payments", "checkout-abc"))
            .IsEqualTo("""{"apiVersion":"policy/v1","kind":"Eviction","metadata":{"name":"checkout-abc","namespace":"payments"}}""");
    }

    /// <summary>
    /// A grace period is carried in <c>deleteOptions</c>, and only when one was asked
    /// for: an omitted grace period leaves the pod's own
    /// <c>terminationGracePeriodSeconds</c> alone, which is a property of the app rather
    /// than of whoever is draining the node.
    /// </summary>
    [Test]
    public async Task Eviction_body_carries_a_grace_period_only_when_given()
    {
        await Assert.That(NodeActions.EvictionBody("payments", "checkout-abc", gracePeriodSeconds: 30))
            .IsEqualTo(
                """{"apiVersion":"policy/v1","kind":"Eviction","metadata":{"name":"checkout-abc","namespace":"payments"},"deleteOptions":{"gracePeriodSeconds":30}}""");

        await Assert.That(NodeActions.EvictionBody("payments", "checkout-abc"))
            .DoesNotContain("deleteOptions");
    }

    // ------------------------------------------------------------ capability

    [Test]
    public async Task Cordon_is_offered_for_a_patchable_node()
    {
        await Assert.That(NodeActions.SupportsCordon(Node(["get", "list", "patch"]))).IsTrue();
    }

    /// <summary>An empty verb list means "not known" (hand-built descriptors carry none), never "none".</summary>
    [Test]
    public async Task Cordon_is_offered_when_the_server_did_not_report_verbs()
    {
        await Assert.That(NodeActions.SupportsCordon(Node())).IsTrue();
    }

    [Test]
    public async Task Cordon_is_refused_when_the_server_says_nodes_are_not_patchable()
    {
        await Assert.That(NodeActions.SupportsCordon(Node(["get", "list", "watch"]))).IsFalse();
    }

    /// <summary>
    /// <c>spec.unschedulable</c> is a field of the core Node schema. Offering "cordon"
    /// on anything else would be offering a patch the object has no field for.
    /// </summary>
    [Test]
    public async Task Cordon_is_not_offered_for_a_kind_that_is_not_a_node()
    {
        var deployments = new ResourceDescriptor("apps", "v1", "Deployment", "deployments", "deployment", true, [], []);

        await Assert.That(NodeActions.SupportsCordon(deployments)).IsFalse();
    }

    /// <summary>
    /// Drain needs the server to serve <c>pods/eviction</c> — the same kind of evidence
    /// <c>SupportsScale</c> takes from the <c>scale</c> subresource, and the reason a
    /// cluster that does not serve it never sees a Drain menu item at all.
    /// </summary>
    [Test]
    public async Task Drain_needs_the_eviction_subresource()
    {
        await Assert.That(NodeActions.SupportsDrain(Node(), Pods(["log", "exec", "eviction"]))).IsTrue();
        await Assert.That(NodeActions.SupportsDrain(Node(), Pods(["log", "exec"]))).IsFalse();
        await Assert.That(NodeActions.SupportsDrain(Node(), podDescriptor: null)).IsFalse();
    }

    [Test]
    public async Task Cordoned_reads_the_nodes_own_spec()
    {
        var cordoned = Parse("""{"kind":"Node","metadata":{"name":"n"},"spec":{"unschedulable":true}}""");
        var open = Parse("""{"kind":"Node","metadata":{"name":"n"},"spec":{"podCIDR":"10.42.0.0/24"}}""");

        await Assert.That(NodeActions.IsCordoned(cordoned)).IsTrue();
        // Absent means schedulable: the API server omits the field rather than writing false.
        await Assert.That(NodeActions.IsCordoned(open)).IsFalse();
    }

    // ------------------------------------------------------------ drain plan

    /// <summary>
    /// An ordinary controller-owned pod with no local storage is what a drain exists to
    /// move, and it needs no options to do it.
    /// </summary>
    [Test]
    public async Task An_ordinary_replicaset_pod_is_evicted()
    {
        var plan = NodeActions.Plan([Parse(PodJson("web-1"))], new DrainOptions());

        await Assert.That(plan.EvictCount).IsEqualTo(1);
        await Assert.That(plan.IsBlocked).IsFalse();
    }

    /// <summary>
    /// A static pod's mirror is the kubelet's own shadow of a file on disk. Evicting it
    /// deletes the shadow, the kubelet recreates it seconds later, and the drain looks
    /// like it never finished. kubectl skips these unconditionally.
    /// </summary>
    [Test]
    public async Task A_mirror_pod_is_skipped_and_never_evicted()
    {
        var plan = NodeActions.Plan([Parse(PodJson("kube-apiserver-cp", ownerKind: "Node", mirror: true))], new DrainOptions());

        await Assert.That(plan.Pods[0].Disposition).IsEqualTo(DrainDisposition.SkippedMirror);
        await Assert.That(plan.EvictCount).IsEqualTo(0);
    }

    /// <summary>
    /// A DaemonSet's controller ignores <c>unschedulable</c>, so an evicted DaemonSet
    /// pod is back within seconds. Deleting them is the bug headlamp#5736 shipped;
    /// this app leaves them in place and says so in the plan.
    /// </summary>
    [Test]
    public async Task A_daemonset_pod_is_skipped_even_with_every_option_set()
    {
        var plan = NodeActions.Plan(
            [Parse(PodJson("kube-proxy-x", ownerKind: "DaemonSet"))],
            new DrainOptions(Force: true, DeleteEmptyDirData: true));

        await Assert.That(plan.Pods[0].Disposition).IsEqualTo(DrainDisposition.SkippedDaemonSet);
        await Assert.That(plan.EvictCount).IsEqualTo(0);
    }

    /// <summary>Nothing recreates a pod with no controller, so evicting it destroys it.</summary>
    [Test]
    public async Task An_unmanaged_pod_blocks_the_drain_until_force_is_given()
    {
        var pod = Parse(PodJson("legacy-runner", ownerKind: null));

        var blocked = NodeActions.Plan([pod], new DrainOptions());
        await Assert.That(blocked.Pods[0].Disposition).IsEqualTo(DrainDisposition.BlockedUnmanaged);
        await Assert.That(blocked.IsBlocked).IsTrue();
        await Assert.That(blocked.EvictCount).IsEqualTo(0);

        var forced = NodeActions.Plan([pod], new DrainOptions(Force: true));
        await Assert.That(forced.Pods[0].Disposition).IsEqualTo(DrainDisposition.Evict);
        await Assert.That(forced.IsBlocked).IsFalse();
    }

    /// <summary>
    /// An <c>emptyDir</c> lives on this node's disk and is deleted with the pod. This is
    /// the branch headlamp#7268 reports as silent data loss, and the one reason the
    /// plan refuses rather than proceeding.
    /// </summary>
    [Test]
    public async Task An_emptydir_pod_blocks_the_drain_until_its_option_is_given()
    {
        var pod = Parse(PodJson("scratch-cache", emptyDir: true));

        var blocked = NodeActions.Plan([pod], new DrainOptions());
        await Assert.That(blocked.Pods[0].Disposition).IsEqualTo(DrainDisposition.BlockedLocalData);
        await Assert.That(blocked.IsBlocked).IsTrue();

        var allowed = NodeActions.Plan([pod], new DrainOptions(DeleteEmptyDirData: true));
        await Assert.That(allowed.Pods[0].Disposition).IsEqualTo(DrainDisposition.Evict);
        // The note still says what is being destroyed — agreeing to it does not hide it.
        await Assert.That(allowed.Pods[0].Note).Contains("emptyDir");
    }

    /// <summary>
    /// A pod that has finished holds nothing; evicting it would only delete a record.
    /// kubectl describe excludes them from a node's allocation for the same reason.
    /// </summary>
    [Test]
    public async Task A_finished_pod_is_skipped()
    {
        var plan = NodeActions.Plan(
            [Parse(PodJson("db-migration", ownerKind: "Job", phase: "Succeeded"))], new DrainOptions());

        await Assert.That(plan.Pods[0].Disposition).IsEqualTo(DrainDisposition.SkippedFinished);
    }

    /// <summary>
    /// A pod already being deleted is not evicted again — its 404 would read as a
    /// failure — but the drain still waits for it, because the node is not drained until
    /// it is gone.
    /// </summary>
    [Test]
    public async Task A_terminating_pod_is_waited_for_rather_than_evicted_again()
    {
        var plan = NodeActions.Plan([Parse(PodJson("web-2", deleting: true))], new DrainOptions());

        await Assert.That(plan.Pods[0].Disposition).IsEqualTo(DrainDisposition.AlreadyTerminating);
        await Assert.That(plan.EvictCount).IsEqualTo(0);
        await Assert.That(plan.WaitingCount).IsEqualTo(1);
        await Assert.That(plan.IsEmpty).IsFalse();
    }

    /// <summary>
    /// The plan's own sentence. It has to name what is being left behind as well as what
    /// is being moved: a drain that reports only its own work leaves the reader believing
    /// the node is empty afterwards, and on any real cluster it is not.
    /// </summary>
    [Test]
    public async Task The_summary_names_what_is_left_behind_as_well_as_what_moves()
    {
        var plan = NodeActions.Plan(
            [
                Parse(PodJson("web-1")),
                Parse(PodJson("web-2")),
                Parse(PodJson("kube-proxy-x", ownerKind: "DaemonSet")),
                Parse(PodJson("kube-apiserver-cp", ownerKind: "Node", mirror: true)),
                Parse(PodJson("db-migration", ownerKind: "Job", phase: "Succeeded")),
            ],
            new DrainOptions());

        await Assert.That(plan.EvictCount).IsEqualTo(2);
        await Assert.That(plan.SkippedCount).IsEqualTo(3);
        await Assert.That(plan.Summary)
            .IsEqualTo("2 pods will be evicted · 3 left in place (DaemonSet, static or finished pods).");
    }

    /// <summary>
    /// The plan is ordered by what it will do, then by name, so two readings of the same
    /// node produce the same list rather than whatever order the API server paged in.
    /// </summary>
    [Test]
    public async Task The_plan_is_ordered_by_disposition_then_name()
    {
        var plan = NodeActions.Plan(
            [
                Parse(PodJson("kube-proxy-x", ownerKind: "DaemonSet")),
                Parse(PodJson("web-2")),
                Parse(PodJson("legacy", ownerKind: null)),
                Parse(PodJson("api-1")),
            ],
            new DrainOptions());

        await Assert.That(plan.Pods.Select(p => p.Name).ToArray())
            .IsEquivalentTo(new[] { "api-1", "web-2", "legacy", "kube-proxy-x" });
    }

    /// <summary>An empty node plans to nothing, and says so rather than looking like a failure.</summary>
    [Test]
    public async Task An_empty_node_produces_an_empty_plan()
    {
        var plan = NodeActions.Plan([], new DrainOptions());

        await Assert.That(plan.IsEmpty).IsTrue();
        await Assert.That(plan.IsBlocked).IsFalse();
    }
}
