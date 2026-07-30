using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KubeNimbus.Core;

/// <summary>
/// Interleaves several <see cref="IAsyncEnumerable{T}"/> streams into one, in
/// arrival order. Built for the fleet (multi-cluster) views, where N clusters
/// each run their own list+watch and the UI wants a single sequence.
/// </summary>
/// <remarks>
/// Why a channel and not something clever: the sources here are long-lived watch
/// streams that each block on the network indefinitely, so a sequential
/// <c>await foreach</c> over them would starve every source but the first. One
/// pump task per source writing into an unbounded channel is the smallest thing
/// that gives every cluster an equal voice, and it is AOT-safe (no reflection,
/// no expression trees).
/// <para>
/// Failure is per-source: a cluster that drops out reports through
/// <c>sourceFailed</c> and stops contributing, while the merged stream keeps
/// running for everyone else. A fleet view that dies because one of five clusters
/// went unreachable would be strictly worse than one that says so and carries on.
/// </para>
/// </remarks>
public static class AsyncMerge
{
    public static async IAsyncEnumerable<T> Merge<T>(
        IEnumerable<IAsyncEnumerable<T>> sources,
        Action<Exception>? sourceFailed = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });

        // Linked so that abandoning the merged enumerator (the consumer breaks out,
        // or the UI switches kinds) tears down every pump, not just the read side.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        var pumps = new List<Task>();
        foreach (var source in sources)
        {
            pumps.Add(PumpAsync(source, channel.Writer, sourceFailed, token));
        }

        if (pumps.Count == 0)
        {
            channel.Writer.TryComplete();
        }
        else
        {
            // Not awaited here: completion has to happen while the consumer below is
            // still reading. CancellationToken.None because this task only waits on
            // pumps that already observe `token` themselves.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(pumps).ConfigureAwait(false);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, CancellationToken.None);
        }

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync<T>(
        IAsyncEnumerable<T> source, ChannelWriter<T> writer, Action<Exception>? sourceFailed, CancellationToken token)
    {
        try
        {
            await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
            {
                await writer.WriteAsync(item, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal teardown
        }
        catch (Exception ex)
        {
            sourceFailed?.Invoke(ex);
        }
    }
}
