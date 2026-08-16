using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the mutating workload actions — the patch
/// bodies and the capability rules behind scale / rollout restart / delete.
///
/// <para>
/// These matter more than their size suggests: every failure mode here is <em>silent</em>.
/// A restart patch with the wrong annotation key, or one that replaces the pod
/// template's annotation map instead of merging into it, is accepted by the API server
/// with a 200 and rolls nothing — indistinguishable, from the UI, from a button that
/// does nothing. And a capability rule that reads a plain Pod as restartable, or a
/// Deployment on a server with no scale subresource as scalable, offers an action that
/// can only end in an error.
/// </para>
/// </summary>
public class WorkloadActionsTests
{
    private static DynamicResource Parse(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    private static ResourceDescriptor Descriptor(
        string kind, string plural, string[]? subresources = null, string[]? verbs = null) =>
        new("apps", "v1", kind, plural, kind.ToLowerInvariant(), true, [], [])
        {
            Subresources = subresources ?? [],
            Verbs = verbs ?? [],
        };

    private const string DeploymentJson = """
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "checkout", "namespace": "payments" },
          "spec": {
            "replicas": 3,
            "template": {
              "metadata": { "labels": { "app": "checkout" } },
              "spec": { "containers": [ { "name": "app", "image": "checkout:1" } ] }
            }
          }
        }
        """;

    private const string PodJson = """
        {
          "apiVersion": "v1",
          "kind": "Pod",
          "metadata": { "name": "checkout-abc", "namespace": "payments" },
          "spec": { "containers": [ { "name": "app", "image": "checkout:1" } ] }
        }
        """;

    // ------------------------------------------------------------ patch bodies

    /// <summary>
    /// The exact bytes of a rollout restart. The annotation key is kubectl's own, and
    /// deliberately so — a restart from kubeNimbus and one from kubectl have to be the
    /// same event to anyone reading the object afterwards.
    /// </summary>
    [Test]
    public async Task Restart_patch_stamps_kubectls_annotation_on_the_pod_template()
    {
        var patch = WorkloadActions.RestartPatch(new DateTimeOffset(2026, 8, 16, 9, 30, 15, TimeSpan.Zero));

        await Assert.That(patch).IsEqualTo(
            """{"spec":{"template":{"metadata":{"annotations":{"kubectl.kubernetes.io/restartedAt":"2026-08-16T09:30:15Z"}}}}}""");
    }

    /// <summary>
    /// The patch must reach <c>spec.template.metadata.annotations</c> and nothing else:
    /// as an RFC 7386 merge patch, every object along that path is merged rather than
    /// replaced, so the template's labels, its containers and any other annotation
    /// survive. A patch one level short (on the object's own metadata) would annotate
    /// the Deployment and roll nothing at all.
    /// </summary>
    [Test]
    public async Task Restart_patch_touches_only_the_template_annotations()
    {
        using var doc = JsonDocument.Parse(WorkloadActions.RestartPatch(DateTimeOffset.UtcNow));
        var root = doc.RootElement;

        await Assert.That(root.EnumerateObject().Count()).IsEqualTo(1);
        var template = root.GetProperty("spec").GetProperty("template");
        await Assert.That(template.EnumerateObject().Count()).IsEqualTo(1);
        var metadata = template.GetProperty("metadata");
        await Assert.That(metadata.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(metadata.GetProperty("annotations").EnumerateObject().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// RFC 3339, seconds, UTC — what kubectl writes. A local-time or fractional-second
    /// value is still accepted by the server, but it stops matching what every other
    /// tool in the ecosystem puts in this field.
    /// </summary>
    [Test]
    public async Task Restart_timestamp_is_rfc3339_utc_at_second_precision()
    {
        var at = new DateTimeOffset(2026, 8, 16, 11, 30, 15, 456, TimeSpan.FromHours(2));

        await Assert.That(WorkloadActions.FormatRestartedAt(at)).IsEqualTo("2026-08-16T09:30:15Z");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(42)]
    public async Task Scale_patch_sets_only_spec_replicas(int replicas)
    {
        var patch = WorkloadActions.ScalePatch(replicas);

        await Assert.That(patch).IsEqualTo("{\"spec\":{\"replicas\":" + replicas + "}}");
    }

    // ------------------------------------------------------- capability rules

    /// <summary>Scale comes from the server's own subresource list, not from the kind's name.</summary>
    [Test]
    public async Task Scale_is_offered_only_when_discovery_reports_a_scale_subresource()
    {
        await Assert.That(WorkloadActions.SupportsScale(
            Descriptor("Deployment", "deployments", ["scale", "status"]))).IsTrue();

        await Assert.That(WorkloadActions.SupportsScale(
            Descriptor("Deployment", "deployments", ["status"]))).IsFalse();

        // A CRD is scalable on exactly the same evidence — nothing here knows what
        // apps/v1 is.
        await Assert.That(WorkloadActions.SupportsScale(
            new ResourceDescriptor("argoproj.io", "v1alpha1", "Rollout", "rollouts", "rollout", true, [], [])
            {
                Subresources = ["scale", "status"],
            })).IsTrue();
    }

    /// <summary>A kind the server says it will not patch cannot be scaled, subresource or not.</summary>
    [Test]
    public async Task Scale_is_withheld_when_the_server_says_the_kind_is_not_patchable()
    {
        await Assert.That(WorkloadActions.SupportsScale(
            Descriptor("Deployment", "deployments", ["scale"], ["get", "list", "watch"]))).IsFalse();

        await Assert.That(WorkloadActions.SupportsScale(
            Descriptor("Deployment", "deployments", ["scale"], ["get", "list", "patch"]))).IsTrue();
    }

    /// <summary>
    /// An empty verb list means "nobody said", which is what every hand-built descriptor
    /// carries (the well-known ones, the demo catalog, fixtures). Discovery is used to
    /// hide what a server has said it cannot do — never to invent a prohibition, which
    /// would silently disable the whole feature on any descriptor not built by discovery.
    /// </summary>
    [Test]
    public async Task Unknown_verbs_are_permissive()
    {
        var unknown = Descriptor("Deployment", "deployments", ["scale"]);

        await Assert.That(unknown.AllowsVerb("patch")).IsTrue();
        await Assert.That(unknown.AllowsVerb("delete")).IsTrue();
        await Assert.That(WorkloadActions.SupportsDelete(unknown)).IsTrue();
        await Assert.That(WorkloadActions.SupportsDelete(
            Descriptor("Deployment", "deployments", verbs: ["get", "list"]))).IsFalse();
    }

    /// <summary>
    /// Restart has no discovery signal at all — no subresource, no verb — so the test is
    /// the object: does it have a pod template to stamp? That is true of Deployments,
    /// StatefulSets and DaemonSets without naming any of them, and false for a bare Pod,
    /// whose restart gesture is a delete.
    /// </summary>
    [Test]
    public async Task Restart_is_offered_for_objects_with_a_pod_template_only()
    {
        await Assert.That(WorkloadActions.SupportsRestart(
            Descriptor("Deployment", "deployments"), Parse(DeploymentJson))).IsTrue();

        await Assert.That(WorkloadActions.SupportsRestart(
            new ResourceDescriptor("", "v1", "Pod", "pods", "pod", true, [], []), Parse(PodJson))).IsFalse();

        // A template with no container list is not a pod template — a CronJob's
        // spec.jobTemplate lives elsewhere, and a half-shaped object must not be
        // patched on a guess.
        await Assert.That(WorkloadActions.SupportsRestart(
            Descriptor("Thing", "things"),
            Parse("""{"kind":"Thing","spec":{"template":{"metadata":{}}}}"""))).IsFalse();

        await Assert.That(WorkloadActions.SupportsRestart(
            Descriptor("Deployment", "deployments", verbs: ["get", "list"]), Parse(DeploymentJson))).IsFalse();
    }

    /// <summary>
    /// The opening value of the replica box. Null rather than 0 when the object has no
    /// <c>spec.replicas</c>: "does not declare replicas" and "scaled to zero" are
    /// different answers, and confusing them would offer to scale something up from a
    /// number it never had.
    /// </summary>
    [Test]
    public async Task Declared_replicas_reads_spec_replicas_and_distinguishes_absent_from_zero()
    {
        await Assert.That(WorkloadActions.DeclaredReplicas(Parse(DeploymentJson))).IsEqualTo(3);
        await Assert.That(WorkloadActions.DeclaredReplicas(Parse("""{"spec":{"replicas":0}}"""))).IsEqualTo(0);
        await Assert.That(WorkloadActions.DeclaredReplicas(Parse(PodJson))).IsNull();
        await Assert.That(WorkloadActions.DeclaredReplicas(Parse("""{"spec":{"replicas":"3"}}"""))).IsNull();
    }

    // ------------------------------------------------- discovery → descriptor

    /// <summary>
    /// The other half of the capability chain: discovery has to actually carry the
    /// subresources and verbs across. They arrive as separate entries in the same
    /// array ("deployments/scale"), in no guaranteed order relative to their parent,
    /// and the parser used to drop them on the floor.
    /// </summary>
    [Test]
    public async Task Discovery_attaches_subresources_and_verbs_to_their_parent_kind()
    {
        using var doc = JsonDocument.Parse("""
            {
              "groupVersion": "apps/v1",
              "resources": [
                { "name": "deployments/scale", "kind": "Scale", "verbs": ["get", "patch", "update"] },
                { "name": "deployments", "kind": "Deployment", "namespaced": true, "singularName": "deployment",
                  "shortNames": ["deploy"], "verbs": ["get", "list", "watch", "patch", "delete"] },
                { "name": "deployments/status", "kind": "Deployment", "verbs": ["get", "patch"] },
                { "name": "controllerrevisions", "kind": "ControllerRevision", "namespaced": true,
                  "verbs": ["get", "list"] }
              ]
            }
            """);

        var parsed = ClusterClient.ParseResourceList(doc.RootElement, "apps").ToList();

        var deployment = parsed.Single(d => d.Kind == "Deployment");
        await Assert.That(deployment.HasSubresource("scale")).IsTrue();
        await Assert.That(deployment.HasSubresource("status")).IsTrue();
        await Assert.That(deployment.HasSubresource("log")).IsFalse();
        await Assert.That(deployment.AllowsVerb("patch")).IsTrue();
        await Assert.That(WorkloadActions.SupportsScale(deployment)).IsTrue();

        // Subresource entries are never browsable kinds of their own.
        await Assert.That(parsed.Any(d => d.Plural.Contains('/'))).IsFalse();

        var revisions = parsed.Single(d => d.Kind == "ControllerRevision");
        await Assert.That(WorkloadActions.SupportsScale(revisions)).IsFalse();
        await Assert.That(WorkloadActions.SupportsDelete(revisions)).IsFalse();
    }

    /// <summary>The subresource path a scale read/patch goes to.</summary>
    [Test]
    public async Task Scale_subresource_path_is_the_object_path_plus_scale()
    {
        var descriptor = Descriptor("Deployment", "deployments", ["scale"]);

        await Assert.That(descriptor.SubresourcePath("payments", "checkout", "scale"))
            .IsEqualTo("apis/apps/v1/namespaces/payments/deployments/checkout/scale");
    }
}
