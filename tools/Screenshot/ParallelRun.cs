using System.Diagnostics;

namespace KubeNimbus.Screenshot;

/// <summary>
/// <c>--jobs=N</c>: a local full run split across N processes of this harness, each rendering
/// one shard (<see cref="ShardPlan"/>) into the same output directory. It is the CI shard matrix
/// on one machine, so it guarantees what the matrix does and nothing less: every scenario is
/// rendered by exactly one child, each child has its own scratch directory, stores, clock and
/// Avalonia instance (they are process-wide statics, which is why this is processes and not
/// threads — the headless platform has one UI thread per process), and no two children write a
/// PNG of the same name. A scenario's PNG does not depend on its shard (verification.md, #347),
/// so the output is the output of a one-process run.
/// </summary>
internal static class ParallelRun
{
    /// <summary>
    /// Parses <c>--jobs=N</c> out of <paramref name="args"/>. <c>--jobs</c> alone means one job
    /// per two logical cores, at most four: on an eight-core Apple Silicon Mac a full run took
    /// 50 s with four and 63 s with six, the children slowing each other more than they added.
    /// </summary>
    internal static (int Jobs, string[] Remaining) Parse(string[] args)
    {
        var jobs = 1;
        var rest = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "--jobs")
            {
                jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            }
            else if (arg.StartsWith("--jobs=", StringComparison.Ordinal))
            {
                if (!int.TryParse(arg["--jobs=".Length..], out jobs) || jobs < 1)
                {
                    throw new ArgumentException($"Expected --jobs=<count> with count >= 1, got '{arg}'.");
                }
            }
            else
            {
                rest.Add(arg);
            }
        }

        return (jobs, rest.ToArray());
    }

    /// <summary>
    /// Starts <paramref name="jobs"/> children with <c>--shard=i/jobs</c> and the rest of the
    /// arguments unchanged, relays their output prefixed with the shard, and returns non-zero
    /// when any child failed. A child's failure does not stop the others, for the reason CI's
    /// matrix is <c>fail-fast: false</c>: one red shard should not hide another's verdict.
    /// </summary>
    internal static int Run(int jobs, string[] args)
    {
        var started = Stopwatch.StartNew();
        var (file, prefix) = Self();
        var children = new List<(int Shard, Process Process)>();
        for (var shard = 1; shard <= jobs; shard++)
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in prefix.Concat(args).Append($"--shard={shard}/{jobs}"))
            {
                info.ArgumentList.Add(arg);
            }

            var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start shard {shard}.");
            var label = $"[{shard}/{jobs}] ";
            process.OutputDataReceived += (_, e) => Relay(Console.Out, label, e.Data);
            process.ErrorDataReceived += (_, e) => Relay(Console.Error, label, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            children.Add((shard, process));
        }

        var failed = new List<string>();
        foreach (var (shard, process) in children)
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                failed.Add($"{shard}/{jobs} (exit {process.ExitCode})");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed.Count == 0
            ? $"All {jobs} shards passed in {started.Elapsed.TotalSeconds:F1} s."
            : $"Shards failed: {string.Join(", ", failed)}. Total {started.Elapsed.TotalSeconds:F1} s.");
        return failed.Count == 0 ? 0 : 1;
    }

    private static readonly Lock RelayLock = new();

    private static void Relay(TextWriter writer, string label, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (RelayLock)
        {
            writer.WriteLine(label + line);
        }
    }

    /// <summary>
    /// How to start this harness again: its apphost when it has one, otherwise the dotnet host
    /// with this assembly (an apphost-less build, or a <c>dotnet Screenshot.dll</c> start).
    /// </summary>
    private static (string File, string[] Prefix) Self()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("No process path to start the shards from.");
        var name = Path.GetFileNameWithoutExtension(process);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (process, [typeof(ParallelRun).Assembly.Location])
            : (process, []);
    }
}
