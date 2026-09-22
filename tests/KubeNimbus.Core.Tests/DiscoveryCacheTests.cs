using System.Text.Json;
namespace KubeNimbus.Core.Tests;

public class DiscoveryCacheTests
{
    [Test]
    public async Task Cache_round_trips_capabilities_and_rejects_changed_version_server_expiry_and_corruption()
    {
        var directory = Directory.CreateTempSubdirectory("discovery-cache-test").FullName;
        try
        {
            var cache = new DiscoveryCache(directory);
            var pods = ResourceDescriptor.Pods with { Subresources = ["log", "exec"], Verbs = ["list", "watch"] };
            await cache.WriteAsync("server-a", "v1.31", [pods], default);
            var loaded = await cache.ReadAsync("server-a", "v1.31", default);
            await Assert.That(loaded!.Single().HasSubresource("exec")).IsTrue();
            await Assert.That(loaded!.Single().AllowsVerb("delete")).IsFalse();
            await Assert.That(await cache.ReadAsync("server-a", "v1.32", default)).IsNull();
            await Assert.That(await cache.ReadAsync("server-b", "v1.31", default)).IsNull();
            var path = Directory.GetFiles(directory).Single();
            var old = new DiscoveryCacheEntry(1, "v1.31", DateTimeOffset.UtcNow - DiscoveryCache.Lifetime - TimeSpan.FromMinutes(1), [pods]);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(old, DiscoveryCacheJson.Default.DiscoveryCacheEntry));
            await Assert.That(await cache.ReadAsync("server-a", "v1.31", default)).IsNull();
            await File.WriteAllTextAsync(path, "broken json");
            await Assert.That(await cache.ReadAsync("server-a", "v1.31", default)).IsNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Stale_aggregated_discovery_requests_legacy_fallback()
    {
        using var doc = JsonDocument.Parse("""
            {"kind":"APIGroupDiscoveryList","items":[{"metadata":{"name":"apps"},
            "versions":[{"version":"v1","freshness":"Stale","resources":[]}]}]}
            """);
        await Assert.That(ClusterClient.ParseAggregatedDiscovery(doc.RootElement)).IsNull();
    }
}
