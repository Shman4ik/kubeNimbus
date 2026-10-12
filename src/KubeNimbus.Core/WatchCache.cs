using System.Threading.Channels;

namespace KubeNimbus.Core;

/// <summary>
/// One list+watch per collection, shared by every list that reads it and kept alive for a
/// while after the last one stops: the informer cache behind the Resources list and the
/// Applications mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>An entry is a collection path</b> (<see cref="ResourceDescriptor.CollectionPath"/>):
/// <c>api/v1/pods</c> and <c>api/v1/namespaces/payments/pods</c> are two entries, and two
/// descriptors that name the same path are one. Each entry runs one source stream, keeps the
/// objects it has delivered, and hands every subscriber the same events. A subscriber that
/// joins late is given what the entry holds first, as a <c>Reset</c>, an <c>Added</c> per
/// object and a <c>Synced</c> if the first list has completed, then the live events, so it
/// cannot tell that it joined late.
/// </para>
/// <para>
/// <b>A namespace is served from the cluster-wide entry when that one is synced.</b> The
/// Applications mode watches Pods, Deployments and the rest across the cluster, so the
/// Resources list of any of them in one namespace is a filter of what is already held, with
/// no second list. Only a synced entry is used that way: a cluster-wide watch that has not yet
/// delivered its list may be one RBAC refuses, and a user granted the namespace must not see
/// the cluster's refusal.
/// </para>
/// <para>
/// <b>An idle entry is kept, and only one that synced.</b> When the last subscriber goes,
/// a synced entry keeps watching so that going back to the kind is instant and current; past
/// <see cref="IdleCapacity"/> idle entries the least recently used is stopped. An entry that
/// never synced is stopped at once: it is most often a watch that is being refused, and it
/// would retry for ever with nobody reading it.
/// </para>
/// <para>
/// <b>What is not replayed.</b> A connection warning and an unreadable object are reported to
/// the subscribers that are there when they happen; one that joins later sees the rows and
/// not the warning.
/// </para>
/// </remarks>
public sealed class WatchCache : IDisposable
{
    /// <summary>A source stream: the uncached list+watch of one collection.</summary>
    public delegate IAsyncEnumerable<ResourceEvent<DynamicResource>> Source(
        ResourceDescriptor descriptor, string? @namespace, Action<Exception> connectionLost, CancellationToken cancellationToken);

    /// <summary>How many entries nobody reads are kept watching. The Resources list's last few kinds.</summary>
    public const int DefaultIdleCapacity = 5;

    private readonly Source _source;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // Idle entries, least recently used first.
    private readonly LinkedList<Entry> _idle = new();
    private bool _disposed;

    public WatchCache(Source source, int idleCapacity = DefaultIdleCapacity)
    {
        _source = source;
        IdleCapacity = Math.Max(0, idleCapacity);
    }

    public int IdleCapacity { get; }

