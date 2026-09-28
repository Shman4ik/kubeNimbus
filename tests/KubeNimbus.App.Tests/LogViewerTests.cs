using KubeNimbus.App.Controls;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The log viewer pass: severity read from the level keyword the logger printed (and only
/// that keyword coloured), a search that takes regular expressions and case, a filter that
/// keeps context around each match like <c>grep -C</c>, and a jump between the errors
/// that hides nothing. None of it is a property a screenshot of a plausible log shows.
/// </summary>
public class LogViewerTests
{
    private static LogLineViewModel Line(string message) => new($"2026-09-28T07:00:00.000Z {message}", showTimestamp: false);

    private static LogQuery Query(string text, bool regex = false, bool matchCase = false) =>
        LogQuery.Create(text, regex, matchCase, out _)!;

    // ------------------------------------------------------------------ severity

    [Test]
    public async Task The_first_level_keyword_decides_and_is_the_one_coloured()
    {
        var line = Line("info: retrying after error from payments");
        await Assert.That(line.Severity).IsEqualTo(LogSeverity.Info);
        await Assert.That(line.Message.Substring(line.LevelIndex, line.LevelLength)).IsEqualTo("info");

        await Assert.That(Line("[main] ERROR could not reach warn-service").Severity).IsEqualTo(LogSeverity.Error);
        await Assert.That(Line("2026-09-28 WRN slow request").Severity).IsEqualTo(LogSeverity.Warn);
        await Assert.That(Line("GET /api/v1/errors 200").Severity).IsEqualTo(LogSeverity.None);
        await Assert.That(Line("GET /healthz 200").LevelIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task A_structured_level_field_beats_words_in_the_message()
    {
        var json = Line("""{"msg":"error budget recalculated","level":"info","ts":1}""");
        await Assert.That(json.Severity).IsEqualTo(LogSeverity.Info);
        await Assert.That(json.Message.Substring(json.LevelIndex, json.LevelLength)).IsEqualTo("info");

        await Assert.That(Line("""{"LogLevel":"Warning","Message":"slow"}""").Severity).IsEqualTo(LogSeverity.Warn);
        await Assert.That(Line("ts=1 level=error msg=\"db down\"").Severity).IsEqualTo(LogSeverity.Error);
        // An explicit debug level is not an error because its message says error.
        await Assert.That(Line("""{"level":"debug","msg":"error path taken"}""").Severity).IsEqualTo(LogSeverity.None);
    }

    [Test]
    public async Task Klog_headers_are_read()
    {
        await Assert.That(Line("E0928 07:00:00.123456       1 controller.go:114] sync failed").Severity).IsEqualTo(LogSeverity.Error);
        await Assert.That(Line("W0928 07:00:00.123456       1 reflector.go:1] watch closed").Severity).IsEqualTo(LogSeverity.Warn);
        await Assert.That(Line("I0928 07:00:00.123456       1 leaderelection.go:1] acquired").Severity).IsEqualTo(LogSeverity.Info);
        await Assert.That(Line("E09 not a header").Severity).IsEqualTo(LogSeverity.None);
    }

    [Test]
    public async Task The_display_level_start_follows_the_timestamp_toggle()
    {
        var line = new LogLineViewModel("2026-09-28T07:00:00.000Z warn: slow", showTimestamp: false);
        await Assert.That(line.DisplayLevelStart).IsEqualTo(0);

        line.ShowTimestamp = true;
        line.UtcTimestamp = true;
        await Assert.That(line.DisplayText.Substring(line.DisplayLevelStart, line.LevelLength)).IsEqualTo("warn");
    }

    [Test]
    public async Task Coloured_spans_never_overlap_and_the_selection_wins()
    {
        // 24 chars of prefix, a level at 25..29, a selection over 27..40.
        var spans = LogLineText.Spans(60, prefixLength: 24, levelStart: 25, levelLength: 4, selectionStart: 27, selectionLength: 13);
        await Assert.That(spans.Select(s => (s.Start, s.Length, s.Kind)).ToArray())
            .IsEquivalentTo(new[] { (0, 24, 0), (25, 2, 1), (27, 13, 2) });

        var plain = LogLineText.Spans(10, 0, -1, 0, 0, 0);
        await Assert.That(plain).IsEmpty();
    }

    // ------------------------------------------------------------------ query

    [Test]
    public async Task A_regular_expression_matches_and_anchors_to_the_message()
    {
        var query = Query(@"^status=5\d\d", regex: true);
        await Assert.That(query.IsMatch("status=503 upstream")).IsTrue();
        await Assert.That(query.IsMatch("got status=503")).IsFalse();

        // In the displayed text the message starts after the timestamp; ^ still means the message.
        const string shown = "2026-09-28 07:00:00.000 status=502 bad gateway";
        var ranges = query.Matches(shown, 24);
        await Assert.That(ranges.Count).IsEqualTo(1);
        await Assert.That(shown.Substring(ranges[0].Start, ranges[0].Length)).IsEqualTo("status=502");
    }

    [Test]
    public async Task Case_is_ignored_unless_asked_and_a_bad_pattern_is_an_error_not_a_filter()
    {
        await Assert.That(Query("Timeout").IsMatch("TIMEOUT calling api")).IsTrue();
        await Assert.That(Query("Timeout", matchCase: true).IsMatch("TIMEOUT calling api")).IsFalse();
        await Assert.That(Query("time(out|d)", regex: true).IsMatch("TIMED")).IsTrue();

        var bad = LogQuery.Create("status=(5", regex: true, matchCase: false, out var error);
        await Assert.That(bad).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(LogQuery.Create("", regex: true, matchCase: false, out var none)).IsNull();

        // A lookaround is refused in words: the engine is linear-time so no pattern can hang the window.
        await Assert.That(LogQuery.Create("(?=timeout)", regex: true, matchCase: false, out var refused)).IsNull();
        await Assert.That(refused!).Contains("lookarounds");
        await Assert.That(Query("(a+)+b", regex: true).IsMatch(new string('a', 5000) + "c")).IsFalse();
        await Assert.That(none).IsNull();
    }

    [Test]
    public async Task An_invalid_pattern_hides_nothing_and_says_so()
    {
        var pane = PaneWithLines(["a", "b", "c"]);
        pane.IsLogFilterMode = true;
        pane.IsLogRegex = true;
        pane.LogSearchText = "b(";

        await Assert.That(pane.LogLines.Count).IsEqualTo(3);
        await Assert.That(pane.HasLogSearchError).IsTrue();
        await Assert.That(pane.LogSearchSummary).IsEqualTo("Invalid pattern");

        pane.LogSearchText = "b(\\d)?";
        await Assert.That(pane.HasLogSearchError).IsFalse();
        await Assert.That(Shown(pane)).IsEqualTo("b");
    }

    // ------------------------------------------------------------------ context

    [Test]
    public async Task Filtering_with_context_keeps_the_lines_around_each_match_and_marks_the_gaps()
    {
        var pane = PaneWithLines(["1", "2", "3 hit", "4", "5", "6", "7", "8 hit", "9", "10"]);
        pane.IsLogFilterMode = true;
        pane.LogContextLines = 1;
        pane.LogSearchText = "hit";

        await Assert.That(Shown(pane)).IsEqualTo("2 | 3 hit | 4 | 7 | 8 hit | 9");
        await Assert.That(pane.LogLines.Select(l => l.IsContextLine).ToArray())
            .IsEquivalentTo(new[] { true, false, true, true, false, true });
        await Assert.That(pane.LogLines.Select(l => l.IsGapBefore).ToArray())
            .IsEquivalentTo(new[] { false, false, false, true, false, false });
        // The counter counts matches, not the context around them.
        await Assert.That(pane.LogSearchSummary).IsEqualTo("2 lines");
    }

    [Test]
    public async Task Context_is_kept_as_lines_stream_in()
    {
        var pane = PaneWithLines(["a", "b"]);
        pane.IsLogFilterMode = true;
        pane.LogContextLines = 2;
        pane.LogSearchText = "hit";
        await Assert.That(Shown(pane)).IsEqualTo("");

        Feed(pane, ["c", "d", "e hit", "f", "g", "h", "i", "j"]);

        // Two before, the match, two after; nothing else until the next match.
        await Assert.That(Shown(pane)).IsEqualTo("c | d | e hit | f | g");

        Feed(pane, ["k hit"]);
        await Assert.That(Shown(pane)).IsEqualTo("c | d | e hit | f | g | i | j | k hit");
        await Assert.That(pane.LogLines.Single(l => l.Message == "i").IsGapBefore).IsTrue();
    }

    [Test]
    public async Task Without_context_the_filter_is_the_plain_filter_it_was()
    {
        var pane = PaneWithLines(["a", "b hit", "c", "d hit"]);
        pane.IsLogFilterMode = true;
        pane.LogSearchText = "hit";

        await Assert.That(Shown(pane)).IsEqualTo("b hit | d hit");
        await Assert.That(pane.LogLines.Any(l => l.IsContextLine)).IsFalse();
    }

    // ------------------------------------------------------------------ problems

    [Test]
    public async Task The_error_jump_starts_at_the_latest_and_walks_back_hiding_nothing()
    {
        var pane = PaneWithLines(["ERROR one", "info fine", "WARN slow", "ERROR two", "info fine", "ERROR three"]);

        await Assert.That(pane.Problems.ErrorCount).IsEqualTo(3);
        await Assert.That(pane.Problems.WarningCount).IsEqualTo(1);

        pane.Problems.PreviousErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR three");
        await Assert.That(pane.Problems.Current.IsProblemCursor).IsTrue();

        pane.Problems.PreviousErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR two");

        pane.Problems.NextErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR three");

        // Wraps from the latest to the earliest.
        pane.Problems.NextErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR one");
        await Assert.That(pane.LogLines.Count).IsEqualTo(6);

        // From a warning, "previous error" is the error before that warning.
        pane.Problems.PreviousWarningCommand.Execute(null);
        pane.Problems.PreviousErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR one");
    }

    [Test]
    public async Task A_new_line_does_not_move_the_error_cursor()
    {
        var pane = PaneWithLines(["ERROR one", "ERROR two"]);
        pane.Problems.PreviousErrorCommand.Execute(null);
        pane.Problems.PreviousErrorCommand.Execute(null);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR one");

        Feed(pane, ["ERROR three"]);
        await Assert.That(pane.Problems.Current!.Message).IsEqualTo("ERROR one");
        await Assert.That(pane.Problems.ErrorCount).IsEqualTo(3);
    }

    // ------------------------------------------------------------------ exclusions

    [Test]
    public async Task A_bang_word_hides_its_lines_in_either_mode_and_counts_them()
    {
        var pane = PaneWithLines(["GET /healthz 200", "timeout calling db", "GET /readyz 200", "timeout, retrying"]);

        pane.LogSearchText = "!healthz !readyz";
        await Assert.That(Shown(pane)).IsEqualTo("timeout calling db | timeout, retrying");
        await Assert.That(pane.LogSearchSummary).IsEqualTo("2 hidden");

        pane.LogSearchText = "timeout !retrying";
        await Assert.That(pane.LogLines.Count).IsEqualTo(3);
        // The retried timeout is hidden, so it is not a match either.
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 of 1 · 1 hidden");

        pane.IsLogFilterMode = true;
        await Assert.That(Shown(pane)).IsEqualTo("timeout calling db");

        // Without a bang word the text is one phrase, spaces included.
        pane.LogSearchText = "calling db";
        await Assert.That(Shown(pane)).IsEqualTo("timeout calling db");
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 line");
    }

    // ------------------------------------------------------------------ stack traces

    [Test]
    public async Task A_stack_trace_takes_the_error_of_the_line_that_threw_and_is_one_stop()
    {
        var pane = PaneWithLines([
            "fail: Orders[0] Unhandled exception",
            "System.InvalidOperationException: nope",
            "   at Orders.Handle() in Orders.cs:line 12",
            "   at Program.Main()",
            "info: next request",
            "  indented, under a plain line",
        ]);

        var lines = pane.LogLines.ToList();
        // Java and .NET print the exception's type flush left under the line that logged it.
        await Assert.That(lines[1].IsErrorLine).IsTrue();
        await Assert.That(lines[2].IsErrorLine).IsTrue();
        await Assert.That(lines[3].IsErrorLine).IsTrue();
        await Assert.That(lines[3].IsInheritedSeverity).IsTrue();
        await Assert.That(lines[5].Severity).IsEqualTo(LogSeverity.None);
        // A type-shaped line with no error above it is just a line.
        await Assert.That(Line("System.InvalidOperationException: nope").Severity).IsEqualTo(LogSeverity.None);
        await Assert.That(pane.Problems.ErrorCount).IsEqualTo(1);
    }

    // ------------------------------------------------------------------ reveal

    [Test]
    public async Task A_filtered_line_double_clicked_is_shown_in_the_full_log()
    {
        var pane = PaneWithLines(["a", "b hit", "c", "d hit", "e"]);
        pane.IsLogFilterMode = true;
        pane.LogSearchText = "hit";
        var first = pane.LogLines[0];

        pane.RevealLine(first);

        await Assert.That(pane.IsLogFilterMode).IsFalse();
        await Assert.That(pane.LogLines.Count).IsEqualTo(5);
        await Assert.That(pane.CurrentLogMatch).IsSameReferenceAs(first);
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 of 2");
        await Assert.That(first.IsProblemCursor).IsTrue();
    }

    // ------------------------------------------------------------------ pins

    [Test]
    public async Task Pinning_keeps_the_highlight_and_frees_the_box()
    {
        var pane = PaneWithLines(["timeout a", "retry b"]);
        pane.LogSearchText = "timeout !noise";
        pane.PinSearchCommand.Execute(null);

        await Assert.That(pane.LogSearchText).IsEqualTo("");
        await Assert.That(pane.Pins.Items.Count).IsEqualTo(1);
        await Assert.That(pane.Pins.Items[0].Text).IsEqualTo("timeout");
        await Assert.That(pane.LogLines[0].Matches(pane.Pins.Items[0].Query)).IsTrue();

        // The same search is not pinned twice, and there are never more than five.
        pane.LogSearchText = "timeout";
        pane.PinSearchCommand.Execute(null);
        await Assert.That(pane.Pins.Items.Count).IsEqualTo(1);
        foreach (var word in new[] { "a", "b", "c", "d", "e" })
        {
            pane.LogSearchText = word;
            if (pane.PinSearchCommand.CanExecute(null)) pane.PinSearchCommand.Execute(null);
        }

        await Assert.That(pane.Pins.Items.Count).IsEqualTo(LogPin.Max);
        await Assert.That(pane.Pins.Items.Select(p => p.Slot).Distinct().Count()).IsEqualTo(LogPin.Max);

        pane.Pins.RemoveCommand.Execute(pane.Pins.Items[0]);
        await Assert.That(pane.Pins.Items.Count).IsEqualTo(LogPin.Max - 1);
        await Assert.That(pane.PinSearchCommand.CanExecute(null)).IsTrue();
    }

    // ------------------------------------------------------------------ ruler

    [Test]
    public async Task The_ruler_buckets_every_line_into_the_rows_of_the_strip()
    {
        var lines = Enumerable.Range(0, 1000)
            .Select(i => Line(i == 10 ? "ERROR boom" : i == 500 ? "WARN slow" : i == 999 ? "timeout here" : $"line {i}"))
            .ToList();

        var (marks, first, _) = LogOverviewRuler.Bucket(lines, 100, Query("timeout"), null, cursor: lines[500]);

        await Assert.That(marks.Length).IsEqualTo(100);
        await Assert.That(first[1]).IsEqualTo(10);      // the error, on row 1 of 100
        await Assert.That(first[50]).IsEqualTo(500);    // the warning, with the cursor on it
        await Assert.That(first[99]).IsEqualTo(999);    // the match
        await Assert.That(marks.Count(m => m != 0)).IsEqualTo(3);
    }

    // ------------------------------------------------------------------ json

    [Test]
    public async Task A_json_line_opens_indented_and_keeps_its_bytes()
    {
        var line = Line("""{"level":"info","msg":"café opened","n":3}""");
        await Assert.That(line.IsJson).IsTrue();
        await Assert.That(line.PrettyJson).Contains("\n  \"msg\": \"café opened\"");
        await Assert.That(line.RawLine).EndsWith("""{"level":"info","msg":"café opened","n":3}""");

        await Assert.That(Line("{not json}").PrettyJson).StartsWith("Not valid JSON");
        await Assert.That(Line("plain text").IsJson).IsFalse();
    }

    // ------------------------------------------------------------------ helpers

    private static PodDetailTabViewModel PaneWithLines(IEnumerable<string> messages)
    {
        TestObjects.RedirectStores();
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "api-1", "namespace": "payments",
                            "annotations": { "kubectl.kubernetes.io/default-container": "model-server" } },
              "spec": { "containers": [ { "name": "istio-proxy" }, { "name": "model-server" } ] },
              "status": { "phase": "Running" }
            }
            """);
        var pod = new KubeNimbus.Core.DynamicResource(document.RootElement.Clone());
        var pane = new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);
        Feed(pane, messages);
        return pane;
    }

    private static int _second;

    private static void Feed(PodDetailTabViewModel pane, IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            var at = new DateTimeOffset(2026, 9, 28, 7, 0, 0, TimeSpan.Zero).AddSeconds(Interlocked.Increment(ref _second));
            pane.Enqueue($"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {message}");
        }

        pane.FlushLogLines();
    }

    private static string Shown(PodDetailTabViewModel pane) => string.Join(" | ", pane.LogLines.Select(l => l.Message));
}
