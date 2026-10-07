using System.Net;
using System.Text.Json;
using static KubeNimbus.Core.Tests.NodeWatchHttpTests;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A name or namespace that came from another object — an owner reference, an Event's
/// involved object, an env var's ConfigMap — never builds a path the URI resolver rewrites.
/// <see cref="Uri.EscapeDataString"/> leaves <c>.</c> alone, and <c>new Uri(base, relative)</c>
/// collapses dot segments, so <c>…/pods/..</c> used to become <c>…/namespaces/a/</c>.
/// </summary>
public class PathSegmentTests
{
    private static readonly ResourceDescriptor Deployments = new(
        "apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: [], Categories: []);

    private static readonly ResourceDescriptor Nodes = new(
        "", "v1", "Node", "nodes", "node", Namespaced: false, ShortNames: [], Categories: []);

    [Test]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("a/b")]
    [Arguments("%2e%2e")]
    [Arguments("web%2Fx")]
    public async Task A_name_that_cannot_be_a_path_segment_is_refused(string name)
    {
        await Assert.That(ResourceDescriptor.IsValidPathSegment(name)).IsFalse();
        await Assert.That(() => ResourceDescriptor.Pods.ItemPath("shop", name)).Throws<ArgumentException>();
        await Assert.That(() => Deployments.SubresourcePath("shop", name, "scale")).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("a/b")]
    public async Task A_namespace_that_cannot_be_a_path_segment_is_refused_rather_than_made_cluster_wide(string ns)
    {
        await Assert.That(() => ResourceDescriptor.Pods.CollectionPath(ns)).Throws<ArgumentException>();
        await Assert.That(() => ResourceDescriptor.Pods.ItemPath(ns, "web")).Throws<ArgumentException>();
    }

    [Test]
    public async Task What_the_resolver_would_have_made_of_a_dot_segment_is_what_the_guard_prevents()
    {
        // The finding itself, pinned: this is the collapse the guard exists for.
        var collapsed = new Uri(new Uri("https://h:6443/"), $"api/v1/namespaces/a/pods/{Uri.EscapeDataString("..")}");
        await Assert.That(collapsed.AbsolutePath).IsEqualTo("/api/v1/namespaces/a/");

        // A real name keeps its whole path, escaped.
        var path = ResourceDescriptor.Pods.ItemPath("shop", "web-1 x?y");
        await Assert.That(new Uri(new Uri("https://h:6443/"), path).AbsolutePath)
            .IsEqualTo("/api/v1/namespaces/shop/pods/web-1%20x%3Fy");
    }

    [Test]
    public async Task A_cluster_scoped_kind_ignores_the_namespace_and_null_still_means_every_namespace()
    {
        await Assert.That(Nodes.ItemPath("..", "worker-1")).IsEqualTo("api/v1/nodes/worker-1");
        await Assert.That(ResourceDescriptor.Pods.CollectionPath(null)).IsEqualTo("api/v1/pods");
    }

    [Test]
    public async Task Discovery_drops_a_group_version_or_plural_that_would_break_a_path()
    {
        using var aggregated = JsonDocument.Parse("""
            {"apiVersion":"apidiscovery.k8s.io/v2","kind":"APIGroupDiscoveryList","items":[
              {"metadata":{"name":"good.example.io"},"versions":[{"version":"v1","freshness":"Current","resources":[
                {"resource":"widgets","responseKind":{"kind":"Widget"},"scope":"Namespaced","verbs":["list"]},
                {"resource":"..","responseKind":{"kind":"Dots"},"scope":"Namespaced","verbs":["list"]},
                {"resource":"a%2fb","responseKind":{"kind":"Escaped"},"scope":"Namespaced","verbs":["list"]}]}]},
              {"metadata":{"name":"../../api"},"versions":[{"version":"v1","freshness":"Current","resources":[
                {"resource":"secrets","responseKind":{"kind":"Secret"},"scope":"Namespaced","verbs":["list"]}]}]},
              {"metadata":{"name":"bad-version.example.io"},"versions":[{"version":"..","freshness":"Current","resources":[
                {"resource":"gadgets","responseKind":{"kind":"Gadget"},"scope":"Namespaced","verbs":["list"]}]}]}]}
            """);
        var catalog = ClusterClient.ParseAggregatedDiscovery(aggregated.RootElement)!;
        await Assert.That(catalog.Select(d => d.Kind)).IsEquivalentTo(["Widget"]);

        using var legacy = JsonDocument.Parse("""
            {"kind":"APIResourceList","groupVersion":"example.io/v1","resources":[
              {"name":"widgets","kind":"Widget","namespaced":true,"verbs":["list"]},
              {"name":"..","kind":"Dots","namespaced":true,"verbs":["list"]}]}
            """);
        await Assert.That(ClusterClient.ParseResourceList(legacy.RootElement, "example.io").Select(d => d.Kind).ToList())
            .IsEquivalentTo(["Widget"]);
        await Assert.That(ClusterClient.ParseResourceList(legacy.RootElement, "../x").Any()).IsFalse();
    }

