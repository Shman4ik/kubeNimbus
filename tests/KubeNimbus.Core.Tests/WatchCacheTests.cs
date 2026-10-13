using System.Text.Json;
using System.Threading.Channels;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="WatchCache"/>: one source per collection however many lists read it, a late
/// reader given what is held, a namespace served from a synced cluster-wide watch, the idle
/// entries kept and evicted, a refused watch never kept, and Refresh relisting in place.
/// </summary>
public class WatchCacheTests
{
    private static readonly ResourceDescriptor Pods = ResourceDescriptor.Pods;

    /// <summary>A stand-in for the API server: one scripted stream per collection path, and a count of the sources started.</summary>
    private sealed class FakeSource
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Channel<ResourceEvent<DynamicResource>>> _streams = new(StringComparer.Ordinal);
        public Dictionary<string, int> Started { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Cancelled { get; } = new(StringComparer.Ordinal);

        public async IAsyncEnumerable<ResourceEvent<DynamicResource>> Watch(
            ResourceDescriptor descriptor, string? @namespace, Action<Exception> connectionLost,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var path = descriptor.CollectionPath(@namespace);
            var stream = Channel.CreateUnbounded<ResourceEvent<DynamicResource>>();
            lock (_gate)
            {
                _streams[path] = stream;
                Started[path] = Started.GetValueOrDefault(path) + 1;
            }

            try
            {
                await foreach (var evt in stream.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return evt;
                }
            }
            finally
            {
                lock (_gate)
                {
                    Cancelled[path] = Cancelled.GetValueOrDefault(path) + (cancellationToken.IsCancellationRequested ? 1 : 0);
                }
            }
        }

        public async Task<Channel<ResourceEvent<DynamicResource>>> StreamAsync(string path)
        {
            for (var i = 0; i < 200; i++)
            {
                lock (_gate)
                {
                    if (_streams.TryGetValue(path, out var stream))
                    {
                        return stream;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"No source started for {path}");
        }

        public async Task SyncAsync(string path, params DynamicResource[] objects)
        {
            var stream = await StreamAsync(path);
            stream.Writer.TryWrite(ResourceEvent<DynamicResource>.Reset);
            foreach (var o in objects)
            {
                stream.Writer.TryWrite(new ResourceEvent<DynamicResource>(ResourceEventType.Added, o));
            }

            stream.Writer.TryWrite(ResourceEvent<DynamicResource>.Synced);
        }

        public int StartedFor(string path)
        {
            lock (_gate)
            {
                return Started.GetValueOrDefault(path);
            }
        }

        public int CancelledFor(string path)
        {
            lock (_gate)
            {
                return Cancelled.GetValueOrDefault(path);
            }
        }

        public void Forget(string path)
        {
            lock (_gate)
            {
                _streams.Remove(path);
            }
        }
    }

    /// <summary>A reader of the cache that keeps what it has seen, as a list would.</summary>
    private sealed class Reader : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        private readonly Lock _gate = new();
        private readonly List<string> _events = [];

        public Reader(WatchCache cache, ResourceDescriptor descriptor, string? @namespace)
        {
            _run = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in cache.WatchAsync(descriptor, @namespace, cancellationToken: _cts.Token))
                    {
                        lock (_gate)
                        {
                            _events.Add(evt.Resource is { } r ? $"{evt.Type}:{r.Key}" : evt.Type.ToString());
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _events.Add($"Failed:{ex.Message}");
                    }
                }
            });
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public async Task<IReadOnlyList<string>> WaitForAsync(Func<IReadOnlyList<string>, bool> done)
        {
            for (var i = 0; i < 200; i++)
            {
                var events = Events;
                if (done(events))
                {
                    return events;
                }

                await Task.Delay(10);
            }

            return Events;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _run;
            _cts.Dispose();
        }
    }

    private static DynamicResource Pod(string @namespace, string name) =>
        new(JsonDocument.Parse($$$"""{"apiVersion":"v1","kind":"Pod","metadata":{"namespace":"{{{@namespace}}}","name":"{{{name}}}"}}""").RootElement.Clone());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Two_readers_of_one_collection_share_one_source_and_a_late_one_is_given_what_is_held()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        await using var first = new Reader(cache, Pods, null);
        await source.SyncAsync("api/v1/pods", Pod("payments", "api-1"), Pod("ledger", "db-0"));
        await first.WaitForAsync(e => e.Contains("Synced"));

        await using var second = new Reader(cache, Pods, null);
        var replayed = await second.WaitForAsync(e => e.Contains("Synced"));

        await Assert.That(source.StartedFor("api/v1/pods")).IsEqualTo(1);
        await Assert.That(replayed).IsEquivalentTo(["Reset", "Added:payments/api-1", "Added:ledger/db-0", "Synced"]);

        // Live events after the replay reach both.
        (await source.StreamAsync("api/v1/pods")).Writer.TryWrite(new ResourceEvent<DynamicResource>(ResourceEventType.Deleted, Pod("ledger", "db-0")));
        await Assert.That((await second.WaitForAsync(e => e.Count == 5)).Last()).IsEqualTo("Deleted:ledger/db-0");
        await Assert.That((await first.WaitForAsync(e => e.Count == 5)).Last()).IsEqualTo("Deleted:ledger/db-0");
    }

    [Test]
    public async Task A_namespace_is_served_from_a_synced_cluster_wide_watch_with_no_list_of_its_own()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        await using var cluster = new Reader(cache, Pods, null);
        await source.SyncAsync("api/v1/pods", Pod("payments", "api-1"), Pod("ledger", "db-0"));
        await cluster.WaitForAsync(e => e.Contains("Synced"));

        await using var payments = new Reader(cache, Pods, "payments");
        var events = await payments.WaitForAsync(e => e.Contains("Synced"));

        await Assert.That(events).IsEquivalentTo(["Reset", "Added:payments/api-1", "Synced"]);
        await Assert.That(source.StartedFor("api/v1/namespaces/payments/pods")).IsEqualTo(0);

        // Live events of other namespaces are not passed on.
        var stream = await source.StreamAsync("api/v1/pods");
        stream.Writer.TryWrite(new ResourceEvent<DynamicResource>(ResourceEventType.Added, Pod("ledger", "db-1")));
        stream.Writer.TryWrite(new ResourceEvent<DynamicResource>(ResourceEventType.Added, Pod("payments", "api-2")));
        await Assert.That((await payments.WaitForAsync(e => e.Count == 4)).Last()).IsEqualTo("Added:payments/api-2");
        await Assert.That(payments.Events).DoesNotContain("Added:ledger/db-1");
    }

    [Test]
    public async Task A_cluster_wide_watch_that_has_not_synced_does_not_serve_a_namespace()
    {
        // It may be one RBAC refuses; a user granted the namespace must get the namespace's own read.
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        await using var cluster = new Reader(cache, Pods, null);
        await source.StreamAsync("api/v1/pods");

        await using var payments = new Reader(cache, Pods, "payments");
        await source.SyncAsync("api/v1/namespaces/payments/pods", Pod("payments", "api-1"));

        await Assert.That(await payments.WaitForAsync(e => e.Contains("Synced"))).IsEquivalentTo(["Reset", "Added:payments/api-1", "Synced"]);
        await Assert.That(source.StartedFor("api/v1/namespaces/payments/pods")).IsEqualTo(1);
    }

    [Test]
    public async Task A_synced_collection_left_by_its_last_reader_keeps_watching_until_it_is_the_oldest_past_capacity()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch, idleCapacity: 2);
        var paths = new[] { "payments", "ledger", "checkout" }.Select(ns => Pods.CollectionPath(ns)).ToList();

        foreach (var ns in new[] { "payments", "ledger", "checkout" })
        {
            var reader = new Reader(cache, Pods, ns);
            await source.SyncAsync(Pods.CollectionPath(ns), Pod(ns, "a"));
            await reader.WaitForAsync(e => e.Contains("Synced"));
            await reader.DisposeAsync();
        }

        await WaitUntilAsync(() => cache.IdlePaths.Count == 2);
        await Assert.That(cache.IdlePaths).IsEquivalentTo([paths[1], paths[2]]);
        await WaitUntilAsync(() => source.CancelledFor(paths[0]) == 1);
        await Assert.That(source.CancelledFor(paths[0])).IsEqualTo(1);
        await Assert.That(source.CancelledFor(paths[1])).IsEqualTo(0);

        // Going back to a kept one starts nothing and is given its rows at once.
        await using var back = new Reader(cache, Pods, "ledger");
        await Assert.That(await back.WaitForAsync(e => e.Contains("Synced"))).IsEquivalentTo(["Reset", "Added:ledger/a", "Synced"]);
        await Assert.That(source.StartedFor(paths[1])).IsEqualTo(1);
        await Assert.That(cache.IdlePaths).IsEquivalentTo([paths[2]]);
    }

    [Test]
    public async Task A_collection_that_never_synced_is_stopped_when_its_last_reader_goes()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        var reader = new Reader(cache, Pods, null);
        await source.StreamAsync("api/v1/pods");
        await reader.DisposeAsync();

        await WaitUntilAsync(() => source.CancelledFor("api/v1/pods") == 1);
        await Assert.That(source.CancelledFor("api/v1/pods")).IsEqualTo(1);
        await Assert.That(cache.RunningPaths).IsEmpty();
        await Assert.That(cache.IdlePaths).IsEmpty();
    }

    [Test]
    public async Task Relist_restarts_the_source_in_place_and_the_reader_sees_a_fresh_list()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        await using var reader = new Reader(cache, Pods, "payments");
        await source.SyncAsync("api/v1/namespaces/payments/pods", Pod("payments", "old"));
        await reader.WaitForAsync(e => e.Contains("Synced"));

        source.Forget("api/v1/namespaces/payments/pods");
        cache.Relist(Pods, "payments");
        await source.SyncAsync("api/v1/namespaces/payments/pods", Pod("payments", "new"));

        var events = await reader.WaitForAsync(e => e.Count(x => x == "Synced") == 2);
        await Assert.That(events).IsEquivalentTo(["Reset", "Added:payments/old", "Synced", "Reset", "Added:payments/new", "Synced"]);
        await Assert.That(source.StartedFor("api/v1/namespaces/payments/pods")).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_source_ends_its_readers_with_the_failure_and_is_not_kept()
    {
        var source = new FakeSource();
        using var cache = new WatchCache(source.Watch);
        await using var reader = new Reader(cache, Pods, null);
        var stream = await source.StreamAsync("api/v1/pods");
        stream.Writer.TryComplete(new HttpRequestException("forbidden"));

        await Assert.That((await reader.WaitForAsync(e => e.Count > 0)).Single()).IsEqualTo("Failed:forbidden");
        await Assert.That(cache.RunningPaths).IsEmpty();
    }

    /// <summary>
    /// #339: a source that reports a 403 and would retry it for ever (what
    /// <c>ClusterClient</c>'s list+watch does) is stopped at the first report, its readers end
    /// with the server's sentence instead of a "connection lost" warning, and nothing is kept,
    /// so the next reader asks again. Mutation-checked: without the refusal branch in
    /// <c>Broadcast</c> the reader never ends and the source keeps reporting.
    /// </summary>
    [Test]
    public async Task A_refused_source_is_stopped_at_its_first_report_and_its_readers_end_with_the_refusal()
    {
        const string Sentence = "applications.argoproj.io is forbidden: User \"narrow\" cannot list resource \"applications\"";
        var starts = 0;
        var reports = 0;
        var cancelled = 0;
        async IAsyncEnumerable<ResourceEvent<DynamicResource>> Refusing(
            ResourceDescriptor descriptor, string? @namespace, Action<Exception> connectionLost,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref starts);
            while (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref reports);
                connectionLost(new WatchConnectionException(
                    "Watch connection lost (forbidden); retrying in 1s.",
                    new HttpRequestException(Sentence, null, System.Net.HttpStatusCode.Forbidden)));
                try
                {
                    await Task.Delay(20, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                }
            }

            yield break;
        }

        using var cache = new WatchCache(Refusing);
        var warnings = new List<Exception>();
        var read = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in cache.WatchAsync(Pods, connectionLost: warnings.Add))
                {
                }
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        });

        var ended = await read.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ended).IsTypeOf<WatchRefusedException>();
        await Assert.That(ended!.Message).IsEqualTo(Sentence);
        await Assert.That(warnings).IsEmpty();
        await WaitUntilAsync(() => Volatile.Read(ref cancelled) == 1);
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref cancelled)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref reports)).IsEqualTo(1);
        await Assert.That(cache.RunningPaths).IsEmpty();

        // Asking again is a new attempt, not the old refusal replayed.
        await using var again = new Reader(cache, Pods, null);
        await again.WaitForAsync(e => e.Count > 0);
        await Assert.That(Volatile.Read(ref starts)).IsEqualTo(2);
    }

    /// <summary>
    /// #339 over real HTTP: a <see cref="ClusterClient"/> whose list is refused (403) used to
    /// retry it every backoff behind "Watch connection lost". Through its shared watches the
    /// stream now ends with the server's sentence after one request, with no "connection lost".
    /// </summary>
    [Test]
    public async Task A_real_clients_refused_list_ends_its_shared_watch_after_one_request()
    {
        const string Sentence = "pods is forbidden: User \"viewer\" cannot list resource \"pods\" in API group \"\" at the cluster scope";
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
            await response.WriteAsync(
                $$"""{"kind":"Status","code":403,"reason":"Forbidden","message":{{JsonSerializer.Serialize(Sentence)}}}""",
                System.Net.HttpStatusCode.Forbidden));
        using var client = server.Connect();
        var warnings = new List<Exception>();

        Exception? ended = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var _ in client.Watches.WatchAsync(Pods, connectionLost: warnings.Add, cancellationToken: timeout.Token))
            {
            }
        }
        catch (Exception ex)
        {
            ended = ex;
        }

        await Assert.That(ended).IsTypeOf<WatchRefusedException>();
        await Assert.That(ended!.Message).IsEqualTo(Sentence);
        await Assert.That(warnings).IsEmpty();

        // The first retry would have come after a one-second backoff.
        await Task.Delay(1500);
        await Assert.That(server.Requests.Count(r => r.Path == "/api/v1/pods")).IsEqualTo(1);
    }

    [Test]
    public async Task WatchRefusedException_finds_a_403_however_it_is_wrapped_and_nothing_else()
    {
        var bare = new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
        await Assert.That(WatchRefusedException.From(new WatchConnectionException("lost", bare))!.Message).IsEqualTo("forbidden");
        await Assert.That(WatchRefusedException.From(bare)).IsNotNull();
        await Assert.That(WatchRefusedException.From(new WatchConnectionException("lost",
            new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable)))).IsNull();
        await Assert.That(WatchRefusedException.From(new WatchConnectionException("lost", new IOException("reset")))).IsNull();
    }
}
