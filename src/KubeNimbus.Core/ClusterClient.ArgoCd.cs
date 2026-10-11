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
    /// this is the half of a sync that removes things, so a sync with it is a separate action
    /// that asks before the patch is sent, where a plain sync fires on its click.
    /// </param>
    /// <remarks>
    /// This is asynchronous in the strongest sense — the API server accepting the patch means
    /// the sync has been <em>requested</em>, not that it has run. What happened lands in
    /// <c>status.operationState</c> some seconds later and reaches the UI through the watch,
    /// which is why the strip reports "sync requested" rather than a result it would have to
    /// invent. An Application with an operation already in flight is Argo's own concern: it
    /// rejects or supersedes the request, and its message is what the strip prints.
    /// <para>
    /// The operation's initiator is the user this connection authenticates as, read from the
    /// API server (<see cref="GetCurrentUsernameAsync"/>), so Argo's history says who asked.
    /// What authorises the sync is Kubernetes RBAC on the Application (<c>patch</c>), not Argo
    /// CD's project roles — exactly as for <c>kubectl patch</c>; the application controller
    /// still applies the project's sync windows to it. See <c>docs/engineering/argo-cd.md</c>.
    /// </para>
    /// </remarks>
    public async Task SyncArgoApplicationAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        bool prune = false,
        CancellationToken cancellationToken = default)
    {
        ResourceDescriptor.RequireName(name);
        var username = await GetCurrentUsernameAsync(cancellationToken).ConfigureAwait(false);

        // The Application's own sync options and retry go into the operation, as Argo's API
        // server copies them (see ArgoCd.SyncPatch). Read fresh rather than taken from the
        // row, which can be minutes old on the dashboard. A missing object is left to the
        // patch, whose 404 is the message the strip then prints.
        // An account allowed to patch but not to read Applications still gets its sync, as
        // before this read existed, only without the options it could not see.
        DynamicResource? current;
        try
        {
            current = await ReadResourceAsync(descriptor, @namespace, name, cancellationToken).ConfigureAwait(false);
        }
        catch (KubernetesApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            current = null;
        }

        var policy = current is null ? default : ArgoCd.SyncPolicyOf(current);
        await PatchArgoApplicationAsync(descriptor, @namespace, name, ArgoCd.SyncPatch(prune, username, policy), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the sync Argo is running on this Application — its Terminate button and
    /// <c>argocd app terminate-op</c>. True when the request was accepted; false when there
    /// was no sync running any more (it finished between the click and the request), which
    /// the patch's own <c>test</c> operation makes the API server refuse with 422 and write
    /// nothing. Like a sync, accepted means asked: the controller stops the operation and
    /// records it as Failed, "Operation terminated", seconds later.
    /// </summary>
    /// <remarks>
    /// The patch goes to the object, never to <c>/status</c>: Argo's Application CRD declares
    /// no status subresource, so <c>…/applications/x/status</c> is a 404 (observed against
    /// Argo CD v3.5.4), and the API server lets an ordinary patch write <c>status</c>. See
    /// <see cref="ArgoCd.TerminatePatch"/>.
    /// </remarks>
    public async Task<bool> TerminateArgoOperationAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await PatchArgoApplicationAsync(
                descriptor, @namespace, name, ArgoCd.TerminatePatch, cancellationToken, JsonPatchContentType)
                .ConfigureAwait(false);
            return true;
        }
        catch (KubernetesApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            return false;
        }
    }

    private const string JsonPatchContentType = "application/json-patch+json";

    private async Task PatchArgoApplicationAsync(
        ResourceDescriptor descriptor, string? @namespace, string name, string body, CancellationToken ct,
        string contentType = MergePatchContentType)
    {
        ResourceDescriptor.RequireName(name);
        // RFC 7386 by default, the same content type the workload actions use and for the
        // same reason: a strategic merge patch is a 415 on a custom resource, and every
        // object touched here is one. Terminate alone is RFC 6902, for its test operation.
        using var content = new StringContent(body, Encoding.UTF8, contentType);
        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            descriptor.ItemPath(@namespace, name),
            content,
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }
}