    [Test]
    public async Task A_reference_matches_only_the_object_it_names()
    {
        var pod = new DynamicResource(JsonDocument.Parse(
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"web-1","namespace":"shop","uid":"u-1"}}""").RootElement.Clone());
        var list = new DynamicResource(JsonDocument.Parse(
            """{"apiVersion":"v1","kind":"PodList","metadata":{"resourceVersion":"5"},"items":[]}""").RootElement.Clone());

        await Assert.That(new OwnerRef("v1", "Pod", "web-1", "u-1", false).IsIdentityOf(pod)).IsTrue();
        await Assert.That(new OwnerRef("v1", "Pod", "web-1", null, false).IsIdentityOf(pod)).IsTrue();
        await Assert.That(new OwnerRef("v1", "Pod", "web-1", "u-2", false).IsIdentityOf(pod)).IsFalse();
        await Assert.That(new OwnerRef("apps/v1", "Pod", "web-1", null, false).IsIdentityOf(pod)).IsFalse();
        await Assert.That(new OwnerRef("v1", "Pod", "", null, false).IsIdentityOf(list)).IsFalse();

        // The kubelet's own node events carry the node's name as its UID.
        var node = new DynamicResource(JsonDocument.Parse(
            """{"apiVersion":"v1","kind":"Node","metadata":{"name":"worker-1","uid":"real-uid"}}""").RootElement.Clone());
        await Assert.That(new OwnerRef("v1", "Node", "worker-1", "worker-1", false).IsIdentityOf(node)).IsTrue();
    }

    private const string Discovery = """
        {"apiVersion":"apidiscovery.k8s.io/v2","kind":"APIGroupDiscoveryList","items":[
          {"metadata":{"name":"%GROUP%"},"versions":[{"version":"v1","freshness":"Current","resources":[%RESOURCES%]}]}]}
        """;

    private static Task DiscoveryAsync(Request request, Responder response) =>
        response.WriteAsync(request.Path == "/api"
            ? Discovery.Replace("%GROUP%", "").Replace("%RESOURCES%",
                """{"resource":"pods","responseKind":{"kind":"Pod"},"scope":"Namespaced","verbs":["get","list"]}""")
            : Discovery.Replace("%GROUP%", "apps").Replace("%RESOURCES%",
                """{"resource":"deployments","responseKind":{"kind":"Deployment"},"scope":"Namespaced","verbs":["get","list"]}"""));

    [Test]
    public async Task A_dot_segment_owner_reference_sends_no_request_and_resolves_to_nothing()
    {
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            if (request.Path is "/api" or "/apis")
            {
                await DiscoveryAsync(request, response);
                return;
            }

            // What the collapsed path would have reached: a list, which used to open as an object.
            await response.WriteAsync("""{"apiVersion":"v1","kind":"PodList","metadata":{},"items":[]}""");
        });
        using var client = server.Connect();

        var resolved = await client.ResolveOwnerAsync(new OwnerRef("v1", "Pod", "..", null, false), "shop");
        var read = await client.ReadResourceAsync(ResourceDescriptor.Pods, "..", "web-1");

        await Assert.That(resolved).IsNull();
        await Assert.That(read).IsNull();
        await Assert.That(server.Requests.Any(r => r.Path.StartsWith("/api/v1/namespaces", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task An_owner_the_server_answers_with_a_different_object_resolves_to_nothing()
    {
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            switch (request.Path)
            {
                case "/api" or "/apis":
                    await DiscoveryAsync(request, response);
                    return;
                case "/apis/apps/v1/namespaces/shop/deployments/web":
                    await response.WriteAsync(
                        """{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"web","namespace":"shop","uid":"new-uid"}}""");
                    return;
                case "/apis/apps/v1/namespaces/shop/deployments/api":
                    // A proxy or aggregated server that answers with something else entirely.
                    await response.WriteAsync(
                        """{"apiVersion":"v1","kind":"Secret","metadata":{"name":"db","namespace":"shop"}}""");
                    return;
                default:
                    await response.NotFoundAsync();
                    return;
            }
        });
        using var client = server.Connect();

        var same = await client.ResolveOwnerAsync(new OwnerRef("apps/v1", "Deployment", "web", "new-uid", true), "shop");
        var replaced = await client.ResolveOwnerAsync(new OwnerRef("apps/v1", "Deployment", "web", "old-uid", true), "shop");
        var other = await client.ResolveOwnerAsync(new OwnerRef("apps/v1", "Deployment", "api", null, true), "shop");

        await Assert.That(same?.Name).IsEqualTo("web");
        await Assert.That(replaced).IsNull();
        await Assert.That(other).IsNull();
    }

    [Test]
    public async Task Every_write_refuses_an_empty_or_dot_segment_name_before_sending_anything()
    {
        await using var server = new ConcurrentStubServer((_, response) =>
            response.WriteAsync("""{"apiVersion":"v1","kind":"Status","status":"Success"}"""));
        using var client = server.Connect();
        var jobs = new ResourceDescriptor("batch", "v1", "Job", "jobs", "job", true, [], []);
        var cronJobs = new ResourceDescriptor("batch", "v1", "CronJob", "cronjobs", "cronjob", true, [], []);

        foreach (var name in (string[])["", ".", ".."])
        {
            await Assert.That(async () => { await client.DeleteResourceAsync(Deployments, "shop", name); }).Throws<ArgumentException>();
            await Assert.That(async () => { await client.ApplyYamlAsync(Deployments, "shop", name, "kind: Deployment", "kubenimbus"); })
                .Throws<ArgumentException>();
            await Assert.That(async () => { await client.ScaleAsync(Deployments, "shop", name, 2); }).Throws<ArgumentException>();
            await Assert.That(async () => { await client.RestartWorkloadAsync(Deployments, "shop", name); }).Throws<ArgumentException>();
            await Assert.That(async () => { await client.SetCronJobSuspendedAsync(cronJobs, "shop", name, true); }).Throws<ArgumentException>();
            await Assert.That(async () => { await client.CreateJobFromCronJobAsync(cronJobs, jobs, "shop", name); }).Throws<ArgumentException>();
        }

        await Assert.That(server.Requests.Count).IsEqualTo(0);
    }
}
