using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The mutating workload actions — read/set the <c>scale</c> subresource, and stamp a
/// rollout restart. Everything the app changes on a cluster other than a YAML apply or
/// a delete goes through here, and all of it is a PATCH of a body built by
/// <see cref="WorkloadActions"/> (no reflection, no serializer models).
/// </summary>
public sealed partial class ClusterClient
{
    /// <summary>
    /// RFC 7386 merge patch. The one content type both built-ins and custom resources
    /// accept — strategic merge is a 415 on a CRD — and for the nested scalar maps
    /// these two actions patch it produces exactly the same object as kubectl's
    /// strategic merge would. See <see cref="WorkloadActions.RestartPatch"/>.
    /// </summary>
    private const string MergePatchContentType = "application/merge-patch+json";

    /// <summary>
    /// Reads a workload's <c>scale</c> subresource. This, not the object's own
    /// <c>spec.replicas</c>, is the authoritative current value: a CRD may declare a
    /// different <c>specReplicasPath</c> entirely, and the subresource is the one place
    /// every scalable kind answers the same way.
    /// </summary>
    public async Task<ScaleState> GetScaleAsync(
        ResourceDescriptor descriptor, string? @namespace, string name, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonDocumentAsync(
            descriptor.SubresourcePath(@namespace, name, WorkloadActions.ScaleSubresource),
            cancellationToken).ConfigureAwait(false);

        return ReadScale(doc.RootElement);
    }

    /// <summary>
    /// Scales a workload by patching its <c>scale</c> subresource, the same way
    /// <c>kubectl scale</c> does. Returns the scale the server reports back, so the UI
    /// states what actually happened rather than what was asked for.
    /// </summary>
    /// <param name="replicas">Target replica count; 0 is valid and means "stop them all".</param>
    public async Task<ScaleState> ScaleAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        int replicas,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);

        using var content = new StringContent(WorkloadActions.ScalePatch(replicas), Encoding.UTF8, MergePatchContentType);
        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            descriptor.SubresourcePath(@namespace, name, WorkloadActions.ScaleSubresource),
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        // Not EnsureSuccessStatusCode: the interesting failures here are a 403 naming
        // the subject and the verb, and a 422 naming the field — both of which live in
        // the Status body EnsureSuccessStatusCode throws away.
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadScale(doc.RootElement);
    }

    /// <summary>
    /// The <c>kubectl rollout restart</c> gesture: stamp <c>restartedAt</c> on the pod
    /// template and let the controller roll the pods under its own update strategy.
    /// Deliberately <em>not</em> a delete-every-pod loop — that bypasses surge,
    /// maxUnavailable, partitions and PodDisruptionBudgets, and can take a whole
    /// Deployment down at once.
    /// </summary>
    /// <param name="at">
    /// The timestamp to stamp; defaults to now. Explicit so tests can pin the patch
    /// body, and because the value is the whole of the change: an identical timestamp
    /// is an identical object, which the API server accepts and no controller acts on.
    /// </param>
    public async Task RestartWorkloadAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        var body = WorkloadActions.RestartPatch(at ?? DateTimeOffset.UtcNow);
        using var content = new StringContent(body, Encoding.UTF8, MergePatchContentType);

        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            descriptor.ItemPath(@namespace, name),
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an <c>autoscaling/v1 Scale</c> object's spec/status replica counts.</summary>
    private static ScaleState ReadScale(JsonElement scale)
    {
        var replicas = 0;
        if (scale.TryGetProperty("spec", out var spec)
            && spec.ValueKind == JsonValueKind.Object
            && spec.TryGetProperty("replicas", out var specReplicas)
            && specReplicas.ValueKind == JsonValueKind.Number)
        {
            specReplicas.TryGetInt32(out replicas);
        }

        int? current = null;
        if (scale.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("replicas", out var statusReplicas)
            && statusReplicas.ValueKind == JsonValueKind.Number
            && statusReplicas.TryGetInt32(out var running))
        {
            current = running;
        }

        return new ScaleState(replicas, current);
    }
}
