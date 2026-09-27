using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// ENG-24 over real HTTP: the pods on a node are a field-selected <em>watch</em> now — for
/// the node pane and for the drain — rather than a one-shot list and a 2 s re-list loop.
/// A stand-in API server that answers requests concurrently, because a watch holds its
/// connection open while the drain goes on evicting through others.
/// </summary>
[NotInParallel(nameof(NodeWatchHttpTests))]
public class NodeWatchHttpTests
{
    private static readonly ResourceDescriptor Pods =
        new("", "v1", "Pod", "pods", "pod", Namespaced: true, ShortNames: ["po"], Categories: ["all"])
        {
            Subresources = [NodeActions.EvictionSubresource],
        };

    private static readonly ResourceDescriptor Nodes =
        new("", "v1", "Node", "nodes", "node", Namespaced: false, ShortNames: ["no"], Categories: []);

    private const string EscapedSelector = "fieldSelector=spec.nodeName%3Dworker-1";

    private static string Pod(string name, string resourceVersion) => $$$"""
        {"metadata":{"name":"{{{name}}}","namespace":"shop","uid":"uid-{{{name}}}","resourceVersion":"{{{resourceVersion}}}",
          "ownerReferences":[{"apiVersion":"apps/v1","kind":"ReplicaSet","name":"web-5d8f","uid":"rs","controller":true}]},
         "spec":{"nodeName":"worker-1","containers":[{"name":"app"}]},
         "status":{"phase":"Running"}}
        """.ReplaceLineEndings("");

    private static string PodList(string resourceVersion, params string[] items) =>
        $$"""{"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"{{resourceVersion}}"},"items":[{{string.Join(",", items)}}]}""";

    private static string Frame(string type, string pod) => $$"""{"type":"{{type}}","object":{{pod}}}""" + "\n";

