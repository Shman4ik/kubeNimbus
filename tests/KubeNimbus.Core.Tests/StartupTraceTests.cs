namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="StartupTrace"/>: one line per phase and subject, the first time only; nothing
/// at all without a target; and a target that cannot be written turns the trace off
/// instead of failing the launch.
/// </summary>
public class StartupTraceTests
{
    [Test]
    public async Task A_phase_is_written_once_per_subject_the_first_time_it_is_reached()
    {
        using var writer = new StringWriter();
        var now = TimeSpan.Zero;
        var log = new StartupTraceLog(writer, () => now);

        now = TimeSpan.FromMilliseconds(120.4);
        log.Mark("version", "prod", "v1.33.4");
        now = TimeSpan.FromMilliseconds(130);
        log.Mark("version", "staging", null);
        now = TimeSpan.FromMilliseconds(900);
        log.Mark("version", "prod", "v1.33.4");
        log.Mark("main", null, null);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo(["120\tversion\tprod\tv1.33.4", "130\tversion\tstaging", "900\tmain"]);
    }

    [Test]
    public async Task Without_a_target_nothing_is_written_and_marks_do_nothing()
    {
        var log = StartupTraceLog.Open(null, TimeSpan.Zero);

        log.Mark("main", null, null);

        await Assert.That(log.IsEnabled).IsFalse();
    }

    [Test]
    public async Task A_file_target_is_appended_to()
    {
        using var directory = new Live.TempDirectory("kubenimbus-trace");
        var path = Path.Combine(directory.Path, "trace.tsv");
        await File.WriteAllTextAsync(path, "earlier launch\n");

        var log = StartupTraceLog.Open(path, TimeSpan.FromMilliseconds(40));
        log.Mark("main", null, null);

        // The trace keeps the file open for the life of the process, so it is read the way
        // another process reading it during a launch would: sharing it for writing.
        string[] lines;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }

        await Assert.That(log.IsEnabled).IsTrue();
        await Assert.That(lines[0]).IsEqualTo("earlier launch");
        await Assert.That(lines[1]).EndsWith("\tmain");
        await Assert.That(int.Parse(lines[1].Split('\t')[0], System.Globalization.CultureInfo.InvariantCulture)).IsGreaterThanOrEqualTo(40);
    }

    [Test]
    public async Task A_target_that_cannot_be_opened_turns_the_trace_off()
    {
        using var directory = new Live.TempDirectory("kubenimbus-trace");

        // A directory is not a file that can be appended to.
        var log = StartupTraceLog.Open(directory.Path, TimeSpan.Zero);
        log.Mark("main", null, null);

        await Assert.That(log.IsEnabled).IsFalse();
    }
}
