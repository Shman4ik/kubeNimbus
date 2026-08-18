using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The aggregated log pane's merge, buffer and filter — the parts of FEAT-3 whose
/// failure modes are invisible in a screenshot.
///
/// <para>
/// A rendered pane cannot tell a correct merge from a wrong one: any ordering of lines
/// looks like a plausible log. What distinguishes them is <em>which</em> ordering, over
/// a batch that arrived in a known-wrong order — so these drive the real
/// <see cref="WorkloadLogsTabViewModel.Enqueue"/> and
/// <see cref="WorkloadLogsTabViewModel.Flush"/>, the same two methods the socket pump and
/// the flush timer call, rather than a reproduction of the ordering rule.
/// </para>
///
/// <para>
/// No Avalonia application is started here, in keeping with this project's rule. The tab
/// is constructed against a selector that matches nothing in the demo dataset, so it
/// registers no sources and opens no streams of its own; the test registers its own
/// sources through <c>RegisterSource</c> and pushes lines through <c>Enqueue</c>. The
/// flush timer is created but never ticks — every flush below is explicit.
/// </para>
/// </summary>
public class WorkloadLogsTests
{
    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    /// <summary>
    /// A demo-mode tab (no <c>ClusterClient</c>, so nothing can reach a network) whose
    /// selector deliberately matches no pod in the shipped demo dataset — the pane starts
    /// with an empty strip and the test fills it.
    /// </summary>
    private static WorkloadLogsTabViewModel Pane(string workloadName = "api")
    {
        TestObjects.RedirectStores();
        var workload = Object($$"""
            {
              "apiVersion": "apps/v1",
              "kind": "Deployment",
              "metadata": { "name": "{{workloadName}}", "namespace": "nowhere" },
              "spec": { "selector": { "matchLabels": { "app": "{{workloadName}}-nothing-matches-this" } } }
            }
            """);

        return new WorkloadLogsTabViewModel(
            client: null,
            DeploymentDescriptor,
            workload,
            LabelSelector.ForPodsOf(workload)!);
    }

    private static string Rendered(WorkloadLogsTabViewModel pane) =>
        string.Join(" | ", pane.LogLines.Select(l => $"{l.Source?.ShortName}:{l.Message}"));

    // ------------------------------------------------------------------ the merge

    /// <summary>
    /// The acceptance criterion, in the smallest form that can fail: three pods answer at
    /// once with their own history, and what lands in the pane is one chronological
    /// stream rather than three consecutive ones.
    /// </summary>
    [Test]
    public async Task An_opening_burst_from_several_pods_is_merged_into_one_chronological_stream()
    {
        var pane = Pane();
        var old1 = pane.RegisterSource("api-7f9c8d6bcd-x7k2m", "app");
        var old2 = pane.RegisterSource("api-7f9c8d6bcd-m4v8s", "app");
        var fresh = pane.RegisterSource("api-8c1a4f2e91-tq6rn", "app");

        // Arrival order is per-pod, which is what a parallel fetch produces.
        pane.Enqueue("2026-08-17T10:00:01.000Z old1 first", old1);
        pane.Enqueue("2026-08-17T10:00:05.000Z old1 second", old1);
        pane.Enqueue("2026-08-17T10:00:02.000Z old2 first", old2);
        pane.Enqueue("2026-08-17T10:00:06.000Z old2 draining", old2);
        pane.Enqueue("2026-08-17T10:00:03.000Z new starting", fresh);
        pane.Enqueue("2026-08-17T10:00:07.000Z new serving", fresh);

        pane.Flush(force: true);

        await Assert.That(string.Join(" | ", pane.LogLines.Select(l => l.Message))).IsEqualTo(
            "old1 first | old2 first | new starting | old1 second | old2 draining | new serving");
    }

    /// <summary>
    /// Two pods that logged in the same millisecond must not swap places on every tick.
    /// LINQ's OrderBy is a stable sort and Array.Sort is not — this is what stops that
    /// choice being quietly reverted.
    /// </summary>
    [Test]
    public async Task Equal_timestamps_keep_their_arrival_order()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        var b = pane.RegisterSource("api-b", "app");

        pane.Enqueue("2026-08-17T10:00:00.000Z from b", b);
        pane.Enqueue("2026-08-17T10:00:00.000Z from a", a);
        pane.Flush(force: true);

