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
    /// and the watch to the objects a workload owns, and <paramref name="fieldSelector"/>
    /// to the objects a server-side field names (the pods on one node) — each the same
    /// selector on both halves, or the watch would report additions the list never
    /// seeded. An object that starts matching a field selector (a pod bound to the node)
    /// arrives as an Added and one that stops matching as a Deleted, which is the API
    /// server's own contract for a selected watch.
    /// </summary>
    public IAsyncEnumerable<ResourceEvent<DynamicResource>> WatchResourceAsync(
        ResourceDescriptor descriptor,
        string? @namespace = null,
        Action<Exception>? connectionLost = null,
        CancellationToken cancellationToken = default,
        LabelSelector? labelSelector = null,
        string? fieldSelector = null) =>
        WatchAsync(
            listPath: descriptor.CollectionPath(descriptor.Namespaced ? @namespace : null),
            listPage: (continueToken, ct) => ListResourcePageAsync(
                descriptor, @namespace, continueToken, ct, fieldSelector: fieldSelector, labelSelector: labelSelector),
            // Watch frames do carry kind/apiVersion, so this is a clone in
            // practice — routing both sources through one factory is what keeps
            // "came from the list" and "came from the watch" indistinguishable.
            deserialize: el => DynamicResource.FromListItem(el, descriptor),
            resourceVersionOf: static r => r.ResourceVersion,
            connectionLost: connectionLost,
            cancellationToken: cancellationToken,
            extraQuery: SelectorQuery(fieldSelector, labelSelector));

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

    /// <summary>
    /// A one-shot list that stops at <paramref name="maxItems"/>. For the callers that
    /// have to stay responsive on a cluster of any size — the palette's "open logs of any
    /// pod" rows list every pod in scope on each open, and an unbounded walk of a
    /// 20 000-pod namespace would page for seconds and hold every object in memory to
    /// render fifty rows. Pages are asked for at no more than what is still wanted, so a
    /// small cap is a small request; <see cref="CappedResourceList.IsTruncated"/> says
    /// whether the server had more, which the caller has to state rather than imply the
    /// list was complete.
    /// </summary>
    public Task<CappedResourceList> ListResourceCappedAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        int maxItems,
        CancellationToken cancellationToken = default) =>
        ListResourceCappedAsync(descriptor, @namespace, maxItems, labelSelector: null, cancellationToken);

    /// <summary>The same capped list, narrowed server-side by <paramref name="labelSelector"/>.</summary>
    /// <param name="labelSelector">The NetworkPolicy pane's "pods this policy selects"; null
    /// lists everything in scope. A separate overload rather than an optional parameter so
    /// the four-argument form stays a method group the palette's log rows can bind to.</param>
    public async Task<CappedResourceList> ListResourceCappedAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        int maxItems,
        LabelSelector? labelSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);

        var result = new List<DynamicResource>();
        string? continueToken = null;
        do
        {
            var (items, next, _) = await ListResourcePageAsync(
                descriptor, @namespace, continueToken, cancellationToken, labelSelector: labelSelector,
                pageSize: Math.Min(DynamicListPageSize, maxItems - result.Count)).ConfigureAwait(false);

            // A server is allowed to ignore `limit` (an aggregated API often does), so
            // the cap is enforced here too rather than trusted to the request.
            var wanted = maxItems - result.Count;
            if (items.Count > wanted)
            {
                result.AddRange(items.Take(wanted));
                return new CappedResourceList(result, IsTruncated: true);
            }

            result.AddRange(items);
            continueToken = next;
        } while (!string.IsNullOrEmpty(continueToken) && result.Count < maxItems);

        return new CappedResourceList(result, IsTruncated: !string.IsNullOrEmpty(continueToken));
    }

    private async Task<(IList<DynamicResource> Items, string? Continue, string? ResourceVersion)> ListResourcePageAsync(
        ResourceDescriptor descriptor,
        string? @namespace,
        string? continueToken,
        CancellationToken ct,
        string? fieldSelector = null,
        LabelSelector? labelSelector = null,
        int pageSize = DynamicListPageSize)
    {
        var path = descriptor.CollectionPath(descriptor.Namespaced ? @namespace : null);
        var query = $"?limit={pageSize}";
        if (!string.IsNullOrEmpty(continueToken))
        {
            query += $"&continue={Uri.EscapeDataString(continueToken)}";
        }

        query += SelectorQuery(fieldSelector, labelSelector);

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
    /// on a 409 field-manager conflict so the caller can offer force-apply, and
    /// <see cref="ServerSideApplyValidationException"/> when strict field validation
    /// refuses an unknown field.
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
        return new ApplyPreview(
            ResourceDiff.Between(live?.Raw, previewed.Raw), previewed, live, StrictValidation: _supportsFieldValidation);
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
        var json = ApplyBody(yaml);
        var path = descriptor.ItemPath(@namespace, name);

        var strict = _supportsFieldValidation;
        var (status, reason, body) = await SendApplyOnceAsync(path, json, fieldManager, force, dryRun, strict, cancellationToken)
            .ConfigureAwait(false);

        var message = ExtractStatusMessage(body);

        // Order matters: a strict rejection is classified *before* the parameter is
        // suspected, so a manifest that happens to contain the word "fieldValidation"
        // in an unknown field cannot be read as a server that does not know the
        // parameter — which would retry without it and then apply the very typo this
        // exists to refuse.
        if (status is System.Net.HttpStatusCode.BadRequest && !LooksLikeFieldValidationRejection(message)
            && strict && MentionsFieldValidationParameter(message))
        {
            // A server too old for `fieldValidation` (pre-1.27, or an aggregated API
            // server or proxy that decodes query parameters strictly) refuses the
            // request outright. Applying must still be possible, so the parameter is
            // dropped and the request retried once — which silently gives up strictness
            // for the rest of this connection. SupportsFieldValidation is what says so,
            // and the editor prints it rather than letting the loss go unmentioned.
            _supportsFieldValidation = false;
            (status, reason, body) = await SendApplyOnceAsync(path, json, fieldManager, force, dryRun, strict: false, cancellationToken)
                .ConfigureAwait(false);
            message = ExtractStatusMessage(body);
        }

        if (status == System.Net.HttpStatusCode.Conflict)
        {
            throw new ServerSideApplyConflictException(message, body);
        }

        // A real API server refuses an unknown field in an apply patch with a **500**, not
        // the 400/422 this was first written against: the apply handler fails building the
        // typed patch ("failed to create typed patch object (ns/name; v1, Kind=ConfigMap):
        // .dta: field not declared in schema") and reports it as an internal error. Observed
        // on k3s v1.33.4 for a core kind, an apps/v1 kind and a CRD alike — and in every
        // fieldValidation mode, Strict, Warn and Ignore (ApplyLiveTests). A 500 is accepted
        // only with that wording, the one a real server sends with it.
        if ((status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity
                && LooksLikeFieldValidationRejection(message))
            || (status is System.Net.HttpStatusCode.InternalServerError
                && message.Contains("field not declared in schema", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServerSideApplyValidationException(message, body);
        }

        if ((int)status is < 200 or > 299)
        {
            throw KubernetesApiException.From(status, reason, body);
        }

        return body;
    }

    /// <summary>
    /// The JSON an apply sends for the editor's text. The editor opens an object exactly as the
    /// server returned it, so its text carries <c>metadata.managedFields</c>, and the API server
    /// refuses an apply patch that does: <c>metadata.managedFields must be nil</c> (HTTP 400, on
    /// the dry run and on the real apply). kubectl removes the field for the same reason before
    /// it sends anything. It is the server's own record of who owns which field, so dropping it
    /// from what is sent loses nothing the apply could use.
    /// </summary>
    /// <remarks>
    /// Only that one field is removed. <c>resourceVersion</c> stays, because a server-side apply
    /// that carries one is an optimistic lock and the editor is exactly the place that wants it:
    /// a Conflict is the answer when the object changed while it was open. <c>uid</c>,
    /// <c>creationTimestamp</c>, <c>generation</c>, <c>selfLink</c> and <c>status</c> are accepted
    /// by a real server in an apply body (ApplyLiveTests sends them), so none of them is touched.
    /// </remarks>
    internal static string ApplyBody(string yaml)
    {
        var node = YamlJson.ParseYamlToJson(yaml);
        if (node is System.Text.Json.Nodes.JsonObject { } root
            && root["metadata"] is System.Text.Json.Nodes.JsonObject metadata)
        {
            metadata.Remove("managedFields");
        }

        return node?.ToJsonString() ?? "{}";
    }

    private async Task<(System.Net.HttpStatusCode Status, string? Reason, string Body)> SendApplyOnceAsync(
        string path,
        string json,
        string fieldManager,
        bool force,
        bool dryRun,
        bool strict,
        CancellationToken cancellationToken)
    {
        var query = $"?fieldManager={Uri.EscapeDataString(fieldManager)}&force={(force ? "true" : "false")}";
        if (dryRun)
        {
            // "All" is the only value the API server defines, and it is what kubectl's
            // own --dry-run=server sends: run every admission stage and the whole
            // validation chain, then discard instead of persisting.
            query += "&dryRun=All";
        }

        if (strict)
        {
            // Without this the API server runs its default `Warn` mode, which *prunes* a
            // misspelled or unknown field and reports it only in a response header
            // nothing reads — so the apply "succeeds" having dropped the edit, and the
            // dry-run preview built on the same request shows a clean diff for exactly
            // that typo. Strict makes the server refuse instead, in its own words.
            query += "&fieldValidation=Strict";
        }

        using var content = new StringContent(json, Encoding.UTF8, "application/apply-patch+yaml");

        using var response = await SendRequestAsync(
            HttpMethod.Patch, path + query, content,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, response.ReasonPhrase, body);
    }

    /// <summary>
    /// Whether the server accepted <c>fieldValidation=Strict</c>. True until one refuses
    /// it, after which every apply on this connection runs in the API server's default
    /// <c>Warn</c> mode — i.e. an unknown field is pruned rather than refused, which is
    /// the honest cost of the fallback and is stated in the UI rather than hidden.
    /// </summary>
    public bool SupportsFieldValidation => _supportsFieldValidation;

    private volatile bool _supportsFieldValidation = true;

    /// <summary>
    /// Whether a refusal is the server complaining about the <c>fieldValidation</c>
    /// parameter itself. There is no distinct status code for it — an unsupported query
    /// parameter and an unknown manifest field are both 400 — so the parameter's own name
    /// in the message is the signal, and it is only consulted once the message has been
    /// ruled out as a strict-decoding error.
    /// </summary>
    private static bool MentionsFieldValidationParameter(string message) =>
        message.Contains("fieldValidation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a refusal is strict field validation doing its job. The three wordings are
    /// the API server's own: <c>strict decoding error</c> from the decoder, <c>unknown
    /// field</c> from the field-validation path, and <c>field not declared in schema</c>
    /// from apply's typed-patch conversion.
    /// </summary>
    private static bool LooksLikeFieldValidationRejection(string message) =>
        message.Contains("strict decoding error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unknown field", StringComparison.OrdinalIgnoreCase)
        || message.Contains("field not declared in schema", StringComparison.OrdinalIgnoreCase);

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
    /// The <c>&amp;fieldSelector=…&amp;labelSelector=…</c> fragment, or an empty string when
    /// there is neither. One place, so the list page and the watch cannot escape either
    /// differently — a selector escaped on one half and not the other silently gives a
    /// watch a different population from the list that seeded it.
    /// </summary>
    private static string SelectorQuery(string? fieldSelector, LabelSelector? labelSelector) =>
        (string.IsNullOrEmpty(fieldSelector) ? "" : $"&fieldSelector={Uri.EscapeDataString(fieldSelector)}")
        + (labelSelector is null ? "" : $"&labelSelector={Uri.EscapeDataString(labelSelector.ToQuery())}");

    /// <summary>The Status <c>message</c>, or the body verbatim when it isn't one.</summary>
    private static string ExtractStatusMessage(string body) =>
        KubernetesApiException.ReadStatusMessage(body) ?? body;
}

/// <summary>
/// Raised when the API server refuses an apply because the document carries a field it
/// does not know — the outcome <c>fieldValidation=Strict</c> exists to produce. Without
/// the parameter the server's default <c>Warn</c> mode prunes that field and answers 200,
/// so a misspelling is applied as a deletion of whatever it meant to set and nothing on
/// screen says so. <see cref="Exception.Message"/> is the server's own sentence, which
/// names the field; <see cref="StatusJson"/> is the raw Status body.
///
/// <para>
/// Deliberately not a subclass of the conflict exception and not offered a retry: unlike
/// a 409 there is nothing to force. The document has to be corrected.
/// </para>
/// </summary>
public sealed class ServerSideApplyValidationException(string message, string statusJson) : Exception(message)
{
    public string StatusJson { get; } = statusJson;
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

/// <summary>
/// The result of <see cref="ClusterClient.ListResourceCappedAsync"/>: at most the cap's
/// worth of objects, and whether the server had more than that.
/// </summary>
public sealed record CappedResourceList(IReadOnlyList<DynamicResource> Items, bool IsTruncated);
