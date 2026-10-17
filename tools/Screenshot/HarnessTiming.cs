using System.Diagnostics;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #347: where a harness run spends its time. Each capture is split into phases (build the
/// window, show it, run the scenario's checks, settle the log streams, the per-scenario walks,
/// render and save the PNG), and the run ends with the totals per phase, per theme and the
/// slowest captures. The numbers are what the CI shard plan in
/// <c>docs/engineering/verification.md</c> is sized from; they never change a PNG.
/// </summary>
internal static class HarnessTiming
{
    private static readonly Dictionary<string, double> PhaseTotals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> ThemeTotals = new(StringComparer.Ordinal);
    private static readonly List<(string Capture, double Seconds)> Captures = [];
    private static readonly List<string> Rows = [];
    private static readonly Stopwatch Run = Stopwatch.StartNew();

    /// <summary>Times one capture; <see cref="Phase"/> marks the end of each phase inside it.</summary>
    internal sealed class Capture(string name, string theme)
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _phase = Stopwatch.StartNew();
        private readonly List<string> _phases = [];

        internal void Phase(string phase)
        {
            var seconds = _phase.Elapsed.TotalSeconds;
            PhaseTotals[phase] = PhaseTotals.GetValueOrDefault(phase) + seconds;
            _phases.Add($"{phase}={seconds:F3}");
            _phase.Restart();
        }

        internal void End()
        {
            var seconds = _total.Elapsed.TotalSeconds;
            ThemeTotals[theme] = ThemeTotals.GetValueOrDefault(theme) + seconds;
            Captures.Add(($"{name}.{theme}", seconds));
            Rows.Add($"{name},{theme},{seconds:F3},{Run.Elapsed.TotalSeconds:F1},{string.Join(';', _phases)}");
        }
    }

    internal static void Report()
    {
        // KUBENIMBUS_HARNESS_TIMING_CSV=<file>: the whole table, one row per capture, with the run's
        // elapsed time at its end. A per-capture cost that grows along the run (elapsed rising faster
        // than the captures' own seconds) is how the window leak in HarnessTeardown was found.
        if (Environment.GetEnvironmentVariable("KUBENIMBUS_HARNESS_TIMING_CSV") is { Length: > 0 } csv)
        {
            File.WriteAllLines(csv, ["scenario,theme,seconds,elapsed,phases", .. Rows]);
        }

        Console.WriteLine();
        Console.WriteLine($"Harness timing: {Captures.Count} captures in {Run.Elapsed.TotalSeconds:F1} s");
        foreach (var (phase, seconds) in PhaseTotals.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"  phase {phase,-10} {seconds,8:F1} s");
        }

        foreach (var (theme, seconds) in ThemeTotals.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  theme {theme,-10} {seconds,8:F1} s");
        }

        Console.WriteLine("  slowest captures:");
        foreach (var (capture, seconds) in Captures.OrderByDescending(c => c.Seconds).Take(15))
        {
            Console.WriteLine($"    {seconds,6:F2} s  {capture}");
        }
    }
}
