using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The rules behind the app's mutating workload actions — scale, rollout restart and
/// delete: which of them a given kind/object supports, and the exact patch body each
/// one sends. Kept apart from <see cref="ClusterClient"/> because all of it is pure
/// and every bit of it fails <em>silently</em> when it is wrong: a restart patch with
/// the wrong annotation key is accepted by the API server with a 200 and rolls
/// nothing, which is indistinguishable from "the button did nothing". That is what
/// <c>WorkloadActionsTests</c> pins, with no cluster needed.
/// </summary>
public static class WorkloadActions
{
    /// <summary>
    /// The annotation <c>kubectl rollout restart</c> stamps on a workload's <em>pod
    /// template</em>. Restarting is that stamp and nothing else: changing the template
    /// makes the controller roll its pods under its own update strategy — surge,
    /// maxUnavailable, partition, PDBs and readiness gates all honored — where deleting
    /// the pods ourselves would bypass every one of them and take a whole Deployment
    /// down at once. The key is kubectl's, deliberately, so a restart from kubeNimbus
    /// and a restart from kubectl are the same event to anyone reading the object.
    /// </summary>
    public const string RestartedAtAnnotation = "kubectl.kubernetes.io/restartedAt";

    /// <summary>The <c>scale</c> subresource's name in discovery and in a request path.</summary>
    public const string ScaleSubresource = "scale";

    /// <summary>
    /// Whether this kind can be scaled: the server declares a <c>scale</c> subresource
    /// for it <em>and</em> says the kind is patchable. Discovery, not a list of kinds —
    /// a CRD with a scale subresource (an Argo Rollout, a KEDA ScaledObject's target)
    /// is scalable exactly like a Deployment is, and neither is special-cased here.
    /// </summary>
    public static bool SupportsScale(ResourceDescriptor descriptor) =>
        descriptor.HasSubresource(ScaleSubresource) && descriptor.AllowsVerb("patch");

    /// <summary>
    /// Whether this object can be rollout-restarted. There is no discovery signal for
    /// "has a pod template" — no subresource, no verb — so the honest test is the
    /// object itself: a restart is a patch of <c>spec.template.metadata.annotations</c>,
    /// and that is meaningful precisely when the object has a pod template to patch.
    /// This covers Deployments, StatefulSets and DaemonSets without naming them, and
    /// covers a CRD that embeds a pod template too; a bare Pod has <c>spec.containers</c>
    /// and no <c>spec.template</c>, so it correctly answers false (deleting it is the
    /// gesture that restarts a pod, and that is a separate action).
    /// </summary>
    public static bool SupportsRestart(ResourceDescriptor descriptor, DynamicResource resource) =>
        descriptor.AllowsVerb("patch") && HasPodTemplate(resource);

    /// <summary>Whether the server says this kind can be deleted at all.</summary>
    public static bool SupportsDelete(ResourceDescriptor descriptor) => descriptor.AllowsVerb("delete");

    /// <summary>True when the object carries a pod template (<c>spec.template.spec.containers</c>).</summary>
    public static bool HasPodTemplate(DynamicResource resource) =>
        resource.Raw.ValueKind == JsonValueKind.Object
        && resource.Raw.TryGetProperty("spec", out var spec)
        && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("template", out var template)
        && template.ValueKind == JsonValueKind.Object
        && template.TryGetProperty("spec", out var podSpec)
        && podSpec.ValueKind == JsonValueKind.Object
        && podSpec.TryGetProperty("containers", out var containers)
        && containers.ValueKind == JsonValueKind.Array;

    /// <summary>
    /// The object's own <c>spec.replicas</c>, when it has one — the opening value for
    /// the replica box while the authoritative read of the <c>scale</c> subresource is
    /// still in flight. Null (rather than 0) when absent, because "this object does not
    /// declare replicas" and "this object is scaled to zero" are different answers.
    /// </summary>
    public static int? DeclaredReplicas(DynamicResource resource) =>
        resource.Raw.ValueKind == JsonValueKind.Object
        && resource.Raw.TryGetProperty("spec", out var spec)
        && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("replicas", out var replicas)
        && replicas.ValueKind == JsonValueKind.Number
        && replicas.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// The restart patch body: the <c>restartedAt</c> annotation on the pod template
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// Sent as an RFC 7386 <c>application/merge-patch+json</c> rather than kubectl's
    /// strategic merge patch. For a nested map of scalars the two are identical — a
    /// merge patch recurses into objects and merges keys, so sibling annotations,
    /// labels and the rest of the template survive — and merge patch is the one both
    /// built-ins and CRDs accept, where strategic merge is a 415 on a custom resource.
    /// See <see cref="ClusterClient.RestartWorkloadAsync"/>.
    /// </remarks>
    public static string RestartPatch(DateTimeOffset at)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("spec");
            writer.WriteStartObject("template");
            writer.WriteStartObject("metadata");
            writer.WriteStartObject("annotations");
            writer.WriteString(RestartedAtAnnotation, FormatRestartedAt(at));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// RFC 3339, seconds precision, UTC — the shape kubectl writes. Two restarts inside
    /// the same second therefore produce the identical annotation value, i.e. no change
    /// and no new rollout; that is kubectl's behaviour too, and the UI's job is to say
    /// the restart was accepted rather than to invent sub-second uniqueness the
    /// ecosystem doesn't use.
    /// </summary>
    public static string FormatRestartedAt(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The scale patch body — <c>{"spec":{"replicas":n}}</c>, applied to the scale subresource.</summary>
    public static string ScalePatch(int replicas)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("spec");
            writer.WriteNumber("replicas", replicas);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// One reading of a workload's <c>scale</c> subresource: what it is set to
/// (<paramref name="Replicas"/>, the spec) and how many exist right now
/// (<paramref name="CurrentReplicas"/>, the status — null when the server didn't say).
/// Two numbers rather than one because "set to 3, 1 running" is the state a scale
/// action is usually taken in.
/// </summary>
public sealed record ScaleState(int Replicas, int? CurrentReplicas);
