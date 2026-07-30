namespace KubeNimbus.Core;

/// <summary>One <c>metrics.k8s.io</c> reading, stamped with when it was taken.</summary>
/// <remarks>
/// Either measurement can be null: the metrics API omits a value for a container
/// that hasn't been sampled yet, and callers also record an all-null sample to
/// mark "this subject reported nothing this tick" — which is a gap in the graph,
/// not a zero.
/// </remarks>
public readonly record struct UsageSample(DateTimeOffset At, long? CpuNanocores, long? MemoryBytes);

/// <summary>
/// A fixed-size, in-memory rolling window of usage samples — the backing store
/// for the CPU/memory graphs in the pod list and pod detail.
/// </summary>
/// <remarks>
/// Deliberately small and deliberately not persisted. <c>metrics.k8s.io</c> is a
/// point-in-time aggregate with no history endpoint and no watch, so any
/// over-time view has to be built from what this session has observed since it
/// started polling; kubeNimbus keeps that honest by holding exactly one bounded
/// ring per subject and never writing it anywhere (same principle as
/// "no credentials persisted" — the app is a viewer, not a time-series store).
/// A cluster-wide history belongs in Prometheus, not here.
/// <para>
/// Lives in Core rather than the App layer because it is engine state, not view
/// state: it has no UI dependency and a future CLI would want the same window.
/// Series are handed out oldest-first as <c>double?</c> in the API's own base
/// units (nanocores, bytes) — scaling and formatting are the renderer's job.
/// </para>
/// </remarks>
public sealed class UsageHistory
{
    /// <summary>
    /// 120 samples — 30 minutes at the app's 15-second poll cadence. Long enough
    /// to show a restart or a memory ramp, short enough to stay a rounding error
    /// in memory (a few KB per subject).
    /// </summary>
    public const int DefaultCapacity = 120;

    private readonly UsageSample[] _samples;

    /// <summary>Index the next sample is written to (the ring's write head).</summary>
    private int _next;

    private int _count;

    public UsageHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new UsageSample[capacity];
    }

    public int Capacity => _samples.Length;

    /// <summary>Samples currently held — grows to <see cref="Capacity"/>, then stays there.</summary>
    public int Count => _count;

    public bool IsEmpty => _count == 0;

    /// <summary>Indexed oldest-first, so index 0 is the leftmost point on a chart.</summary>
    public UsageSample this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);

            // _next - _count is the oldest slot; it can be negative by at most
            // Capacity, so one wrap-around addition is always enough.
            return _samples[(_next - _count + index + _samples.Length) % _samples.Length];
        }
    }

    /// <summary>Most recent sample, or null when nothing has been recorded yet.</summary>
    public UsageSample? Latest => _count == 0 ? null : this[_count - 1];

    /// <summary>Wall-clock span covered by the held samples (zero with fewer than two).</summary>
    public TimeSpan Window => _count < 2 ? TimeSpan.Zero : this[_count - 1].At - this[0].At;

    public long? PeakCpuNanocores => Peak(cpu: true);

    public long? PeakMemoryBytes => Peak(cpu: false);

    public void Add(UsageSample sample)
    {
        _samples[_next] = sample;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length)
        {
            _count++;
        }
    }

    /// <summary>Records one poll's reading; pass both nulls to record a gap.</summary>
    public void Add(long? cpuNanocores, long? memoryBytes, DateTimeOffset? at = null) =>
        Add(new UsageSample(at ?? DateTimeOffset.UtcNow, cpuNanocores, memoryBytes));

    public void Clear()
    {
        Array.Clear(_samples);
        _next = 0;
        _count = 0;
    }

    /// <summary>CPU readings oldest-first, in nanocores; null entries are gaps.</summary>
    public double?[] CpuSeries() => Series(cpu: true);

    /// <summary>Memory readings oldest-first, in bytes; null entries are gaps.</summary>
    public double?[] MemorySeries() => Series(cpu: false);

    private double?[] Series(bool cpu)
    {
        var series = new double?[_count];
        for (var i = 0; i < _count; i++)
        {
            var sample = this[i];
            series[i] = (cpu ? sample.CpuNanocores : sample.MemoryBytes) is { } value ? (double?)value : null;
        }

        return series;
    }

    /// <summary>Highest reading in the window, or null when nothing reported that measure.</summary>
    private long? Peak(bool cpu)
    {
        long? peak = null;
        for (var i = 0; i < _count; i++)
        {
            var sample = this[i];
            if ((cpu ? sample.CpuNanocores : sample.MemoryBytes) is { } value && (peak is null || value > peak))
            {
                peak = value;
            }
        }

        return peak;
    }
}
