using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// ENG-8: what a resource's <c>verbs</c> say about whether it belongs in the sidebar.
/// An empty array and a missing property are opposite answers — "supports nothing" and
/// "did not say" — and both parses (aggregated discovery and the per-group fallback) have
/// to read them the same way. They used not to: the aggregated parse dropped a resource
/// with no <c>verbs</c>, and the per-group parse, after FEAT-1's two-pass rewrite, kept a
/// resource that reported <c>"verbs": []</c>. See <c>ClusterClient.IsListable</c>.
/// </summary>
public class DiscoveryVerbsTests
{
    /// <summary>Four resources, one per shape, in the legacy APIResourceList form.</summary>
    private const string ResourceList = """
        {
          "groupVersion": "example.io/v1",
          "resources": [
            { "name": "listables", "kind": "Listable", "namespaced": true, "verbs": ["get", "list", "watch"] },
            { "name": "createonlies", "kind": "CreateOnly", "namespaced": true, "verbs": ["create"] },
            { "name": "nothings", "kind": "Nothing", "namespaced": true, "verbs": [] },
            { "name": "unstateds", "kind": "Unstated", "namespaced": true }
          ]
        }
        """;

    /// <summary>The same four, in the aggregated (apidiscovery.k8s.io/v2) form.</summary>
    private const string Aggregated = """
        {
          "kind": "APIGroupDiscoveryList",
          "items": [ {
            "metadata": { "name": "example.io" },
            "versions": [ {
              "version": "v1",
              "freshness": "Current",
              "resources": [
                { "resource": "listables", "responseKind": { "kind": "Listable" }, "scope": "Namespaced",
                  "verbs": ["get", "list", "watch"] },
                { "resource": "createonlies", "responseKind": { "kind": "CreateOnly" }, "scope": "Namespaced",
                  "verbs": ["create"] },
                { "resource": "nothings", "responseKind": { "kind": "Nothing" }, "scope": "Namespaced",
                  "verbs": [] },
                { "resource": "unstateds", "responseKind": { "kind": "Unstated" }, "scope": "Namespaced" }
              ]
            } ]
          } ]
        }
        """;

    private static string Kinds(IEnumerable<ResourceDescriptor> descriptors) =>
        string.Join(", ", descriptors.Select(d => d.Kind).Order(StringComparer.Ordinal));

    [Test]
    public async Task Per_group_discovery_drops_an_empty_verbs_array_and_keeps_a_missing_one()
    {
        using var doc = JsonDocument.Parse(ResourceList);

        var kinds = Kinds(ClusterClient.ParseResourceList(doc.RootElement, "example.io").ToList());

        // "Nothing" said it supports nothing; "CreateOnly" said it cannot be listed.
        // "Unstated" said nothing at all, which is not a no.
        await Assert.That(kinds).IsEqualTo("Listable, Unstated");
    }

    [Test]
    public async Task Aggregated_discovery_reads_the_three_shapes_the_same_way()
    {
        using var doc = JsonDocument.Parse(Aggregated);

        var parsed = ClusterClient.ParseAggregatedDiscovery(doc.RootElement);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(Kinds(parsed!)).IsEqualTo("Listable, Unstated");
    }

    /// <summary>
    /// A kept resource whose verbs were not stated carries an empty <c>Verbs</c> — the
    /// "unknown" every capability check offers and lets the server answer — rather than a
    /// guessed list.
    /// </summary>
    [Test]
    public async Task A_resource_with_no_stated_verbs_is_kept_with_unknown_capabilities()
    {
        using var doc = JsonDocument.Parse(ResourceList);

        var unstated = ClusterClient.ParseResourceList(doc.RootElement, "example.io").Single(d => d.Kind == "Unstated");

        await Assert.That(unstated.Verbs.Count).IsEqualTo(0);
        await Assert.That(unstated.AllowsVerb("patch")).IsTrue();
    }
}
