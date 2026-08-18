using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Generic (built-in or CRD) resource access, layered on the same list+watch
/// engine <see cref="WatchPodsAsync"/> uses — the sidebar tree, list views and
/// YAML editor all go through this for every resource kind except pods (which
/// keep their typed, source-generated path for the live pod list).
/// </summary>
public sealed partial class ClusterClient
{
    private const int DynamicListPageSize = 500;

    /// <summary>
    /// Live stream of one resource kind, informer-style (see <see cref="WatchPodsAsync"/>
    /// for the semantics). <paramref name="labelSelector"/> narrows both the initial list
    /// and the watch to the objects a workload owns — the same selector on both halves,
    /// or the watch would report additions the list never seeded.
    /// </summary>
    public IAsyncEnumerable<ResourceEvent<DynamicResource>> WatchResourceAsync(
        ResourceDescriptor descriptor,
        string? @namespace = null,
        Action<Exception>? connectionLost = null,
        CancellationToken cancellationToken = default,
        LabelSelector? labelSelector = null) =>
        WatchAsync(
            listPath: descriptor.CollectionPath(descriptor.Namespaced ? @namespace : null),
            listPage: (continueToken, ct) => ListResourcePageAsync(descriptor, @namespace, continueToken, ct, labelSelector: labelSelector),
            // Watch frames do carry kind/apiVersion, so this is a clone in
            // practice — routing both sources through one factory is what keeps
            // "came from the list" and "came from the watch" indistinguishable.
            deserialize: el => DynamicResource.FromListItem(el, descriptor),
            resourceVersionOf: static r => r.ResourceVersion,
            connectionLost: connectionLost,
            cancellationToken: cancellationToken,
            extraQuery: LabelSelectorQuery(labelSelector));

    /// <summary>One full (non-watching) list — used for events, typeahead and one-shot lookups.</summary>
    public async Task<IReadOnlyList<DynamicResource>> ListResourceOnceAsync(
        ResourceDescriptor descriptor,
        string? @namespace = null,
        string? fieldSelector = null,
        CancellationToken cancellationToken = default,
        LabelSelector? labelSelector = null)
    {
        var result = new List<DynamicResource>();
        string? continueToken = null;
        do
        {
            var (items, next, _) = await ListResourcePageAsync(
                descriptor, @namespace, continueToken, cancellationToken, fieldSelector, labelSelector).ConfigureAwait(false);
            result.AddRange(items);
            continueToken = next;
        } while (!string.IsNullOrEmpty(continueToken));

        return result;
    }

