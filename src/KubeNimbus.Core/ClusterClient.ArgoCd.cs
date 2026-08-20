using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// Argo CD over the Kubernetes API: list the Applications a cluster holds, and ask Argo's
/// own controller to sync or refresh one. There is no Argo API server in this path, no
/// <c>argocd</c> binary and no second credential — Argo's objects are custom resources, so
/// reading them is the generic list path and acting on them is a merge patch, exactly as
/// scale and rollout restart are. See <see cref="ArgoCd"/> for the patch bodies and why each
/// one is shaped the way it is.
/// </summary>
public sealed partial class ClusterClient
{
    /// <summary>
    /// Every Argo CD Application in a namespace, or across the cluster when
    /// <paramref name="namespace"/> is null. Applications almost always live in one namespace
    /// (<c>argocd</c>) while the workloads they manage are spread across the rest, so the
    /// cluster-wide read is the one the dashboard uses.
    /// </summary>
    public async Task<IReadOnlyList<ArgoApplication>> ListArgoApplicationsAsync(
        ResourceDescriptor descriptor,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var objects = await ListResourceOnceAsync(descriptor, @namespace, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return [.. objects.Select(ArgoCd.ReadApplication)];
    }

    /// <summary>
    /// Asks Argo to re-compare this Application against Git — its Refresh button, and
    /// <c>argocd app get --refresh</c>. <paramref name="hard"/> additionally re-renders the
    /// manifests from source instead of reusing the repo-server's cache.
    /// </summary>
    /// <remarks>
    /// The controller <em>removes</em> the annotation once it has acted, so there is nothing
    /// to observe afterwards on the object: this returns when the API server has accepted the
    /// patch, which is what "requested" means in the UI. A refresh changes no cluster state —
    /// it only re-runs the comparison — which is why it is the one Argo action here that does
    /// not need a prune decision.
    /// </remarks>
    public Task RefreshArgoApplicationAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        bool hard = false,
        CancellationToken cancellationToken = default) =>
        PatchArgoApplicationAsync(descriptor, @namespace, name, ArgoCd.RefreshPatch(hard), cancellationToken);

    /// <summary>
    /// Asks Argo to sync this Application: writes the object's top-level <c>operation</c>,
    /// which the application controller watches for and runs under the Application's own sync
    /// policy and options.
    /// </summary>
    /// <param name="prune">
    /// Delete resources that are no longer declared in Git. Off unless somebody has said yes:
    /// this is the half of a sync that removes things, and it is surfaced in the confirm
    /// before the patch is sent.
    /// </param>
    /// <remarks>
    /// This is asynchronous in the strongest sense — the API server accepting the patch means
    /// the sync has been <em>requested</em>, not that it has run. What happened lands in
    /// <c>status.operationState</c> some seconds later and reaches the UI through the watch,
    /// which is why the strip reports "sync requested" rather than a result it would have to
    /// invent. An Application with an operation already in flight is Argo's own concern: it
    /// rejects or supersedes the request, and its message is what the strip prints.
    /// </remarks>
    public Task SyncArgoApplicationAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        bool prune = false,
        CancellationToken cancellationToken = default) =>
        PatchArgoApplicationAsync(descriptor, @namespace, name, ArgoCd.SyncPatch(prune), cancellationToken);

    private async Task PatchArgoApplicationAsync(
        ResourceDescriptor descriptor, string? @namespace, string name, string body, CancellationToken ct)
    {
        // RFC 7386, the same content type the workload actions use and for the same reason:
        // a strategic merge patch is a 415 on a custom resource, and every object touched
        // here is one.
        using var content = new StringContent(body, Encoding.UTF8, MergePatchContentType);
        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            descriptor.ItemPath(@namespace, name),
            content,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }
}