    /// <summary>
    /// The watch and the list that seeds it carry the <em>same</em> escaped field selector.
    /// A selector on one half only would give the watch a different population from the
    /// list — every pod in the cluster arriving as an Added on a node pane.
    /// </summary>
    [Test]
    public async Task The_pods_on_node_watch_sends_the_node_selector_on_the_list_and_the_watch()
    {
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            if (request.Path == "/api/v1/pods" && !request.IsWatch)
            {
                await response.WriteAsync(PodList("5", Pod("web-1", "5")));
                return;
            }

            if (request.Path == "/api/v1/pods" && request.IsWatch)
            {
                await response.StreamAsync(Frame("ADDED", Pod("web-2", "6")));
                return;
            }

            await response.NotFoundAsync();
        });

        using var client = server.Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var names = new List<string>();
        await foreach (var evt in client.WatchPodsOnNodeAsync(Pods, "worker-1", cancellationToken: cts.Token))
        {
            if (evt.Resource is { } pod)
            {
                names.Add(pod.Name);
            }

            if (names.Contains("web-2"))
            {
                break;
            }
        }

        await Assert.That(string.Join(",", names)).IsEqualTo("web-1,web-2");
        var list = server.Requests.First(r => r.Path == "/api/v1/pods" && !r.IsWatch);
        var watch = server.Requests.First(r => r.Path == "/api/v1/pods" && r.IsWatch);
        await Assert.That(list.Query).Contains(EscapedSelector);
        await Assert.That(watch.Query).Contains(EscapedSelector);
        await Assert.That(watch.Query).Contains("resourceVersion=5");
    }

    /// <summary>
    /// The drain learns that an evicted pod has gone from the watch's Deleted, and
    /// finishes on it — one list, then the watch, and no second list. The loop it replaces
    /// slept 2 s and re-listed, which this test would see as a second plain GET.
    /// </summary>
    [Test]
    public async Task A_drain_observes_the_eviction_through_the_watch_instead_of_re_listing()
    {
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            switch (request.Method, request.Path, request.IsWatch)
            {
                case ("PATCH", "/api/v1/nodes/worker-1", _):
                    await response.WriteAsync("{}");
                    return;
                case ("GET", "/api/v1/pods", false):
                    await response.WriteAsync(PodList("5", Pod("web-1", "5")));
                    return;
                case ("GET", "/api/v1/pods", true):
                    // Nothing happens on the node until the eviction is accepted; then the
                    // pod goes, which is what a real kubelet reports a moment later.
                    await response.StreamAsync(async write =>
                    {
                        await evicted.Task;
                        await write(Frame("DELETED", Pod("web-1", "7")));
                    });
                    return;
                case ("POST", "/api/v1/namespaces/shop/pods/web-1/eviction", _):
                    await response.WriteAsync("{}", HttpStatusCode.Created);
                    evicted.TrySetResult();
                    return;
                default:
                    await response.NotFoundAsync();
                    return;
            }
        });

        using var client = server.Connect();
        var stages = await DrainAsync(client);

        await Assert.That(stages[^1]).IsEqualTo(DrainStage.Completed);
        await Assert.That(stages).Contains(DrainStage.PodEvicted);
        await Assert.That(server.Requests.Count(r => r is { Method: "GET", Path: "/api/v1/pods", IsWatch: false }))
            .IsEqualTo(1);
    }

    /// <summary>
    /// A PodDisruptionBudget's 429 means "ask again later", and that is the one timer the
    /// drain still has: with nothing else moving on the node, the blocked pod is asked
    /// again after the retry interval and evicted once the budget allows it.
    /// </summary>
    [Test]
    public async Task A_pod_held_by_a_disruption_budget_is_asked_again_after_the_retry_interval()
    {
        var previous = ClusterClient.EvictionRetryInterval;
        ClusterClient.EvictionRetryInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            var attempts = 0;
            var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new ConcurrentStubServer(async (request, response) =>
            {
                switch (request.Method, request.Path, request.IsWatch)
                {
                    case ("PATCH", "/api/v1/nodes/worker-1", _):
                        await response.WriteAsync("{}");
                        return;
                    case ("GET", "/api/v1/pods", false):
                        await response.WriteAsync(PodList("5", Pod("web-1", "5")));
                        return;
                    case ("GET", "/api/v1/pods", true):
                        await response.StreamAsync(async write =>
                        {
                            await evicted.Task;
                            await write(Frame("DELETED", Pod("web-1", "7")));
                        });
                        return;
                    case ("POST", "/api/v1/namespaces/shop/pods/web-1/eviction", _):
                        if (Interlocked.Increment(ref attempts) == 1)
                        {
                            await response.WriteAsync(
                                """{"kind":"Status","code":429,"message":"Cannot evict pod as it would violate the pod's disruption budget."}""",
                                HttpStatusCode.TooManyRequests);
                            return;
                        }

                        await response.WriteAsync("{}", HttpStatusCode.Created);
                        evicted.TrySetResult();
                        return;
                    default:
                        await response.NotFoundAsync();
                        return;
                }
            });

            using var client = server.Connect();
            var stages = await DrainAsync(client);

            await Assert.That(stages).Contains(DrainStage.PodBlocked);
            await Assert.That(stages[^1]).IsEqualTo(DrainStage.Completed);
            await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2);
        }
        finally
        {
            ClusterClient.EvictionRetryInterval = previous;
        }
    }

    /// <summary>
    /// A drain whose pods cannot be read at all — the common case is a 403 on listing pods
    /// cluster-wide — fails with the server's own sentence, as the one-shot list it
    /// replaced did, rather than waiting on a watch that is retrying for ever.
    /// </summary>
    [Test]
    public async Task A_drain_that_cannot_list_the_pods_fails_with_the_servers_sentence()
    {
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            if (request.Method == "PATCH")
            {
                await response.WriteAsync("{}");
                return;
            }

            await response.WriteAsync(
                """{"kind":"Status","code":403,"message":"pods is forbidden: User \"viewer\" cannot list resource \"pods\" at the cluster scope"}""",
                HttpStatusCode.Forbidden);
        });

        using var client = server.Connect();
        Exception? failure = null;
        try
        {
            await DrainAsync(client);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("cannot list resource \"pods\"");
    }

    private static async Task<List<DrainStage>> DrainAsync(ClusterClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stages = new List<DrainStage>();
        await foreach (var progress in client.DrainNodeAsync(Nodes, Pods, "worker-1", new DrainOptions(false, false), cts.Token))
        {
            stages.Add(progress.Stage);
        }

        return stages;
    }

    internal sealed record Request(string Method, string Path, string Query)
    {
        public bool IsWatch => Query.Contains("watch=true", StringComparison.Ordinal);
    }

    internal sealed class Responder(HttpListenerResponse response)
    {
        public async Task WriteAsync(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = (int)status;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        public Task NotFoundAsync() =>
            WriteAsync("""{"kind":"Status","code":404,"message":"no stub for this request"}""", HttpStatusCode.NotFound);

        public Task StreamAsync(string frames) => StreamAsync(write => write(frames));

        /// <summary>
        /// A watch: headers now, frames as <paramref name="produce"/> writes them, and the
        /// connection held open afterwards — a real watch does not end when it has nothing
        /// to say, and one that did would send the client straight back for another.
        /// </summary>
        public async Task StreamAsync(Func<Func<string, Task>, Task> produce)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.SendChunked = true;
            await response.OutputStream.FlushAsync();
            try
            {
                await produce(async text =>
                {
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                    await response.OutputStream.FlushAsync();
                });
                await Task.Delay(Timeout.Infinite, Closing);
            }
            catch (Exception)
            {
                // The client went away or the server is shutting down.
            }
        }

        public CancellationToken Closing { get; init; }
    }

    /// <summary>
    /// A loopback stand-in for an API server that answers every request on its own task.
    /// <c>ApplyPreviewHttpTests.StubApiServer</c> answers one request at a time, which a
    /// held-open watch would deadlock.
    /// </summary>
    internal sealed class ConcurrentStubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _closing = new();
        private readonly ConcurrentQueue<Request> _requests = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Func<Request, Responder, Task> _handle;
        private readonly string _directory;
        private readonly Task _pump;

        public ConcurrentStubServer(Func<Request, Responder, Task> handle)
        {
            _handle = handle;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);

            _directory = Directory.CreateTempSubdirectory("kubenimbus-stub-watch").FullName;
            File.WriteAllText(Path.Combine(_directory, "kubeconfig.yaml"), $$"""
                apiVersion: v1
                kind: Config
                clusters:
                - name: stub
                  cluster:
                    server: http://127.0.0.1:{{Port}}
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

        public int Port { get; }

        public IReadOnlyList<Request> Requests => [.. _requests];

        public ClusterClient Connect() => ClusterClient.Connect(new ClusterContext(
            Name: "stub", ClusterName: "stub", Namespace: null, UserName: "stub",
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

                var request = new Request(
                    context.Request.HttpMethod, context.Request.Url!.AbsolutePath, context.Request.Url.Query);
                _requests.Enqueue(request);
                _handlers.Add(Task.Run(async () =>
                {
                    try
                    {
                        await _handle(request, new Responder(context.Response) { Closing = _closing.Token });
                    }
                    catch (Exception)
                    {
                        // A client that hung up mid-answer; nothing to report.
                    }
                }));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _closing.CancelAsync();
            _listener.Close();
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.WhenAll(_handlers).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Shutdown path.
            }

            _closing.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
