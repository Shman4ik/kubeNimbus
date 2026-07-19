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
        var result = new List<ResourceDescriptor>();

        using (var coreDoc = await GetJsonDocumentAsync("api/v1", cancellationToken).ConfigureAwait(false))
        {
            result.AddRange(ParseResourceList(coreDoc.RootElement, group: ""));
        }

        using var groupsDoc = await GetJsonDocumentAsync("apis", cancellationToken).ConfigureAwait(false);
        if (!groupsDoc.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var group in groups.EnumerateArray())
        {
            var groupName = group.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = PreferredVersion(group);
            if (groupName is null || version is null)
            {
                continue;
            }

            try
            {
                using var resDoc = await GetJsonDocumentAsync($"apis/{groupName}/{version}", cancellationToken).ConfigureAwait(false);
                result.AddRange(ParseResourceList(resDoc.RootElement, groupName));
            }
            catch (HttpRequestException)
            {
                // A group can vanish between listing and querying (webhook-backed
                // aggregated APIs); skip it rather than fail the whole catalog.
            }
        }

        return result;
    }

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

    private static IEnumerable<ResourceDescriptor> ParseResourceList(JsonElement resourceList, string group)
    {
        var version = resourceList.TryGetProperty("groupVersion", out var gv)
            ? (gv.GetString() ?? "v1").Split('/').Last()
            : "v1";

        if (!resourceList.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var res in resources.EnumerateArray())
        {
            var name = res.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || name.Contains('/'))
            {
                continue; // subresource (status, scale, log, exec, ...) — not independently browsable
            }

            if (res.TryGetProperty("verbs", out var verbs) && verbs.ValueKind == JsonValueKind.Array
                && !verbs.EnumerateArray().Any(v => v.GetString() == "list"))
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
                    : []);
        }
    }
}