        await Assert.That(Rendered(pane)).IsEqualTo("b:from b | a:from a");
    }

    /// <summary>
    /// A line whose leading token is not a timestamp inherits the instant of the line
    /// before it, so a stack trace stays attached to the line it belongs to instead of
    /// being sorted to the top of the batch.
    /// </summary>
    [Test]
    public async Task An_untimestamped_continuation_line_stays_behind_the_line_it_follows()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        var b = pane.RegisterSource("api-b", "app");

        pane.Enqueue("2026-08-17T10:00:09.000Z panic: nil map", a);
        pane.Enqueue("    goroutine 1 [running]:", a);
        pane.Enqueue("    main.main()", a);
        pane.Enqueue("2026-08-17T10:00:01.000Z earlier line from b", b);
        pane.Flush(force: true);

        await Assert.That(string.Join(" | ", pane.LogLines.Select(l => l.Message))).IsEqualTo(
            "earlier line from b | panic: nil map |     goroutine 1 [running]: |     main.main()");
    }

    /// <summary>
    /// <see cref="WorkloadLogsTabViewModel.OrderBatch"/> is called on every tick, so it
    /// must not reorder a batch that is already in order (which is the common case once a
    /// pane is following).
    /// </summary>
    [Test]
    public async Task An_already_ordered_batch_is_left_alone()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");

        pane.Enqueue("2026-08-17T10:00:01.000Z one", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z two", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z three", a);
        pane.Flush(force: true);

        await Assert.That(string.Join(" | ", pane.LogLines.Select(l => l.Message))).IsEqualTo("one | two | three");
    }

    // ------------------------------------------------------- colour and identity

    /// <summary>
    /// Each pod gets its own colour, and the brush is a real one. A converter returning
    /// null for the default case is how this app once rendered most log lines invisible —
    /// a null Foreground is a *local* value, it beats inheritance, and Avalonia's glyph
    /// draw early-returns on it. There is no null to produce here, and this says so.
    /// </summary>
    [Test]
    public async Task Every_pod_gets_a_distinct_non_null_colour()
    {
        var pane = Pane();
        var brushes = Enumerable.Range(0, LogSourcePalette.Count)
            .Select(i => pane.RegisterSource($"api-{i}", "app").Brush)
            .ToList();

        await Assert.That(brushes.Any(b => b is null)).IsFalse();
        await Assert.That(brushes.Distinct().Count()).IsEqualTo(LogSourcePalette.Count);

        // Past the palette's length the colours repeat rather than becoming null or
        // running off the end of the array.
        var wrapped = pane.RegisterSource("api-wrapped", "app");
        await Assert.That(wrapped.Brush).IsEqualTo(brushes[0]);
    }

    [Test]
    public async Task The_line_prefix_drops_the_workload_name_every_pod_shares()
    {
        await Assert.That(LogSourcePalette.ShortNameFor("api-7f9c8d6bcd-x7k2m", "api"))
            .IsEqualTo("7f9c8d6bcd-x7k2m");

        // A Service's selector can match pods named anything at all, so an unrelated name
        // is printed whole rather than mangled.
        await Assert.That(LogSourcePalette.ShortNameFor("checkout-worker-5d8", "api"))
            .IsEqualTo("checkout-worker-5d8");

        // "api" itself, and "apixyz", must not be shortened to nothing or to "xyz".
        await Assert.That(LogSourcePalette.ShortNameFor("api", "api")).IsEqualTo("api");
        await Assert.That(LogSourcePalette.ShortNameFor("apixyz", "api")).IsEqualTo("apixyz");
    }

    // ------------------------------------------------------------------- filtering

    [Test]
    public async Task Hiding_a_pod_hides_the_lines_it_already_sent_and_showing_it_brings_them_back()
    {
        var pane = Pane();
        var noisy = pane.RegisterSource("api-noisy", "app");
        var quiet = pane.RegisterSource("api-quiet", "app");

        pane.Enqueue("2026-08-17T10:00:01.000Z chatter", noisy);
        pane.Enqueue("2026-08-17T10:00:02.000Z the interesting one", quiet);
        pane.Flush(force: true);

        noisy.IsIncluded = false;
        await Assert.That(Rendered(pane)).IsEqualTo("quiet:the interesting one");

        // Back in place, not appended at the end — the whole buffer is re-projected.
        noisy.IsIncluded = true;
        await Assert.That(Rendered(pane)).IsEqualTo("noisy:chatter | quiet:the interesting one");
    }

    [Test]
    public async Task The_text_filter_matches_the_message_and_composes_with_the_pod_filter()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        var b = pane.RegisterSource("api-b", "app");

        pane.Enqueue("2026-08-17T10:00:01.000Z ERROR upstream reset", a);
        pane.Enqueue("2026-08-17T10:00:02.000Z GET /healthz 200", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z ERROR upstream reset", b);
        pane.Flush(force: true);

        pane.LogSearchText = "upstream";
        await Assert.That(Rendered(pane)).IsEqualTo("a:ERROR upstream reset | b:ERROR upstream reset");

        b.IsIncluded = false;
        await Assert.That(Rendered(pane)).IsEqualTo("a:ERROR upstream reset");
    }

    /// <summary>
    /// The filter hides lines; it must not drop them. This is UI rule 13's invariant in
    /// the log pane: the buffer is the streams' own complete record and
    /// <c>LogLines</c> is the rendered projection of it, so a line that arrives
    /// <em>while</em> its pod is hidden still has to be buffered — otherwise re-including
    /// that pod shows only what it says from then on, and the minutes you hid it for are
    /// gone with no indication that anything is missing. The line arriving during the
    /// hidden window is what makes this fail if the filter is applied on the way in
    /// rather than on the way out.
    /// </summary>
    [Test]
    public async Task Lines_that_arrive_while_a_pod_is_hidden_are_still_buffered()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        var b = pane.RegisterSource("api-b", "app");

        pane.Enqueue("2026-08-17T10:00:01.000Z before hiding", a);
        pane.Flush(force: true);
        a.IsIncluded = false;

        // Arrives while a is hidden: not rendered, but recorded.
        pane.Enqueue("2026-08-17T10:00:02.000Z while hidden", a);
        pane.Enqueue("2026-08-17T10:00:03.000Z from b", b);
        pane.Flush(force: true);

        await Assert.That(Rendered(pane)).IsEqualTo("b:from b");
        await Assert.That(pane.Summary).IsEqualTo("1 of 2 pods · 3 lines");

        a.IsIncluded = true;
        await Assert.That(Rendered(pane))
            .IsEqualTo("a:before hiding | a:while hidden | b:from b");
    }

    // --------------------------------------------------------------- states, tail

    /// <summary>
    /// "Nothing has arrived", "the filter matched nothing" and "every pod is hidden" are
    /// three different next steps and used to be one blank rectangle (UI rule 9).
    /// </summary>
    [Test]
    public async Task Each_empty_state_names_itself()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");

        await Assert.That(pane.LogPlaceholder).IsNotNull();
        await Assert.That(pane.LogPlaceholder!).Contains("waiting for output");

        pane.Enqueue("2026-08-17T10:00:01.000Z hello", a);
        pane.Flush(force: true);
        await Assert.That(pane.LogPlaceholder).IsNull();

        pane.LogSearchText = "nothing matches this";
        await Assert.That(pane.LogPlaceholder!).Contains("No lines match");

        pane.LogSearchText = "";
        a.IsIncluded = false;
        await Assert.That(pane.LogPlaceholder!).Contains("Every pod is hidden");
    }

    /// <summary>
    /// The tail-lines decision, stated as arithmetic. The pane's buffer is shared by
    /// every pod in it, so the per-pod fetch is the budget divided by the pod count —
    /// never more than the single-pod pane's own 200 (widening the window is a separate,
    /// unbuilt control that belongs to both panes), and never so small that a replica
    /// contributes nothing.
    /// </summary>
    [Test]
    public async Task The_per_pod_tail_divides_the_panes_budget_and_is_clamped_at_both_ends()
    {
        // One pod: the single-pod pane's own value, not the whole 4000-line buffer.
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(4000, 1)).IsEqualTo(200);

        // Twenty pods: 4000/20, which fills the buffer exactly once with no pod's
        // backfill trimmed away by another's.
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(4000, 20)).IsEqualTo(200);
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(4000, 40)).IsEqualTo(100);

        // Past the point where division would leave a replica with nothing readable, the
        // floor takes over and the buffer cap does the trimming instead.
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(4000, 500))
            .IsEqualTo(WorkloadLogsTabViewModel.MinPerPodTailLines);

        // A hand-lowered LogBufferLines must not produce a negative or zero fetch.
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(200, 50))
            .IsEqualTo(WorkloadLogsTabViewModel.MinPerPodTailLines);
        await Assert.That(WorkloadLogsTabViewModel.PerPodTailLines(4000, 0)).IsEqualTo(200);
    }

    /// <summary>
    /// The pane's key is cluster-qualified for the same reason pod detail's is: two
    /// clusters in an aggregated fleet list routinely hold a Deployment with the same
    /// namespace and name, and an unqualified key silently hands the second one the
    /// first one's pane.
    /// </summary>
    [Test]
    public async Task The_tab_key_is_cluster_and_kind_qualified()
    {
        var here = WorkloadLogsTabViewModel.KeyFor("", DeploymentDescriptor, "payments", "api");
        var there = WorkloadLogsTabViewModel.KeyFor("eu-prod", DeploymentDescriptor, "payments", "api");
        var statefulSet = WorkloadLogsTabViewModel.KeyFor(
            "", new ResourceDescriptor("apps", "v1", "StatefulSet", "statefulsets", "statefulset", true, [], []),
            "payments", "api");

        await Assert.That(here).IsNotEqualTo(there);
        await Assert.That(here).IsNotEqualTo(statefulSet);
    }
}
