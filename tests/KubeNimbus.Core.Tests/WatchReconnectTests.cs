using System.Collections.Concurrent;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #363: after a cut and a restore, the QA run's Service pane stayed on "Watch connection lost
/// (IOException); retrying in 16s." for minutes. The watch had failed twice in a row (the
/// second time while reconnecting, as an IOException mid-stream), and the retry after that
/// succeeded, but it resumed from the last resourceVersion on a quiet collection: no frame
/// ever arrived, and a reader clears its "connection lost" line on the next frame. The retry
/// after a lost connection now relists, so the reader is told the connection is back.
/// </summary>
public class WatchReconnectTests
{
    private const string EmptyPods = """{"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"7"},"items":[]}""";

    /// <summary>
    /// Mutation-checked: without <c>needRelist = true</c> in <c>PumpAsync</c>'s lost-connection
    /// catch, the third watch is held open with nothing to say and no frame follows the second
    /// warning, so the wait below times out.
    /// </summary>
    [Test]
    public async Task A_watch_that_fails_twice_in_a_row_tells_its_reader_when_it_is_back()
    {
        var watches = 0;
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (!request.IsWatch)
            {
                await response.WriteAsync(EmptyPods);
                return;
            }

            switch (Interlocked.Increment(ref watches))
            {
                case 1:
                    // The cut: the connection is reset before an answer.
                    response.Abort();
                    break;
                case 2:
                    // The reconnect that fails as well, mid-stream (the QA run's IOException).
                    await response.StreamThenAbortAsync();
                    break;
                default:
                    // Back, and quiet: a watch on a collection nothing is changing.
                    await response.StreamAsync(_ => Task.CompletedTask);
                    break;
            }
        });
        using var client = server.Connect();

        // What the reader sees, in order: a frame type, or "lost" for a connectionLost warning.
        var seen = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   ResourceDescriptor.Pods, "team", connectionLost: _ => seen.Enqueue("lost"),
                                   cancellationToken: stop.Token))
                {
                    seen.Enqueue(evt.Type.ToString());
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Two warnings, then the end of a list after the second one.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!(seen.Count(s => s == "lost") >= 2 && seen.Skip(LastLost(seen) + 1).Contains(nameof(ResourceEventType.Synced))))
        {
            if (DateTime.UtcNow > deadline)
            {
                await stop.CancelAsync();
                await reading;
                throw new TimeoutException(
                    $"No frame reached the reader after the second lost connection ({string.Join(", ", seen)}; {Volatile.Read(ref watches)} watches).");
            }

            await Task.Delay(50);
        }

        await stop.CancelAsync();
        await reading;

        var after = seen.Skip(LastLost(seen) + 1).ToList();
        await Assert.That(after).Contains(nameof(ResourceEventType.Synced));
        await Assert.That(Volatile.Read(ref watches)).IsGreaterThanOrEqualTo(3);
    }

    private static int LastLost(ConcurrentQueue<string> seen)
    {
        var last = -1;
        var i = 0;
        foreach (var s in seen)
        {
            if (s == "lost")
            {
                last = i;
            }

            i++;
        }

        return last;
    }
}
