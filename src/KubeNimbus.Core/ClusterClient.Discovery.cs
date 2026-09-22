using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Discovery API: walks <c>/api</c> (core) and <c>/apis</c> (grouped) to build
/// the catalog of browsable resource kinds — CRDs show up here automatically,
/// nothing is hardcoded. Parsed with raw <see cref="JsonDocument"/>, same as
/// the watch/log paths in ClusterClient.cs, since the discovery response shape
/// isn't worth a source-generated model for.
/// </summary>
public sealed partial class ClusterClient
{
    /// <summary>
    /// All resource kinds the server exposes (core + every API group's preferred
    /// version), skipping subresources (e.g. "pods/status") and non-listable
    /// entries. Order is not guaranteed — callers group/sort for the sidebar.
    /// </summary>
    public async Task<IReadOnlyList<ResourceDescriptor>> DiscoverResourcesAsync(CancellationToken cancellationToken = default)
    {
        _discoveryComplete = true;
        var endpoints = await Task.WhenAll(FetchDiscoveryAsync("api", cancellationToken),
            FetchDiscoveryAsync("apis", cancellationToken)).ConfigureAwait(false);
        using var core = endpoints[0];
        using var grouped = endpoints[1];
        var coreAggregated = ParseAggregatedDiscovery(core?.RootElement);
        var groupedAggregated = ParseAggregatedDiscovery(grouped?.RootElement);
        if (coreAggregated is not null && groupedAggregated is not null)
            return [.. coreAggregated, .. groupedAggregated];

        var coreTask = coreAggregated is not null
            ? Task.FromResult(coreAggregated)
            : FetchResourceListAsync("api/v1", "", false, cancellationToken);
        List<(string Group, string Version)> groupVersions = [];
        // Reuse the legacy response when negotiation fell back to APIGroupList.
        using var legacyGroups = groupedAggregated is null
            && (grouped is null || IsAggregated(grouped.RootElement))
            ? await GetJsonDocumentAsync("apis", cancellationToken).ConfigureAwait(false) : null;
        var groupRoot = legacyGroups?.RootElement ?? grouped?.RootElement;
        if (groupedAggregated is null && groupRoot is { } root
            && root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("name", out var n) ? n.GetString() : null;
                var version = PreferredVersion(group);
                if (groupName is not null && version is not null) groupVersions.Add((groupName, version));
            }

        // One request per API group, issued concurrently. These used to go out one after
        // another, which made connect time scale with round-trip time × group count: a
        // cluster running cert-manager, Istio and Argo serves 50+ groups, so at 100 ms
        // to a distant API server the sidebar — and the first pod list, which waits on
        // it — took five seconds that were nothing but queueing. The bound keeps a
        // 100-group cluster from opening 100 connections at once; kubectl's own
        // discovery client fans out the same way.
        using var gate = new SemaphoreSlim(MaxConcurrentDiscoveryRequests);
        var groupTasks = groupVersions.Select(async gv =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchResourceListAsync(
                    $"apis/{gv.Group}/{gv.Version}", gv.Group, tolerateFailure: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var result = new List<ResourceDescriptor>(await coreTask.ConfigureAwait(false));

        // Awaited in the order the server listed the groups, so the catalog's order is
        // as stable as it was when the requests were sequential.
        foreach (var descriptors in await Task.WhenAll(groupTasks).ConfigureAwait(false))
        {
            result.AddRange(descriptors);
        }

        if (groupedAggregated is not null) result.AddRange(groupedAggregated);
        return result;
    }

    /// <summary>
    /// How many group discovery requests may be in flight at once. High enough that a
    /// typical cluster's groups all go out in two or three waves, low enough not to look
    /// like a burst to an API server's priority-and-fairness limits.
    /// </summary>
    internal const int MaxConcurrentDiscoveryRequests = 16;

    private async Task<IReadOnlyList<ResourceDescriptor>> FetchResourceListAsync(
        string path, string group, bool tolerateFailure, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonDocumentAsync(path, cancellationToken).ConfigureAwait(false);

            // Materialized before the document is disposed: ParseResourceList is lazy
            // and reads JsonElements that die with it.
            return [.. ParseResourceList(doc.RootElement, group)];
        }
        catch (HttpRequestException) when (tolerateFailure)
        {
            _discoveryComplete = false;
            // A group can vanish between listing and querying (webhook-backed
            // aggregated APIs); skip it rather than fail the whole catalog.
            return [];
        }
    }

    private bool _discoveryComplete;
    internal const string DiscoveryAccept =
        "application/json;g=apidiscovery.k8s.io;v=v2;as=APIGroupDiscoveryList," +
        "application/json;g=apidiscovery.k8s.io;v=v2beta1;as=APIGroupDiscoveryList,application/json";

    private async Task<JsonDocument?> FetchDiscoveryAsync(string path, CancellationToken ct)
    {
        using var response = await SendRequestAsync(HttpMethod.Get, path, null,
            HttpCompletionOption.ResponseContentRead, ct, DiscoveryAccept).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotAcceptable)
            return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static bool IsAggregated(JsonElement root) =>
        root.TryGetProperty("kind", out var kind) && kind.GetString() == "APIGroupDiscoveryList";

    /// <summary>Null means legacy or stale discovery: query the individual group endpoints.</summary>
    internal static IReadOnlyList<ResourceDescriptor>? ParseAggregatedDiscovery(JsonElement? document)
    {
        if (document is not { } root || !IsAggregated(root)
            || !root.TryGetProperty("items", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        var result = new List<ResourceDescriptor>();
        foreach (var group in groups.EnumerateArray())
        {
            var name = group.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("name", out var n)
                ? n.GetString() ?? "" : "";
            if (!group.TryGetProperty("versions", out var versions) || versions.GetArrayLength() == 0) continue;
            // The server sorts versions in preference order, matching preferredVersion in legacy discovery.
            var version = versions[0];
            if (!version.TryGetProperty("freshness", out var freshness) || freshness.GetString() != "Current") return null;
            if (!version.TryGetProperty("resources", out var resources)) return null;
            foreach (var resource in resources.EnumerateArray())
            {
                var plural = resource.GetProperty("resource").GetString() ?? "";
                var verbs = Strings(resource, "verbs");
                if (plural.Length == 0 || plural.Contains('/') || !verbs.Contains("list")) continue;
                if (!resource.TryGetProperty("responseKind", out var responseKind)
                    || !responseKind.TryGetProperty("kind", out var kind) || string.IsNullOrEmpty(kind.GetString())) continue;
                result.Add(new ResourceDescriptor(name, version.GetProperty("version").GetString()!,
                    kind.GetString()!, plural,
                    resource.TryGetProperty("singularResource", out var singular) ? singular.GetString() ?? plural : plural,
                    resource.TryGetProperty("scope", out var scope) && scope.GetString() == "Namespaced",
                    Strings(resource, "shortNames"), Strings(resource, "categories"))
                {
                    Verbs = verbs,
                    Subresources = resource.TryGetProperty("subresources", out var subs)
                        ? subs.EnumerateArray().Select(sub => sub.GetProperty("subresource").GetString() ?? "").ToArray() : [],
                });
            }
        }
        return result;
    }

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];

    private static string? PreferredVersion(JsonElement group)
    {
        if (group.TryGetProperty("preferredVersion", out var preferred)
            && preferred.TryGetProperty("version", out var pv))
        {
            return pv.GetString();
        }

        if (group.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versions.EnumerateArray())
            {
                if (v.TryGetProperty("version", out var ver))
                {
                    return ver.GetString();
                }
            }
        }

        return null;
    }

    internal static IEnumerable<ResourceDescriptor> ParseResourceList(JsonElement resourceList, string group)
    {
        var version = resourceList.TryGetProperty("groupVersion", out var gv)
            ? (gv.GetString() ?? "v1").Split('/').Last()
            : "v1";

        if (!resourceList.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Subresources are entries in the same array ("deployments/scale"), and their
        // order relative to the parent is not guaranteed, so they are collected in a
        // first pass and attached in the second. They are what tells the app that a
        // kind can be scaled without a hardcoded list of kinds that can — including a
        // CRD that declares `scale`, and excluding an apps/v1 resource on a server
        // that (for whatever reason) doesn't serve one.
        var subresources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var res in resources.EnumerateArray())
        {
            if (res.TryGetProperty("name", out var n) && n.GetString() is { } name
                && name.IndexOf('/') is var slash && slash > 0)
            {
                var parent = name[..slash];
                var child = name[(slash + 1)..];
                if (!subresources.TryGetValue(parent, out var list))
                {
                    subresources[parent] = list = [];
                }

                list.Add(child);
            }
        }

        return ParseParents(resources, group, version, subresources);
    }

    private static IEnumerable<ResourceDescriptor> ParseParents(
        JsonElement resources, string group, string version, Dictionary<string, List<string>> subresources)
    {
        foreach (var res in resources.EnumerateArray())
        {
            var name = res.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || name.Contains('/'))
            {
                continue; // subresource (status, scale, log, exec, ...) — not independently browsable
            }

            var verbs = res.TryGetProperty("verbs", out var verbsEl) && verbsEl.ValueKind == JsonValueKind.Array
                ? verbsEl.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray()
                : [];

            if (verbs.Length > 0 && !verbs.Contains("list", StringComparer.Ordinal))
            {
                continue; // not listable — nothing to show in a table
            }

            yield return new ResourceDescriptor(
                Group: group,
                Version: version,
                Kind: res.TryGetProperty("kind", out var k) ? k.GetString() ?? name : name,
                Plural: name,
                SingularName: res.TryGetProperty("singularName", out var sn) ? sn.GetString() ?? name : name,
                Namespaced: res.TryGetProperty("namespaced", out var ns) && ns.ValueKind == JsonValueKind.True,
                ShortNames: res.TryGetProperty("shortNames", out var short_) && short_.ValueKind == JsonValueKind.Array
                    ? [.. short_.EnumerateArray().Select(s => s.GetString() ?? "")]
                    : [],
                Categories: res.TryGetProperty("categories", out var cat) && cat.ValueKind == JsonValueKind.Array
                    ? [.. cat.EnumerateArray().Select(c => c.GetString() ?? "")]
                    : [])
            {
                Subresources = subresources.TryGetValue(name, out var subs) ? subs : [],
                Verbs = verbs,
            };
        }
    }
}