    private async Task<(IList<DynamicResource> Items, string? Continue, string? ResourceVersion)> ListResourcePageAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string? continueToken,
        CancellationToken ct,
        string? fieldSelector = null,
        LabelSelector? labelSelector = null)
    {
        var path = descriptor.CollectionPath(descriptor.Namespaced ? @namespace : null);
        var query = $"?limit={DynamicListPageSize}";
        if (!string.IsNullOrEmpty(continueToken))
        {
            query += $"&continue={Uri.EscapeDataString(continueToken)}";
        }

        if (!string.IsNullOrEmpty(fieldSelector))
        {
            query += $"&fieldSelector={Uri.EscapeDataString(fieldSelector)}";
        }

        query += LabelSelectorQuery(labelSelector);

        using var doc = await GetJsonDocumentAsync(path + query, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var items = new List<DynamicResource>();
        if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                // A list's items carry no kind/apiVersion (the enclosing
                // PodList implies them); the descriptor supplies them so a
                // list-seeded row behaves exactly like a watch-seeded one.
                items.Add(DynamicResource.FromListItem(item, descriptor));
            }
        }

        string? next = null;
        string? resourceVersion = null;
        if (root.TryGetProperty("metadata", out var meta))
        {
            if (meta.TryGetProperty("continue", out var c))
            {
                next = c.GetString();
            }

            if (meta.TryGetProperty("resourceVersion", out var rv))
            {
                resourceVersion = rv.GetString();
            }
        }

        return (items, next, resourceVersion);
    }

    /// <summary>Single object fetch, or null on 404.</summary>
    public async Task<DynamicResource?> ReadResourceAsync(
        ResourceDescriptor descriptor, string? @namespace, string name, CancellationToken cancellationToken = default)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, descriptor.ItemPath(@namespace, name), content: null,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        // Not EnsureSuccessStatusCode: a 403 here is a full sentence from the
        // API server ("secrets \"db-creds\" is forbidden: User \"x\" cannot get
        // resource \"secrets\" in namespace \"y\"") and that sentence is the
        // whole diagnosis — see EnsureSuccessAsync.
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DynamicResource.FromListItem(doc.RootElement, descriptor);
    }

    /// <summary>
    /// Server-side apply: PATCH with Content-Type application/apply-patch+yaml
    /// (the API server's apply decoder happily accepts JSON there too — valid
    /// JSON is valid YAML). Throws <see cref="ServerSideApplyConflictException"/>
    /// on a 409 field-manager conflict so the caller can offer force-apply.
    /// </summary>
    public async Task<DynamicResource> ApplyYamlAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        string yaml,
        string fieldManager,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApplyAsync(
            descriptor, @namespace, name, yaml, fieldManager, force, dryRun: false, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        return DynamicResource.FromListItem(doc.RootElement, descriptor);
    }

    /// <summary>
    /// What the same apply would do, without doing it: the object is read as it is now,
    /// the apply is sent with <c>dryRun=All</c>, and the two are diffed. Throws the same
    /// <see cref="ServerSideApplyConflictException"/> a real apply would — a conflict is
    /// exactly the thing worth learning before the object changes rather than after.
    /// </summary>
    /// <remarks>
    /// Both sides of the diff come from the API server, which is what makes this
    /// different from diffing the editor's text against the live object: the dry-run
    /// response has been through defaulting, admission webhooks and every mutating
    /// controller in the chain, so a field the cluster is going to add or rewrite shows
    /// up here and cannot show up in a local diff. The live read is done first and its
    /// 404 is not an error — it means the apply would create the object.
    /// </remarks>
    public async Task<ApplyPreview> PreviewApplyAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        string yaml,
        string fieldManager,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var live = await ReadResourceAsync(descriptor, @namespace, name, cancellationToken).ConfigureAwait(false);
        var body = await SendApplyAsync(
            descriptor, @namespace, name, yaml, fieldManager, force, dryRun: true, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        var previewed = DynamicResource.FromListItem(doc.RootElement, descriptor);
        return new ApplyPreview(ResourceDiff.Between(live?.Raw, previewed.Raw), previewed, live);
    }

    private async Task<string> SendApplyAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        string yaml,
        string fieldManager,
        bool force,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var json = YamlJson.ParseYamlToJson(yaml)?.ToJsonString() ?? "{}";
        var query = $"?fieldManager={Uri.EscapeDataString(fieldManager)}&force={(force ? "true" : "false")}";
        if (dryRun)
        {
            // "All" is the only value the API server defines, and it is what kubectl's
            // own --dry-run=server sends: run every admission stage and the whole
            // validation chain, then discard instead of persisting.
            query += "&dryRun=All";
        }

        using var content = new StringContent(json, Encoding.UTF8, "application/apply-patch+yaml");

        using var response = await SendRequestAsync(
            HttpMethod.Patch, descriptor.ItemPath(@namespace, name) + query, content,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new ServerSideApplyConflictException(ExtractStatusMessage(body), body);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw KubernetesApiException.From(response.StatusCode, response.ReasonPhrase, body);
        }

        return body;
    }

    /// <summary>Deletes one object; treats "already gone" (404) as success.</summary>
    public async Task DeleteResourceAsync(
        ResourceDescriptor descriptor, string? @namespace, string name, CancellationToken cancellationToken = default)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Delete, descriptor.ItemPath(@namespace, name), content: null,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>&amp;labelSelector=…</c> fragment for a selector, or an empty string when
    /// there is none. One place, so the list page and the watch cannot escape it
    /// differently — a selector escaped on one half and not the other silently gives a
    /// watch a different population from the list that seeded it.
    /// </summary>
    private static string LabelSelectorQuery(LabelSelector? labelSelector) =>
        labelSelector is null ? "" : $"&labelSelector={Uri.EscapeDataString(labelSelector.ToQuery())}";

    /// <summary>The Status <c>message</c>, or the body verbatim when it isn't one.</summary>
    private static string ExtractStatusMessage(string body) =>
        KubernetesApiException.ReadStatusMessage(body) ?? body;
}

/// <summary>
/// Raised when a server-side apply hits a field-manager conflict (HTTP 409).
/// <see cref="StatusJson"/> is the raw Status object body for a detailed
/// conflict view; callers typically offer a force-apply retry.
/// </summary>
public sealed class ServerSideApplyConflictException(string message, string statusJson) : Exception(message)
{
    public string StatusJson { get; } = statusJson;
}
