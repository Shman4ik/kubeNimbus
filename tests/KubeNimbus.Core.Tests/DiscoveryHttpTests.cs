using System.Net;
using System.Text;
using System.Collections.Concurrent;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Discovery over real HTTP, against a loopback stand-in that answers every request
/// after a delay and counts how many are in flight at once.
///
/// <para>
/// Discovery is the one step between "connected" and the first row that scales with
/// the cluster rather than with the list: one request per API group, and a cluster
/// running cert-manager, Istio and Argo serves fifty or more. They used to go out one at
/// a time, so connect time was round-trip time × group count — five seconds of pure
/// queueing at 100 ms. That regression would be silent (the catalog comes out
/// identical, only later), which is why the concurrency itself is what is asserted.
/// </para>
/// </summary>
public class DiscoveryHttpTests
{
    private const int GroupCount = 40;

    [Test]
    public async Task Group_requests_go_out_concurrently_and_the_catalog_keeps_server_order()
    {
        using var server = new SlowDiscoveryServer(GroupCount, failingGroup: null);
        using var client = server.Connect();

        var catalog = await client.DiscoverResourcesAsync();

        // Core first, then every group in the order /apis listed them — the same order
        // the sequential walk produced.
        var groups = catalog.Select(d => d.Group).Distinct().ToList();
        await Assert.That(groups[0]).IsEqualTo("");
        await Assert.That(groups.Skip(1).SequenceEqual(Enumerable.Range(0, GroupCount).Select(SlowDiscoveryServer.GroupName)))
            .IsTrue();

        // Concurrent, and bounded: many at once, never all forty. (The core list can overlap the first
        // group or two even when groups are fetched one by one, hence more than a handful.)
        await Assert.That(server.MaxInFlight).IsGreaterThan(4);
        await Assert.That(server.MaxInFlight).IsLessThanOrEqualTo(ClusterClient.MaxConcurrentDiscoveryRequests + 1);
    }

    [Test]
    public async Task A_group_that_fails_is_skipped_rather_than_failing_the_catalog()
    {
        using var server = new SlowDiscoveryServer(GroupCount, failingGroup: 7);
        using var client = server.Connect();

        var catalog = await client.DiscoverResourcesAsync();

        var groups = catalog.Select(d => d.Group).Distinct().ToList();
        await Assert.That(groups).DoesNotContain(SlowDiscoveryServer.GroupName(7));
        await Assert.That(groups.Count).IsEqualTo(GroupCount); // core + 39 groups
    }

    [Test]
    public async Task Aggregated_discovery_uses_two_negotiated_requests_and_preserves_capabilities()
    {
        using var server = new SlowDiscoveryServer(0, null, aggregated: true);
        using var client = server.Connect();
        var catalog = await client.DiscoverResourcesAsync();
        await Assert.That(server.Requests.Count).IsEqualTo(2);
        await Assert.That(server.Requests.All(r => r.Accept.Contains("apidiscovery.k8s.io;v=v2"))).IsTrue();
        await Assert.That(catalog.Count).IsEqualTo(2);
        var deployment = catalog.Single(d => d.Group == "apps");
        await Assert.That(deployment.Version).IsEqualTo("v1");
        await Assert.That(deployment.HasSubresource("scale")).IsTrue();
        await Assert.That(deployment.Namespaced).IsTrue();
        await Assert.That(deployment.ShortNames.Contains("deploy")).IsTrue();
    }

    [Test]
    public async Task Warm_connections_skip_discovery_and_version_changes_invalidate_the_disk_cache()
    {
        using var server = new SlowDiscoveryServer(0, null, aggregated: true);
        using (var cold = server.Connect())
        {
            await cold.GetServerVersionAsync();
            await cold.GetResourceCatalogAsync();
        }
        await Assert.That(server.Requests.Count).IsEqualTo(3);
        using (var warm = server.Connect())
        {
            await warm.GetServerVersionAsync();
            await Assert.That((await warm.GetResourceCatalogAsync()).Count).IsEqualTo(2);
            await Assert.That(server.Requests.Count).IsEqualTo(4);
            await warm.GetResourceCatalogAsync(forceRefresh: true);
            await Assert.That(server.Requests.Count).IsEqualTo(6);
        }
        server.Version = "v1.32.0";
        using var upgraded = server.Connect();
        await upgraded.GetServerVersionAsync();
        await upgraded.GetResourceCatalogAsync();
        await Assert.That(server.Requests.Count).IsEqualTo(9);
    }

    [Test]
    public async Task Discovery_honors_cancellation()
    {
        using var server = new SlowDiscoveryServer(GroupCount, null);
        using var client = server.Connect();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var canceled = false;
        try { await client.DiscoverResourcesAsync(cts.Token); }
        catch (OperationCanceledException) { canceled = true; }
        await Assert.That(canceled).IsTrue();
    }

    /// <summary>
    /// A stand-in API server serving <c>/api/v1</c>, <c>/apis</c> and one resource list
    /// per group, each after a short delay, handling requests concurrently so that the
    /// client's own concurrency is what decides how many overlap.
    /// </summary>
    private sealed class SlowDiscoveryServer : IDisposable
    {
        private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(60);

