using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="AsyncBatching.InBatches"/>, which every live list now reads its watch through:
/// one hop onto the UI thread per batch of arrived events instead of one per event. What a
/// watch consumer relies on is pinned here — order, grouping of what is already queued, no
/// waiting for a batch to fill, and the source's error arriving after the items before it.
/// </summary>
public class AsyncBatchingTests
{
    private static async IAsyncEnumerable<int> FromChannel(ChannelReader<int> reader, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var item in reader.ReadAllAsync(token))
        {
            yield return item;
        }
    }

    [Test]
    public async Task Everything_already_queued_arrives_as_one_batch_in_order()
    {
        // The pump starts with the first read and runs ahead of the consumer, so the first
        // batch is whatever had arrived by then, which can be a single item. What is pinned is
        // the rest: once the source has been read to its end, everything still queued comes in
        // one batch. A fixed delay for this failed on a loaded CI runner (32 batches instead of
        // two); the source now says when it has been read to its end. That is signalled when
        // the pump asks for the item after the last one, which is after the last item was
        // written to the buffer.
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<int> Source()
        {
            for (var i = 0; i < 1_000; i++)
            {
                yield return i;
            }

            queued.SetResult();
            await Task.CompletedTask;
        }

        await using var batches = Source().InBatches(maxBatch: 10_000).GetAsyncEnumerator();
        var received = new List<IReadOnlyList<int>>();
        await Assert.That(await batches.MoveNextAsync()).IsTrue();
        received.Add(batches.Current);

        await queued.Task.WaitAsync(TimeSpan.FromSeconds(30));
        while (await batches.MoveNextAsync())
        {
            received.Add(batches.Current);
        }

        await Assert.That(received.SelectMany(b => b).SequenceEqual(Enumerable.Range(0, 1_000))).IsTrue();
        await Assert.That(received.Count).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task A_batch_never_exceeds_its_limit()
    {
        var batches = new List<IReadOnlyList<int>>();
        await foreach (var batch in Enumerable.Range(0, 1_234).ToAsyncEnumerable().InBatches(maxBatch: 100))
        {
            batches.Add(batch);
        }

        await Assert.That(batches.All(b => b.Count is > 0 and <= 100)).IsTrue();
        await Assert.That(batches.Sum(b => b.Count)).IsEqualTo(1_234);
    }

    [Test]
    public async Task A_lone_item_is_delivered_without_waiting_for_more()
    {
        var channel = Channel.CreateUnbounded<int>();
        await using var batches = FromChannel(channel.Reader).InBatches().GetAsyncEnumerator();

        channel.Writer.TryWrite(42);
        var moved = batches.MoveNextAsync().AsTask();
        var first = await Task.WhenAny(moved, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.That(first).IsSameReferenceAs(moved);
        await Assert.That(batches.Current).IsEquivalentTo([42]);
        channel.Writer.Complete();
    }

    [Test]
    public async Task The_sources_error_arrives_after_every_item_before_it()
    {
        static async IAsyncEnumerable<int> Failing()
        {
            for (var i = 0; i < 50; i++)
            {
                yield return i;
            }

            await Task.Yield();
            throw new InvalidOperationException("watch ended");
        }

        var seen = new List<int>();
        Exception? caught = null;
        try
        {
            await foreach (var batch in Failing().InBatches())
            {
                seen.AddRange(batch);
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(seen.Count).IsEqualTo(50);
        await Assert.That(caught).IsTypeOf<InvalidOperationException>();
        await Assert.That(caught!.Message).IsEqualTo("watch ended");
    }

    [Test]
    public async Task Cancelling_stops_the_source()
    {
        var stopped = new TaskCompletionSource();

        async IAsyncEnumerable<int> Endless([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                var i = 0;
                while (true)
                {
                    await Task.Delay(1, token);
                    yield return i++;
                }
            }
            finally
            {
                stopped.TrySetResult();
            }
        }

        using var cts = new CancellationTokenSource();
        var threw = false;
        try
        {
            await foreach (var _ in Endless().InBatches(cancellationToken: cts.Token))
            {
                await cts.CancelAsync();
            }
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5)))).IsSameReferenceAs(stopped.Task);
    }
}
