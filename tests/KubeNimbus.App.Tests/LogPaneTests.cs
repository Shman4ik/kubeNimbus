using System.Text.Json;
using KubeNimbus.App.Controls;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The log panes' reading tools — search that finds as well as filters (FEAT-33), the
/// level filter (FEAT-36), clear (FEAT-40), local time (FEAT-39), the default container
/// (FEAT-38) — and what the multi-pod pane says about pods that never started (ENG-45).
///
/// <para>
/// Every one of these is a statement about <em>which</em> lines are shown and what the
/// pane says about them, and none of it is visible as a difference in a screenshot of a
/// plausible log. So these drive the real <c>Enqueue</c>/<c>Flush</c> the socket pump
/// and the flush timer call, on a demo-mode pane (no client, so nothing reaches a
/// network), as <see cref="WorkloadLogsTests"/> does.
/// </para>
/// </summary>
public class LogPaneTests
{
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>A multi-pod pane whose selector matches nothing in the demo dataset, so the test fills it.</summary>
    private static WorkloadLogsTabViewModel Pane(bool freshStores = true)
    {
        if (freshStores) TestObjects.RedirectStores();
        var workload = Object("""
            {
              "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "api", "namespace": "nowhere" },
              "spec": { "selector": { "matchLabels": { "app": "api-nothing-matches-this" } } }
            }
            """);
        return new WorkloadLogsTabViewModel(null, DeploymentDescriptor, workload, LabelSelector.ForPodsOf(workload)!);
    }

