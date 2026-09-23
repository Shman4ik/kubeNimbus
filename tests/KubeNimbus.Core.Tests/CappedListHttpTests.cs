using System.Net;
using StubApiServer = KubeNimbus.Core.Tests.ApplyPreviewHttpTests.StubApiServer;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="ClusterClient.ListResourceCappedAsync"/> over real HTTP, against the same
/// loopback stand-in the apply preview uses. What is pinned is the request, not just the
/// count: the palette's log rows list every pod in scope on each open, and a cap that
/// trimmed the answer while still paging the whole collection would bound the memory and
/// not the wait — which is the half a large cluster notices.
/// </summary>
public class CappedListHttpTests
{
    private const string PodsPath = "/api/v1/namespaces/shop/pods";

    private static string Page(int from, int count, string? next)
    {
        var items = string.Join(",", Enumerable.Range(from, count).Select(i =>
            $$$"""{"metadata":{"name":"pod-{{{i}}}","namespace":"shop"}}"""));
        var continuation = next is null ? "" : $",\"continue\":\"{next}\"";
        return $$$"""{"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"7"{{{continuation}}}},"items":[{{{items}}}]}""";
    }

    [Test]
    public async Task Paging_stops_at_the_cap_asks_for_no_more_than_is_still_wanted_and_reports_the_rest()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodsPath, HttpStatusCode.OK, Page(0, 2, next: "page-2"));
        server.Respond("GET", PodsPath, HttpStatusCode.OK, Page(2, 2, next: "page-3"));

        using var client = server.Connect();
        var result = await client.ListResourceCappedAsync(ResourceDescriptor.Pods, "shop", maxItems: 3);

        await Assert.That(result.Items.Select(p => p.Name)).IsEquivalentTo(["pod-0", "pod-1", "pod-2"]);
        await Assert.That(result.IsTruncated).IsTrue();
        await Assert.That(server.Requests.Select(r => r.Query)).IsEquivalentTo(["?limit=3", "?limit=1&continue=page-2"]);
    }

    [Test]
    public async Task A_collection_smaller_than_the_cap_is_complete_and_says_so()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodsPath, HttpStatusCode.OK, Page(0, 2, next: "page-2"));
        server.Respond("GET", PodsPath, HttpStatusCode.OK, Page(2, 1, next: null));

        using var client = server.Connect();
        var result = await client.ListResourceCappedAsync(ResourceDescriptor.Pods, "shop", maxItems: 10);

        await Assert.That(result.Items.Count).IsEqualTo(3);
        await Assert.That(result.IsTruncated).IsFalse();
        await Assert.That(server.Requests.Count).IsEqualTo(2);
    }

    /// <summary>
    /// A server may ignore <c>limit</c> (aggregated APIs often do). The cap is enforced on
    /// what came back too, and the surplus is reported as more rather than dropped silently.
    /// </summary>
    [Test]
    public async Task A_server_that_ignores_the_limit_is_still_capped()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodsPath, HttpStatusCode.OK, Page(0, 5, next: null));

        using var client = server.Connect();
        var result = await client.ListResourceCappedAsync(ResourceDescriptor.Pods, "shop", maxItems: 2);

        await Assert.That(result.Items.Count).IsEqualTo(2);
        await Assert.That(result.IsTruncated).IsTrue();
    }

    [Test]
    public async Task A_refused_list_raises_the_servers_own_403()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodsPath, HttpStatusCode.Forbidden, """
            {"kind":"Status","status":"Failure","reason":"Forbidden","code":403,
             "message":"pods is forbidden: User \"dev\" cannot list resource \"pods\" in API group \"\" in the namespace \"shop\""}
            """);

        using var client = server.Connect();
        var thrown = await Assert.ThrowsAsync<KubernetesApiException>(
            async () => await client.ListResourceCappedAsync(ResourceDescriptor.Pods, "shop", maxItems: 5));

        await Assert.That(thrown!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(thrown.ServerMessage!).StartsWith("pods is forbidden");
    }
}
