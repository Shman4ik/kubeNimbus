using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the stream interleaver behind the
/// multi-cluster fleet views. What matters here isn't ordering across sources
/// (that's arrival order by design) but that nothing is dropped, that a slow
/// source can't starve a fast one, that one source failing doesn't take the
/// merged stream down with it, and that abandoning the consumer tears the pumps
/// down. All four are invisible in a screenshot and fatal in production.
/// </summary>
public class AsyncMergeTests
{
    [Test]
    public async Task Yields_every_item_from_every_source()
    {
        var merged = new List<int>();
        await foreach (var item in AsyncMerge.Merge<int>([Range(0, 3), Range(10, 3), Range(20, 3)]))
        {
            merged.Add(item);
        }

        merged.Sort();
        await Assert.That(merged.Count).IsEqualTo(9);
        await Assert.That(string.Join(",", merged)).IsEqualTo("0,1,2,10,11,12,20,21,22");
    }

    [Test]
    public async Task Returns_immediately_with_no_sources()
    {
        var count = 0;
        await foreach (var _ in AsyncMerge.Merge<int>([]))
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>
    /// The reason this exists at all: a sequential await-foreach over long-lived
    /// watch streams would never reach source 2. A source that never completes
    /// must not stop the others from being delivered.
    /// </summary>
    [Test]
    public async Task A_never_ending_source_does_not_starve_the_others()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<int>();

        try
        {
            await foreach (var item in AsyncMerge.Merge<int>([Blocking(cts.Token), Range(1, 3)], cancellationToken: cts.Token))
            {
                seen.Add(item);
                if (seen.Count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected: cancelling the merged stream is how this test ends
        }

        seen.Sort();
        await Assert.That(string.Join(",", seen)).IsEqualTo("1,2,3");
    }

    [Test]
    public async Task One_failing_source_is_reported_and_the_rest_keep_flowing()
    {
        var failures = new List<string>();
        var merged = new List<int>();

        await foreach (var item in AsyncMerge.Merge<int>(
            [Failing("cluster-b unreachable"), Range(1, 3)],
            sourceFailed: ex => failures.Add(ex.Message)))
        {
            merged.Add(item);
        }

        merged.Sort();
        await Assert.That(string.Join(",", merged)).IsEqualTo("1,2,3");
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0]).IsEqualTo("cluster-b unreachable");
    }

    /// <summary>Breaking out of the consumer must cancel the pumps, not leak them.</summary>
    [Test]
    public async Task Abandoning_the_consumer_stops_the_sources()
    {
        var stopped = new TaskCompletionSource();

        await foreach (var _ in AsyncMerge.Merge<int>([Endless(stopped)]))
        {
            break;
        }

        // The pump observes cancellation on its next move, so give it a moment;
        // a hang here means the linked token isn't reaching the sources.
        var finished = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(finished == stopped.Task).IsTrue();
    }

    private static async IAsyncEnumerable<int> Range(int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return start + i;
        }
    }

    private static async IAsyncEnumerable<int> Failing(string message)
    {
        await Task.Yield();
        throw new InvalidOperationException(message);
#pragma warning disable CS0162 // unreachable: an iterator method needs a yield to be one
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>A source that produces nothing and never completes — a watch on an idle kind.</summary>
    private static async IAsyncEnumerable<int> Blocking(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        yield break;
    }

    /// <summary>Signals when its enumeration is torn down, so cancellation can be asserted.</summary>
    private static async IAsyncEnumerable<int> Endless(TaskCompletionSource stopped)
    {
        try
        {
            while (true)
            {
                await Task.Delay(10);
                yield return 1;
            }
        }
        finally
        {
            stopped.TrySetResult();
        }
    }
}
