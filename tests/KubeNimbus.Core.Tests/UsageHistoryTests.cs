using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the rolling usage window behind the
/// CPU/memory graphs. The ring-buffer indexing and the gap handling are the two
/// things that fail silently in the UI — a wrong wrap-around draws a plausible
/// but wrong chart, and a dropped gap makes "stopped reporting" look like "idle"
/// — so they're pinned here rather than left to a visual check.
/// </summary>
public class UsageHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Starts_empty()
    {
        var history = new UsageHistory(4);

        await Assert.That(history.Count).IsEqualTo(0);
        await Assert.That(history.IsEmpty).IsTrue();
        await Assert.That(history.Latest.HasValue).IsFalse();
        await Assert.That(history.Window).IsEqualTo(TimeSpan.Zero);
        await Assert.That(history.PeakCpuNanocores).IsNull();
        await Assert.That(history.CpuSeries().Length).IsEqualTo(0);
    }

    [Test]
    public async Task Indexes_oldest_first()
    {
        var history = new UsageHistory(4);
        for (var i = 0; i < 3; i++)
        {
            history.Add(i, i * 10, T0.AddSeconds(i * 15));
        }

        await Assert.That(history.Count).IsEqualTo(3);
        await Assert.That(history[0].CpuNanocores).IsEqualTo(0L);
        await Assert.That(history[2].CpuNanocores).IsEqualTo(2L);
        await Assert.That(history.Latest!.Value.MemoryBytes).IsEqualTo(20L);
        await Assert.That(history.Window).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    /// <summary>The wrap-around case: past capacity the oldest samples must fall off, in order.</summary>
    [Test]
    public async Task Evicts_oldest_samples_past_capacity()
    {
        var history = new UsageHistory(3);
        for (var i = 0; i < 7; i++)
        {
            history.Add(i, null, T0.AddSeconds(i));
        }

        await Assert.That(history.Count).IsEqualTo(3);
        await Assert.That(history.Capacity).IsEqualTo(3);
        await AssertSeries(history.CpuSeries(), 4, 5, 6);
        await Assert.That(history.Window).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Keeps_gaps_as_nulls_rather_than_zeros()
    {
        var history = new UsageHistory(4);
        history.Add(100, 200, T0);
        history.Add(null, null, T0.AddSeconds(15));
        history.Add(300, null, T0.AddSeconds(30));

        await AssertSeries(history.CpuSeries(), 100, null, 300);
        await AssertSeries(history.MemorySeries(), 200, null, null);
    }

    [Test]
    public async Task Peaks_ignore_missing_readings()
    {
        var history = new UsageHistory(8);
        history.Add(10, null, T0);
        history.Add(null, 4096, T0.AddSeconds(15));
        history.Add(7, 1024, T0.AddSeconds(30));

        await Assert.That(history.PeakCpuNanocores).IsEqualTo(10L);
        await Assert.That(history.PeakMemoryBytes).IsEqualTo(4096L);
    }

    [Test]
    public async Task Peak_is_null_when_nothing_reported_that_measure()
    {
        var history = new UsageHistory(4);
        history.Add(null, 512, T0);

        await Assert.That(history.PeakCpuNanocores).IsNull();
        await Assert.That(history.PeakMemoryBytes).IsEqualTo(512L);
    }

    [Test]
    public async Task Clear_resets_the_window()
    {
        var history = new UsageHistory(4);
        history.Add(1, 1, T0);
        history.Add(2, 2, T0.AddSeconds(15));

        history.Clear();

        await Assert.That(history.IsEmpty).IsTrue();
        await Assert.That(history.Latest.HasValue).IsFalse();
        await Assert.That(history.CpuSeries().Length).IsEqualTo(0);
    }

    /// <summary>Adding after a Clear must reuse the ring from the start, not from the old write head.</summary>
    [Test]
    public async Task Reuses_the_ring_after_clear()
    {
        var history = new UsageHistory(3);
        history.Add(1, 1, T0);
        history.Add(2, 2, T0.AddSeconds(1));
        history.Clear();
        history.Add(9, 9, T0.AddSeconds(2));

        await Assert.That(history.Count).IsEqualTo(1);
        await AssertSeries(history.CpuSeries(), 9);
    }

    [Test]
    public async Task Window_is_zero_with_a_single_sample()
    {
        var history = new UsageHistory(4);
        history.Add(1, 1, T0);

        await Assert.That(history.Window).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Rejects_a_non_positive_capacity()
    {
        await Assert.That(Threw(() => _ = new UsageHistory(0))).IsTrue();
    }

    [Test]
    public async Task Rejects_an_out_of_range_index()
    {
        var history = new UsageHistory(4);
        history.Add(1, 1, T0);

        await Assert.That(Threw(() => _ = history[1])).IsTrue();
        await Assert.That(Threw(() => _ = history[-1])).IsTrue();
    }

    /// <summary>Element-wise so a length mismatch and a value mismatch report differently.</summary>
    private static async Task AssertSeries(double?[] actual, params double?[] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }

    private static bool Threw(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