        private readonly HttpListener _listener = new();
        private readonly int _groupCount;
        private readonly int? _failingGroup;
        private readonly bool _aggregated;
        public ConcurrentBag<(string Path, string Accept)> Requests { get; } = [];
        public string Version { get; set; } = "v1.31.0";
        private readonly string _directory;
        private readonly Task _pump;
        private int _inFlight;
        private int _maxInFlight;

        public SlowDiscoveryServer(int groupCount, int? failingGroup, bool aggregated = false)
        {
            _groupCount = groupCount;
            _aggregated = aggregated;
            _failingGroup = failingGroup;

            var port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);

            _directory = Directory.CreateTempSubdirectory("kubenimbus-stub-discovery").FullName;
            File.WriteAllText(Path.Combine(_directory, "kubeconfig.yaml"), $$"""
                apiVersion: v1
                kind: Config
                clusters:
                - name: stub
                  cluster:
                    server: http://127.0.0.1:{{port}}
                contexts:
                - name: stub
                  context:
                    cluster: stub
                    user: stub
                current-context: stub
                users:
                - name: stub
                  user:
                    # Not a credential: the stand-in never checks it.
                    token: stub-token
                """);
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public static string GroupName(int index) => $"g{index:D2}.example.io";

        public ClusterClient Connect()
        {
            var client = ClusterClient.Connect(new ClusterContext("stub", "stub", null, "stub", Path.Combine(_directory, "kubeconfig.yaml")));
            client.DiscoveryCacheDirectory = Path.Combine(_directory, "cache");
            return client;
        }

        private async Task PumpAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                // Not awaited: each request is answered on its own, so overlap is
                // limited only by what the client sends.
                _ = Task.Run(() => AnswerAsync(context));
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            Requests.Add((context.Request.Url!.AbsolutePath, context.Request.Headers["Accept"] ?? ""));
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight))
                   && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(Latency);
                var (status, body) = Route(context.Request.Url!.AbsolutePath);
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The listener closing mid-answer at the end of a test.
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private (HttpStatusCode Status, string Body) Route(string path)
        {
            if (path.TrimEnd('/') == "/version")
                return (HttpStatusCode.OK, $$"""{"major":"1","minor":"31","gitVersion":"{{Version}}"}""");
            if (_aggregated && path is "/api" or "/apis")
            {
                var core = path == "/api";
                return (HttpStatusCode.OK, $$"""
                    {"apiVersion":"apidiscovery.k8s.io/v2","kind":"APIGroupDiscoveryList","items":[
                      {"metadata":{"name":"{{(core ? "" : "apps")}}"},"versions":[
                        {"version":"v1","freshness":"Current","resources":[
                          {"resource":"{{(core ? "pods" : "deployments")}}","responseKind":{"kind":"{{(core ? "Pod" : "Deployment")}}"},
                           "scope":"Namespaced","singularResource":"{{(core ? "pod" : "deployment")}}",
                           "verbs":["get","list","watch","patch"],"shortNames":["deploy"],"categories":["all"],
                           "subresources":[{"subresource":"scale"}]}]},
                        {"version":"v1beta1","freshness":"Current","resources":[]}]}]}
                    """);
            }
            if (path == "/api/v1")
            {
                return (HttpStatusCode.OK, ResourceList("v1", "Pod", "pods"));
            }

            if (path == "/apis")
            {
                var groups = Enumerable.Range(0, _groupCount).Select(i =>
                    $$$"""{"name":"{{{GroupName(i)}}}","versions":[{"groupVersion":"{{{GroupName(i)}}}/v1","version":"v1"}],"preferredVersion":{"groupVersion":"{{{GroupName(i)}}}/v1","version":"v1"}}""");
                return (HttpStatusCode.OK, $$"""{"kind":"APIGroupList","groups":[{{string.Join(",", groups)}}]}""");
            }

            for (var i = 0; i < _groupCount; i++)
            {
                if (path == $"/apis/{GroupName(i)}/v1")
                {
                    return i == _failingGroup
                        ? (HttpStatusCode.ServiceUnavailable,
                            """{"kind":"Status","code":503,"message":"the server is currently unable to handle the request"}""")
                        : (HttpStatusCode.OK, ResourceList($"{GroupName(i)}/v1", $"Widget{i}", $"widget{i}s"));
                }
            }

            return (HttpStatusCode.NotFound, """{"kind":"Status","code":404,"message":"no stub for this request"}""");
        }

        private static string ResourceList(string groupVersion, string kind, string plural) => $$"""
            {"kind":"APIResourceList","groupVersion":"{{groupVersion}}","resources":[
              {"name":"{{plural}}","singularName":"","namespaced":true,"kind":"{{kind}}","verbs":["get","list","watch"]},
              {"name":"{{plural}}/status","singularName":"","namespaced":true,"kind":"{{kind}}","verbs":["get"]}
            ]}
            """;

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _pump.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Closing the listener faults the pending GetContextAsync; nothing to report.
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not a test failure.
            }
        }
    }
}
