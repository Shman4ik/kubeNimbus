using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Demo;

/// <summary>
/// Fabricates a session's worth of metrics.k8s.io polls for the demo cluster.
///
/// Everything here goes in through <see cref="ResourceRowViewModel.ApplyUsage"/> and
/// <see cref="PodDetailTabViewModel.ApplyMetrics"/> — the same entry points a real
/// poll lands on, which is exactly why both take an optional sample timestamp.
/// Nothing sets chart state directly: the sparklines, the peak readouts, the window
/// caption and the "collecting…" state all have to come out of production code, or
/// what a demo (or a screenshot) shows is a second implementation of the feature.
/// </summary>
/// <remarks>
/// This used to live in the screenshot harness. It moved here with the dataset so
/// there is one of it — the harness now calls straight into this, with a fixed
/// <c>now</c> so its images stay diffable, while the running demo cluster passes the
/// real clock so its time axis reads as the last few minutes.
/// </remarks>
public static class DemoUsage
{
    /// <summary>
    /// How many poll ticks of history to build. 24 ticks at the app's real 15s cadence
    /// is six minutes — enough for a sparkline to actually have a shape, which a single
    /// stand-in sample never does.
    /// </summary>
    public const int SampleCount = 24;

    /// <summary>The app's real metrics cadence, so the window caption reads truthfully.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Replays <see cref="SampleCount"/> polls into a list row's usage history, landing
    /// exactly on <paramref name="cpu"/>/<paramref name="memory"/> on the last tick — so
    /// the sparkline has a shape while the CPU/Memory text still matches the dataset's
    /// own numbers.
    /// </summary>
    public static void Seed(ResourceRowViewModel row, int seed, long? cpu, long? memory, DateTimeOffset? now = null)
    {
        for (var tick = 0; tick < SampleCount; tick++)
        {
            var final = tick == SampleCount - 1;
            row.ApplyUsage(Ripple(cpu, seed, tick, final), Ripple(memory, seed + 5, tick, final), TickAt(tick, now));
        }
    }

    /// <summary>Records a full window of gaps — the "no reading for this object" state, which must not read as idle.</summary>
    public static void SeedGap(ResourceRowViewModel row, DateTimeOffset? now = null)
    {
        for (var tick = 0; tick < SampleCount; tick++)
        {
            row.ClearUsage(TickAt(tick, now));
        }
    }

    /// <summary>
    /// The same replay for a pod detail tab, through its real
    /// <see cref="PodDetailTabViewModel.ApplyMetrics"/> — so the per-container chips,
    /// the whole-pod charts and the window caption all come out of production code.
    /// </summary>
    public static void SeedPod(PodDetailTabViewModel detail, DateTimeOffset? now = null)
    {
        var bases = detail.Containers
            .Select((c, i) => (c.Name, Cpu: (11 + i * 23) * 1_000_000L, Memory: (64 + i * 55) * 1024L * 1024L))
            .ToArray();

        for (var tick = 0; tick < SampleCount; tick++)
        {
            var final = tick == SampleCount - 1;
            var containers = new List<ContainerMetrics>(bases.Length);
            for (var i = 0; i < bases.Length; i++)
            {
                containers.Add(new ContainerMetrics(
                    bases[i].Name,
                    Ripple(bases[i].Cpu, i, tick, final),
                    Ripple(bases[i].Memory, i + 5, tick, final)));
            }

            detail.ApplyMetrics(new PodMetrics(detail.PodNamespace, detail.PodName, containers), TickAt(tick, now));
        }
    }

    /// <summary>
    /// Pushes the dataset's PodMetrics onto whichever rows they match, seeding a
    /// history behind each. Rows with no entry record a window of gaps rather than
    /// zeroes — a pod that isn't reporting must not draw as a pod that went idle.
    /// </summary>
    public static void SeedRows(IReadOnlyList<ResourceRowViewModel> rows, DateTimeOffset? now = null)
    {
        var byKey = new Dictionary<string, (long? Cpu, long? Memory)>(StringComparer.Ordinal);
        foreach (var metrics in DemoData.PodMetrics)
        {
            byKey[metrics.Key] = SumContainerUsage(metrics);
        }

        // Nodes are cluster-scoped, so their DynamicResource key is "/<name>" — the same
        // shape ClusterTabViewModel.StartMetricsPolling builds for a real NodeMetrics.
        foreach (var node in DemoData.NodeUsage)
        {
            byKey[$"/{node.Name}"] = (node.CpuNanocores, node.MemoryBytes);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            // Fleet rows are cluster-qualified; the metrics fixtures are keyed by
            // namespace/name, so match on the object's own key.
            if (byKey.TryGetValue(row.Resource.Key, out var sample))
            {
                Seed(row, i, sample.Cpu, sample.Memory, now);
            }
            else
            {
                SeedGap(row, now);
            }
        }
    }

    private static DateTimeOffset TickAt(int tick, DateTimeOffset? now) =>
        (now ?? DateTimeOffset.UtcNow) - PollInterval * (SampleCount - 1 - tick);

    /// <summary>
    /// A deterministic 0.5–1.1× wobble around the reading. Two sines of different
    /// periods, so the series reads like a real workload rather than a textbook sine
    /// wave — and identically on every run, which is what keeps the screenshots
    /// diffable.
    /// </summary>
    private static long? Ripple(long? value, int seed, int tick, bool final)
    {
        if (value is not { } v)
        {
            return null;
        }

        if (final)
        {
            return v;
        }

        var factor = 0.8 + 0.22 * Math.Sin((tick + seed * 3) * 0.55) + 0.08 * Math.Cos((tick + seed) * 1.31);
        return (long)(v * Math.Clamp(factor, 0.35, 1.25));
    }

    private static (long? Cpu, long? Memory) SumContainerUsage(DynamicResource podMetrics)
    {
        if (!podMetrics.Raw.TryGetProperty("containers", out var containers)
            || containers.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return (null, null);
        }

        long cpu = 0, memory = 0;
        var any = false;
        foreach (var c in containers.EnumerateArray())
        {
            if (!c.TryGetProperty("usage", out var usage) || usage.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            if (usage.TryGetProperty("cpu", out var cpuEl) && Quantity.ParseCpuNanocores(cpuEl.GetString()) is { } c1)
            {
                cpu += c1;
                any = true;
            }

            if (usage.TryGetProperty("memory", out var memEl) && Quantity.ParseBytes(memEl.GetString()) is { } m1)
            {
                memory += m1;
                any = true;
            }
        }

        return any ? (cpu, memory) : (null, null);
    }
}
