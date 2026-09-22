using System.Net;
using System.Text;

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
        private readonly string _directory;
        private readonly Task _pump;
        private int _inFlight;
        private int _maxInFlight;

        public SlowDiscoveryServer(int groupCount, int? failingGroup)
        {
            _groupCount = groupCount;
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

        public ClusterClient Connect() => ClusterClient.Connect(new ClusterContext(
            Name: "stub",
            ClusterName: "stub",
            Namespace: null,
            UserName: "stub",
            KubeconfigPath: Path.Combine(_directory, "kubeconfig.yaml")));

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
