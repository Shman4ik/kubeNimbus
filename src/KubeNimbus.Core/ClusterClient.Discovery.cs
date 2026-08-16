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
