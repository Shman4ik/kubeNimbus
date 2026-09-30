using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace KubeNimbus.Core;

/// <summary>
/// Groups a stream's items into batches of whatever has already arrived, so a consumer
/// that pays a fixed cost per hand-off — a hop onto the UI thread — pays it once per batch
/// rather than once per item.
/// </summary>
/// <remarks>
/// <para>
/// A watch's initial list arrives as one event per object, back to back. Every live list in
/// the app used to await one <c>Dispatcher.UIThread.InvokeAsync</c> per event, so 5,000 pods
/// were 5,000 serial hops, each followed by a layout of the grid it had just changed: the
/// list filled visibly slowly and the window stayed busy the whole time. With this the pump
/// keeps reading while the UI thread applies what is already queued, and a burst reaches the
/// UI in a handful of batches. A steady trickle — one Modified every few seconds — is still
/// delivered at once, as a batch of one: nothing waits for a batch to fill.
/// </para>
/// <para>
/// Order is preserved. An exception from the source is rethrown to the consumer only after
/// every item that arrived before it has been delivered, which is what a watch's "the list
/// so far stays on screen, then the error" handling relies on. The buffer between the two is
/// bounded, so a UI thread that falls behind slows the reader down (as the awaited hop used
/// to) instead of queuing a cluster's worth of events in memory.
/// </para>
/// </remarks>
public static class AsyncBatching
{
    /// <summary>Items buffered between the reader and the consumer before the reader waits.</summary>
    public const int BufferCapacity = 10_000;

    public static async IAsyncEnumerable<IReadOnlyList<T>> InBatches<T>(
        this IAsyncEnumerable<T> source,
        int maxBatch = 500,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatch, 1);

        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(BufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(stop.Token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(item, stop.Token).ConfigureAwait(false);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            while (await NextBatchAsync(channel.Reader, maxBatch, cancellationToken).ConfigureAwait(false) is { } batch)
            {
                yield return batch;
            }
        }
        finally
        {
            // The consumer stopped early (a break, a cancellation, an exception of its own):
            // stop reading the source rather than leave it pumping into a buffer nobody drains.
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for at least one item and returns every item already queued, up to
    /// <paramref name="maxBatch"/>; null once the source has ended. Rethrows the source's own
    /// exception, not the channel's wrapper, once the queue before it is drained.
    /// </summary>
    private static async ValueTask<List<T>?> NextBatchAsync<T>(ChannelReader<T> reader, int maxBatch, CancellationToken token)
    {
        bool more;
        try
        {
            more = await reader.WaitToReadAsync(token).ConfigureAwait(false);
        }
        catch (ChannelClosedException closed) when (closed.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Throw(inner);
            throw;
        }

        if (!more)
        {
            // Completed: normally, or with an exception WaitToReadAsync did not surface.
            if (reader.Completion.IsFaulted && reader.Completion.Exception?.InnerException is { } fault)
            {
                ExceptionDispatchInfo.Throw(fault);
            }

            return null;
        }

        var batch = new List<T>();
        while (batch.Count < maxBatch && reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        return batch;
    }
}