    /// <summary>
    /// A demo-mode pod detail pane. By default it opens on "model-server", the dataset's
    /// never-started container, for which the demo replay has no lines — which keeps the
    /// canned stream out of what the test enqueues. <c>null</c> leaves the annotation off.
    /// </summary>
    private static PodDetailTabViewModel Detail(string? defaultContainer = "model-server", bool freshStores = true)
    {
        if (freshStores) TestObjects.RedirectStores();
        var annotations = defaultContainer is null
            ? ""
            : $$""", "annotations": { "kubectl.kubernetes.io/default-container": "{{defaultContainer}}" }""";
        var pod = Object($$"""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "checkout-7f9c-x7k2m", "namespace": "payments"{{annotations}} },
              "spec": {
                "nodeName": "worker-1",
                "containers": [ { "name": "istio-proxy" }, { "name": "model-server" } ]
              },
              "status": { "phase": "Running" }
            }
            """);
        return new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);
    }

    private static string Shown(WorkloadLogsTabViewModel pane) => string.Join(" | ", pane.LogLines.Select(l => l.Message));

    private static string Shown(PodDetailTabViewModel pane) => string.Join(" | ", pane.LogLines.Select(l => l.Message));

    // ---------------------------------------------------------------- find (FEAT-33)

    /// <summary>
    /// Finding keeps every line on screen — the lines around a match are the point — and
    /// counts the matching lines; a new query lands on the newest match, the one a pane
    /// following the bottom of the stream is nearest to.
    /// </summary>
    [Test]
    public async Task Finding_keeps_every_line_and_lands_on_the_newest_match()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z ERROR timeout calling payments", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z GET /healthz 200", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z retrying after Timeout", a);
        pane.Flush(force: true);

        pane.LogSearchText = "timeout";

        await Assert.That(pane.LogLines.Count).IsEqualTo(3);
        await Assert.That(pane.LogSearchSummary).IsEqualTo("2 of 2");
        await Assert.That(pane.CurrentLogMatch!.Message).IsEqualTo("retrying after Timeout");
        await Assert.That(pane.CurrentLogMatch.IsCurrentMatch).IsTrue();
        await Assert.That(pane.IsFinding).IsTrue();
    }

    /// <summary>Next is later and Previous is earlier, and both wrap — with exactly one line marked current.</summary>
    [Test]
    public async Task Next_and_previous_step_through_the_matching_lines_and_wrap()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z first match", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z nothing here", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z second match", a);
        pane.Flush(force: true);
        pane.LogSearchText = "match";

        pane.FindPreviousLogMatchCommand.Execute(null);
        await Assert.That(pane.CurrentLogMatch!.Message).IsEqualTo("first match");
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 of 2");

        pane.FindPreviousLogMatchCommand.Execute(null);
        await Assert.That(pane.CurrentLogMatch!.Message).IsEqualTo("second match");

        pane.FindNextLogMatchCommand.Execute(null);
        await Assert.That(pane.CurrentLogMatch!.Message).IsEqualTo("first match");
        await Assert.That(pane.LogLines.Count(l => l.IsCurrentMatch)).IsEqualTo(1);
    }

    /// <summary>
    /// A following stream must not drag the reader off the match they are on: a new
    /// matching line raises the count and leaves the position where it was.
    /// </summary>
    [Test]
    public async Task A_new_matching_line_does_not_move_the_current_match()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z error one", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z error two", a);
        pane.Flush(force: true);
        pane.LogSearchText = "error";
        pane.FindPreviousLogMatchCommand.Execute(null);

        pane.Enqueue("2026-08-17T10:00:03.000Z error three", a);
        pane.Flush(force: true);

        await Assert.That(pane.CurrentLogMatch!.Message).IsEqualTo("error one");
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 of 3");
    }

    /// <summary>
    /// Filtering is the other mode, not gone: it hides what does not match, has nothing
    /// to step through, and its counter is a count.
    /// </summary>
    [Test]
    public async Task Filter_mode_hides_what_does_not_match_and_counts_what_is_left()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z error one", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z fine", a);
        pane.Flush(force: true);
        pane.LogSearchText = "error";

        pane.IsLogFilterMode = true;

        await Assert.That(Shown(pane)).IsEqualTo("error one");
        await Assert.That(pane.LogSearchSummary).IsEqualTo("1 line");
        await Assert.That(pane.CurrentLogMatch).IsNull();
        await Assert.That(pane.FindNextLogMatchCommand.CanExecute(null)).IsFalse();

        pane.IsLogFilterMode = false;
        await Assert.That(Shown(pane)).IsEqualTo("error one | fine");
    }

    /// <summary>
    /// The highlight matches the message, never the timestamp prefix, so what is lit up is
    /// exactly what the counter counts. Case-insensitive, and non-overlapping.
    /// </summary>
    [Test]
    public async Task The_highlight_ranges_skip_the_timestamp_and_ignore_case()
    {
        var line = new LogLineViewModel("2026-08-17T10:00:01.000Z 08:41 Timeout; timeout", showTimestamp: true, utcTimestamp: true);

        static LogQuery Plain(string text) => LogQuery.Create(text, regex: false, matchCase: false, out _)!;

        var ranges = Plain("timeout").Matches(line.DisplayText, line.MessageOffset);
        await Assert.That(ranges.Count).IsEqualTo(2);
        await Assert.That(line.DisplayText.Substring(ranges[0].Start, ranges[0].Length)).IsEqualTo("Timeout");

        // "08:41" is in the message here too, but the timestamp's own "10:00" is not
        // searched: only matches at or after the message offset count.
        await Assert.That(Plain("10:00").Matches(line.DisplayText, line.MessageOffset)).IsEmpty();
        await Assert.That(Plain("aa").Matches(line.DisplayText, 0)).IsEmpty();
        await Assert.That(Plain("aa").Matches("aaaa", 0).Count).IsEqualTo(2);
    }

    // -------------------------------------------------------------- levels (FEAT-36)

    /// <summary>
    /// Hiding a level hides its lines; a line with no level keyword is always shown, even
    /// with all three off — most real output carries none, and hiding it would turn "hide
    /// the INFO noise" into "hide most of the log".
    /// </summary>
    [Test]
    public async Task Hiding_levels_never_hides_an_unclassified_line()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z INFO started", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z GET /healthz 200 3ms", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z WARN slow query", a);
        pane.Enqueue("2026-08-17T10:00:04.000Z ERROR upstream reset", a);
        pane.Flush(force: true);

        pane.Levels.ShowInfo = false;
        await Assert.That(Shown(pane)).IsEqualTo("GET /healthz 200 3ms | WARN slow query | ERROR upstream reset");
        await Assert.That(pane.Levels.Label).IsEqualTo("Error, Warn");
        await Assert.That(pane.Levels.IsFiltering).IsTrue();

        pane.Levels.ShowWarn = false;
        pane.Levels.ShowError = false;
        await Assert.That(Shown(pane)).IsEqualTo("GET /healthz 200 3ms");
        await Assert.That(pane.Levels.Label).IsEqualTo("Unleveled only");

        // Back in place — a narrowing of the projection, never of the buffer.
        pane.Levels.ShowInfo = true;
        pane.Levels.ShowWarn = true;
        pane.Levels.ShowError = true;
        await Assert.That(pane.LogLines.Count).IsEqualTo(4);
        await Assert.That(pane.Levels.Label).IsEqualTo("Levels");
    }

    /// <summary>A pane whose every line is at a hidden level says so, rather than looking like a quiet container.</summary>
    [Test]
    public async Task A_pane_emptied_by_the_level_filter_says_so()
    {
        var detail = Detail();
        detail.Enqueue("2026-08-17T10:00:01.000Z INFO started");
        detail.FlushLogLines();

        detail.Levels.ShowInfo = false;

        await Assert.That(detail.LogLines.Count).IsEqualTo(0);
        await Assert.That(detail.LogPlaceholder!).Contains("hidden level");
    }

    // --------------------------------------------------------------- clear (FEAT-40)

    /// <summary>
    /// Clear empties the pane and keeps the stream: lines that arrive afterwards are shown,
    /// and the pane in between says it was cleared — not "no lines", which after a clear
    /// would be a verdict about the stream it has no grounds for.
    /// </summary>
    [Test]
    public async Task Clearing_empties_the_pane_and_lines_keep_arriving()
    {
        var detail = Detail();
        detail.Enqueue("2026-08-17T10:00:01.000Z before");
        detail.Enqueue("2026-08-17T10:00:02.000Z before too");
        detail.FlushLogLines();
        await Assert.That(detail.ClearLogsCommand.CanExecute(null)).IsTrue();

        detail.ClearLogsCommand.Execute(null);

        await Assert.That(detail.LogLines.Count).IsEqualTo(0);
        await Assert.That(detail.LogPlaceholder!).StartsWith("Cleared 2 lines");
        await Assert.That(detail.ClearLogsCommand.CanExecute(null)).IsFalse();

        detail.Enqueue("2026-08-17T10:00:03.000Z after");
        detail.FlushLogLines();
        await Assert.That(Shown(detail)).IsEqualTo("after");
        await Assert.That(detail.LogPlaceholder).IsNull();
    }

    /// <summary>The multi-pod pane's clear keeps every pod's source — nothing is re-resolved or re-opened.</summary>
    [Test]
    public async Task Clearing_the_multi_pod_pane_keeps_its_pods()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        pane.Enqueue("2026-08-17T10:00:01.000Z before", a);
        pane.Flush(force: true);

        pane.ClearLogsCommand.Execute(null);

        await Assert.That(pane.Sources.Count).IsEqualTo(1);
        await Assert.That(pane.LogPlaceholder!).StartsWith("Cleared 1 line");

        pane.Enqueue("2026-08-17T10:00:02.000Z after", a);
        pane.Flush(force: true);
        await Assert.That(Shown(pane)).IsEqualTo("after");
        await Assert.That(pane.Summary).IsEqualTo("1 pod · 1 line");
    }

    // ---------------------------------------------------------- local time (FEAT-39)

    /// <summary>
    /// Timestamps print in local time by default and as the server's own token in UTC
    /// mode; Copy and Download write <see cref="LogLineViewModel.RawLine"/> either way. A
    /// real API server's nine fractional digits parse.
    /// </summary>
    [Test]
    [NotInParallel(nameof(LogLineViewModel.ToLocal))]
    public async Task Timestamps_are_local_by_default_and_the_servers_own_in_utc()
    {
        var previous = LogLineViewModel.ToLocal;
        LogLineViewModel.ToLocal = at => at.ToOffset(TimeSpan.FromHours(3));
        try
        {
            const string raw = "2026-07-20T08:41:02.114523917Z connected to postgres";
            var line = new LogLineViewModel(raw, showTimestamp: true);

            await Assert.That(line.DisplayText).IsEqualTo("2026-07-20 11:41:02.114 connected to postgres");
            await Assert.That(line.DisplayText[line.MessageOffset..]).IsEqualTo("connected to postgres");

            line.UtcTimestamp = true;
            await Assert.That(line.DisplayText).IsEqualTo(raw);

            line.ShowTimestamp = false;
            await Assert.That(line.DisplayText).IsEqualTo("connected to postgres");
            await Assert.That(line.RawLine).IsEqualTo(raw);
            await Assert.That(LogLineViewModel.ZoneTooltip).Contains("UTC+03:00");
        }
        finally
        {
            LogLineViewModel.ToLocal = previous;
        }
    }

    // ---------------------------------------------------- persisted toggles (FEAT-37)

    /// <summary>
    /// The display toggles set in one pane are what the next one opens with — pod
    /// detail's and the multi-pod pane's alike. What changes which lines are read is not
    /// carried over: the search text, the levels, and above all Previous (freelens#2095,
    /// where a persisted Previous made a crashed run the default view of healthy pods).
    /// </summary>
    // Unkeyed [NotInParallel], and every pane after the first built on the same stores:
    // since App resolves its settings path per call (ENG-37), a RedirectStores between the
    // write and the read — this test's own helpers included — would move it to an empty file.
    [Test]
    [NotInParallel]
    public async Task Display_toggles_follow_the_reader_into_the_next_pane_and_nothing_else_does()
    {
        var first = Detail();
        try
        {
            first.ShowLogTimestamps = true;
            first.WrapLogLines = true;
            first.UseUtcTimestamps = true;
            first.LogSearchText = "timeout";
            first.Levels.ShowInfo = false;

            var next = Detail(freshStores: false);
            await Assert.That(next.ShowLogTimestamps).IsTrue();
            await Assert.That(next.WrapLogLines).IsTrue();
            await Assert.That(next.UseUtcTimestamps).IsTrue();
            await Assert.That(next.LogSearchText).IsEqualTo("");
            await Assert.That(next.Levels.IsFiltering).IsFalse();
            await Assert.That(next.IsShowingPreviousLogs).IsFalse();

            var multi = Pane(freshStores: false);
            await Assert.That(multi.ShowLogTimestamps).IsTrue();
            await Assert.That(multi.WrapLogLines).IsTrue();

            // The negative half, stated where a later change would have to break it: no
            // setting exists that could carry Previous, the search or the levels.
            var names = typeof(AppSettings).GetProperties().Select(p => p.Name).ToList();
            await Assert.That(names.Any(n => n.Contains("Previous", StringComparison.OrdinalIgnoreCase))).IsFalse();
            await Assert.That(names.Any(n => n.Contains("Search", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Filter", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Level", StringComparison.OrdinalIgnoreCase))).IsFalse();
        }
        finally
        {
            App.Update(s => s with { LogShowTimestamps = false, LogWrapLines = false, LogTimestampsUtc = false });
        }
    }

    // ----------------------------------------------------- default container (FEAT-38)

    /// <summary>
    /// A pod naming its default container opens its logs on that one — not on the mesh
    /// proxy injected ahead of it, which is what "the first container" is on such a pod.
    /// </summary>
    [Test]
    public async Task Pod_detail_opens_on_the_annotated_default_container()
    {
        var annotated = Detail(defaultContainer: "model-server");
        await Assert.That(annotated.SelectedContainer!.Name).IsEqualTo("model-server");

        var plain = Detail(defaultContainer: null);
        await Assert.That(plain.SelectedContainer!.Name).IsEqualTo("istio-proxy");
        await plain.OnClosingAsync();
    }

    // ------------------------------------------------------------- ENG-36, ENG-45

    /// <summary>
    /// ENG-36's acceptance criterion at the App layer: the row icon, L and "Logs (all pods)"
    /// are all gated on <see cref="LogTarget.CanOpen"/>, which a claim no longer passes.
    /// </summary>
    [Test]
    public async Task A_persistent_volume_claim_offers_no_logs()
    {
        var claim = Object("""
            {
              "apiVersion": "v1", "kind": "PersistentVolumeClaim",
              "metadata": { "name": "data", "namespace": "payments" },
              "spec": { "selector": { "matchLabels": { "app": "api" } } }
            }
            """);

        await Assert.That(LogTarget.CanOpen(claim)).IsFalse();
    }

    /// <summary>
    /// The demo's fraud detector is unschedulable, and its log pane has to say so the way a
    /// live cluster would — through <see cref="LogStreamEnd"/> reading the pod — with the
    /// chip saying "not started", not "ended" over a body that disagreed.
    /// </summary>
    [Test]
    public async Task A_pod_that_never_started_says_so_on_the_chip_and_in_the_body()
    {
        var fraud = DemoData.Pods.First(p => p.Name.StartsWith("fraud-detector", StringComparison.Ordinal));
        var (text, problem, notStarted) = LogStreamEnd.DescribePod(fraud.Raw, "model-server", atStart: null);

        await Assert.That(notStarted).IsTrue();
        await Assert.That(problem).IsFalse();
        await Assert.That(text).Contains("not scheduled");
        await Assert.That(text).Contains("Unschedulable");

        var pane = Pane();
        foreach (var name in (string[])["fraud-detector-a", "fraud-detector-b"])
        {
            var source = pane.RegisterSource(name, "model-server");
            source.State = LogSourceState.NotStarted;
            source.StatusMessage = text;
        }

        await Assert.That(pane.Sources.All(s => s.StateLabel == "not started")).IsTrue();
        await Assert.That(pane.LogPlaceholder!).StartsWith("None of the 2 pods has started.");
        await Assert.That(pane.LogPlaceholder!).Contains(text);
    }

    /// <summary>
    /// A container waiting with no container id has never run either (pulling,
    /// ContainerCreating); one waiting after a run (CrashLoopBackOff) has, and is not
    /// "not started".
    /// </summary>
    [Test]
    public async Task Waiting_before_the_first_run_is_not_started_and_waiting_after_one_is_not()
    {
        static JsonElement Pod(string containerStatus) => Object($$"""
            {
              "apiVersion": "v1", "kind": "Pod", "metadata": { "name": "p" },
              "spec": { "nodeName": "worker-1", "containers": [ { "name": "app" } ] },
              "status": { "containerStatuses": [ {{containerStatus}} ] }
            }
            """).Raw;

        var creating = LogStreamEnd.DescribePod(
            Pod("""{ "name": "app", "state": { "waiting": { "reason": "ContainerCreating" } } }"""), "app", null);
        var crashing = LogStreamEnd.DescribePod(
            Pod("""{ "name": "app", "containerID": "containerd://b", "restartCount": 4, "state": { "waiting": { "reason": "CrashLoopBackOff" } } }"""),
            "app",
            null);

        await Assert.That(creating.NotStarted).IsTrue();
        await Assert.That(crashing.NotStarted).IsFalse();
        await Assert.That(crashing.Text).Contains("CrashLoopBackOff");
    }
}