    /// <summary>The collection paths with a running source, for the tests.</summary>
    internal IReadOnlyList<string> RunningPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The idle collection paths, least recently used first, for the tests.</summary>
    internal IReadOnlyList<string> IdlePaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _idle.Select(e => e.Path)];
            }
        }
    }

    /// <summary>
    /// The list+watch of one collection, shared. Ends when <paramref name="cancellationToken"/>
    /// is cancelled; throws what the source threw if it fails.
    /// </summary>
    public async IAsyncEnumerable<ResourceEvent<DynamicResource>> WatchAsync(
        ResourceDescriptor descriptor,
        string? @namespace = null,
        Action<Exception>? connectionLost = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = Subscribe(descriptor, descriptor.Namespaced ? @namespace : null, connectionLost);
        try
        {
            await foreach (var evt in subscriber.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            Unsubscribe(subscriber);
        }
    }

    /// <summary>
    /// Lists the collection again: the entry's source is restarted in place, so whoever reads it
    /// sees a <c>Reset</c> and a fresh list, and nothing has to subscribe again. The Refresh
    /// command's path; a cached entry would otherwise answer it with what it already held.
    /// </summary>
    public void Relist(ResourceDescriptor descriptor, string? @namespace = null)
    {
        CancellationTokenSource? replaced = null;
        lock (_gate)
        {
            // A namespace served from the cluster-wide entry is relisted with it.
            var path = descriptor.CollectionPath(descriptor.Namespaced ? @namespace : null);
            if (_entries.TryGetValue(path, out var entry) || _entries.TryGetValue(descriptor.CollectionPath(null), out entry))
            {
                replaced = Start(entry);
            }
        }

        Cancel(replaced);
    }

    /// <summary>
    /// Stops every entry. A reconnect that replaces the connection starts from nothing; the
    /// subscribers still reading are ended, which they are being anyway.
    /// </summary>
    public void Clear()
    {
        var stopped = new List<CancellationTokenSource?>();
        var ended = new List<Subscriber>();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                stopped.Add(entry.Detach());
                ended.AddRange(entry.Subscribers);
                entry.IdleNode = null;
            }

            _entries.Clear();
            _idle.Clear();
        }

        foreach (var cts in stopped)
        {
            Cancel(cts);
        }

        foreach (var subscriber in ended)
        {
            subscriber.Channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Clear();
    }

    private Subscriber Subscribe(ResourceDescriptor descriptor, string? @namespace, Action<Exception>? connectionLost)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = descriptor.CollectionPath(@namespace);

            // A namespace of a collection already watched across the cluster and synced: a
            // filter of what that entry holds, with no list of its own.
            Entry? entry = null;
            string? filter = null;
            if (!_entries.TryGetValue(path, out entry) && @namespace is not null
                && _entries.TryGetValue(descriptor.CollectionPath(null), out var clusterWide) && clusterWide.Synced)
            {
                entry = clusterWide;
                filter = @namespace;
            }

            if (entry is null)
            {
                entry = new Entry(path, descriptor, @namespace);
                _entries[path] = entry;
                Cancel(Start(entry));
            }

            var subscriber = new Subscriber(entry, filter, connectionLost);
            if (entry.IdleNode is { } node)
            {
                _idle.Remove(node);
                entry.IdleNode = null;
            }

            // What the entry already holds, before anything live, under the same lock the
            // source's events are delivered under, so nothing is missed or seen twice.
            if (entry.HasBegun)
            {
                subscriber.Channel.Writer.TryWrite(ResourceEvent<DynamicResource>.Reset);
                foreach (var resource in entry.Objects.Values)
                {
                    if (subscriber.Accepts(resource))
                    {
                        subscriber.Channel.Writer.TryWrite(new ResourceEvent<DynamicResource>(ResourceEventType.Added, resource));
                    }
                }

                if (entry.Synced)
                {
                    subscriber.Channel.Writer.TryWrite(ResourceEvent<DynamicResource>.Synced);
                }
            }

            entry.Subscribers.Add(subscriber);
            return subscriber;
        }
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        var stopped = new List<CancellationTokenSource?>();
        lock (_gate)
        {
            var entry = subscriber.Entry;
            if (!entry.Subscribers.Remove(subscriber) || entry.Subscribers.Count > 0
                || !_entries.TryGetValue(entry.Path, out var current) || current != entry)
            {
                return;
            }

            if (!entry.Synced || IdleCapacity == 0)
            {
                _entries.Remove(entry.Path);
                stopped.Add(entry.Detach());
            }
            else
            {
                entry.IdleNode = _idle.AddLast(entry);
                while (_idle.Count > IdleCapacity && _idle.First is { } oldest)
                {
                    _idle.RemoveFirst();
                    oldest.Value.IdleNode = null;
                    _entries.Remove(oldest.Value.Path);
                    stopped.Add(oldest.Value.Detach());
                }
            }
        }

        foreach (var cts in stopped)
        {
            Cancel(cts);
        }
    }

    // Under _gate. Starts the entry's source, or restarts it in place; returns the source it
    // replaced, for the caller to cancel once the lock is released.
    private CancellationTokenSource? Start(Entry entry)
    {
        var replaced = entry.Detach();
        var cts = new CancellationTokenSource();
        entry.Cts = cts;
        var token = cts.Token;
        var generation = entry.Generation;
        _ = Task.Run(() => RunAsync(entry, generation, token), CancellationToken.None);
        return replaced;
    }

    // Outside _gate: a cancellation runs its callbacks on this thread. Not disposed: the
    // source may still be registering on its token, and a registration on a disposed source's
    // token throws. A source with no timer holds nothing a dispose would release.
    private static void Cancel(CancellationTokenSource? cts) => cts?.Cancel();

    private async Task RunAsync(Entry entry, int generation, CancellationToken token)
    {
        try
        {
            await foreach (var evt in _source(entry.Descriptor, entry.Namespace, ex => Broadcast(entry, generation, ex), token)
                .WithCancellation(token).ConfigureAwait(false))
            {
                Deliver(entry, generation, evt);
            }

            // A list+watch runs until it is cancelled; one that ends on its own has stopped
            // being a watch, and its readers must hear about it rather than wait.
            if (!token.IsCancellationRequested)
            {
                Fail(entry, generation, new InvalidOperationException("The watch ended."));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // stopped, evicted or relisted
        }
        catch (Exception ex)
        {
            Fail(entry, generation, ex);
        }
    }

    private void Deliver(Entry entry, int generation, ResourceEvent<DynamicResource> evt)
    {
        lock (_gate)
        {
            if (entry.Generation != generation)
            {
                return;
            }

            switch (evt.Type)
            {
                case ResourceEventType.Reset:
                    entry.Objects.Clear();
                    entry.Synced = false;
                    entry.HasBegun = true;
                    break;
                case ResourceEventType.Synced:
                    entry.Synced = true;
                    break;
                case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } added:
                    entry.Objects[added.Key] = added;
                    break;
                case ResourceEventType.Deleted when evt.Resource is { } deleted:
                    entry.Objects.Remove(deleted.Key);
                    break;
            }

            foreach (var subscriber in entry.Subscribers)
            {
                if (evt.Resource is null || subscriber.Accepts(evt.Resource))
                {
                    subscriber.Channel.Writer.TryWrite(evt);
                }
            }
        }
    }

    private void Broadcast(Entry entry, int generation, Exception ex)
    {
        List<Subscriber> subscribers;
        lock (_gate)
        {
            if (entry.Generation != generation)
            {
                return;
            }

            subscribers = [.. entry.Subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.ConnectionLost?.Invoke(ex);
        }
    }

    private void Fail(Entry entry, int generation, Exception ex)
    {
        List<Subscriber> subscribers;
        CancellationTokenSource? stopped;
        lock (_gate)
        {
            if (entry.Generation != generation)
            {
                return;
            }

            if (_entries.TryGetValue(entry.Path, out var current) && current == entry)
            {
                _entries.Remove(entry.Path);
            }

            if (entry.IdleNode is { } node)
            {
                _idle.Remove(node);
                entry.IdleNode = null;
            }

            subscribers = [.. entry.Subscribers];
            stopped = entry.Detach();
        }

        Cancel(stopped);
        foreach (var subscriber in subscribers)
        {
            subscriber.Channel.Writer.TryComplete(ex);
        }
    }

    private sealed class Entry(string path, ResourceDescriptor descriptor, string? @namespace)
    {
        public string Path { get; } = path;
        public ResourceDescriptor Descriptor { get; } = descriptor;
        public string? Namespace { get; } = @namespace;
        public Dictionary<string, DynamicResource> Objects { get; } = new(StringComparer.Ordinal);
        public List<Subscriber> Subscribers { get; } = [];
        public bool HasBegun { get; set; }
        public bool Synced { get; set; }
        public int Generation { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public LinkedListNode<Entry>? IdleNode { get; set; }

        // Under _gate. A source being replaced or stopped must not deliver into the entry
        // once it is gone, so the generation moves on before its token is cancelled.
        public CancellationTokenSource? Detach()
        {
            Generation++;
            var cts = Cts;
            Cts = null;
            return cts;
        }
    }

    private sealed class Subscriber(Entry entry, string? namespaceFilter, Action<Exception>? connectionLost)
    {
        public Entry Entry { get; } = entry;
        public Action<Exception>? ConnectionLost { get; } = connectionLost;

        // Unbounded: the source must never wait on a slow reader, and every reader drains
        // into its own batches (AsyncBatching) as fast as the UI takes them.
        public Channel<ResourceEvent<DynamicResource>> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<ResourceEvent<DynamicResource>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public bool Accepts(DynamicResource resource) =>
            namespaceFilter is null || string.Equals(resource.Namespace, namespaceFilter, StringComparison.Ordinal);
    }
}
