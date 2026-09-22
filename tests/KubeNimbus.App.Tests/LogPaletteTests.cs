using System.Net;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// L1 — the palette's "Logs: …" rows: what the one-shot list turns into (pods and
/// workloads, the cap, a refused list), how the palette shows them (the logs prefix, the
/// notes, selection that never lands on a note), and that a row opens the same pane the
/// list's L key does.
///
/// <para>
/// The loader is driven through <see cref="LogTargetSource"/> stand-ins rather than a
/// cluster: a 403 and a truncated list are exactly the states a sandbox will not produce
/// on demand, and they are the ones whose failure is a silent empty palette.
/// </para>
/// </summary>
public class LogPaletteTests
{
    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static readonly ResourceDescriptor StatefulSets =
        new("apps", "v1", "StatefulSet", "statefulsets", "statefulset", Namespaced: true, ShortNames: ["sts"], Categories: []);

    private static readonly ResourceDescriptor DaemonSets =
        new("apps", "v1", "DaemonSet", "daemonsets", "daemonset", Namespaced: true, ShortNames: ["ds"], Categories: []);

    private static readonly IReadOnlyList<ResourceDescriptor> Catalog =
        [TestObjects.PodDescriptor, Deployments, StatefulSets, DaemonSets];

    private static DynamicResource Workload(string kind, string @namespace, string name, string selector = """{ "matchLabels": { "app": "x" } }""")
    {
        using var document = JsonDocument.Parse($$"""
            {
              "apiVersion": "apps/v1",
              "kind": "{{kind}}",
              "metadata": { "name": "{{name}}", "namespace": "{{@namespace}}", "uid": "{{name}}" },
              "spec": { "replicas": 3, "selector": {{selector}} },
              "status": { "readyReplicas": 2 }
            }
            """);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>A stand-in cluster: fixed objects per kind, optional failures, and a count of what was listed.</summary>
    private sealed class FakeCluster
    {
        public Dictionary<string, List<DynamicResource>> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);

        public List<string> Listed { get; } = [];

        public bool CatalogFails { get; set; }

        public LogTargetSource Source(string clusterName = "", IReadOnlyList<DynamicResource>? knownPods = null) =>
            new(
                clusterName,
                null,
                _ => CatalogFails
                    ? Task.FromException<IReadOnlyList<ResourceDescriptor>>(new HttpRequestException("discovery down"))
                    : Task.FromResult(Catalog),
                (descriptor, _, cap, _) =>
                {
                    lock (Listed)
                    {
                        Listed.Add(descriptor.Kind);
                    }

                    if (Failures.TryGetValue(descriptor.Kind, out var failure))
                    {
                        return Task.FromException<CappedResourceList>(failure);
                    }

                    var all = Objects.GetValueOrDefault(descriptor.Kind) ?? [];
                    return Task.FromResult(new CappedResourceList([.. all.Take(cap)], all.Count > cap));
                },
                knownPods);
    }

    private static HttpRequestException Forbidden(string what) =>
        new($"{what} is forbidden: User \"dev\" cannot list resource", null, HttpStatusCode.Forbidden);

    private static FakeCluster Payments()
    {
        var cluster = new FakeCluster();
        cluster.Objects["Pod"] =
        [
            TestObjects.Pod("payments", "checkout-worker-7d9f-abcde"),
            TestObjects.Pod("payments", "api-5c6d-xyz12", phase: "Pending", ready: false),
        ];
        cluster.Objects["Deployment"] = [Workload("Deployment", "payments", "checkout-worker")];
        cluster.Objects["StatefulSet"] =
        [
            Workload("StatefulSet", "payments", "ledger-db"),
            // An empty selector names no pods — refused rather than read as "all of them",
            // exactly as the list's own "Logs (all pods)" refuses it.
            Workload("StatefulSet", "payments", "selects-everything", selector: "{}"),
        ];
        return cluster;
    }

    // ------------------------------------------------------------------ the loader

    [Test]
    public async Task Pods_and_workloads_are_listed_workloads_first_and_a_selectorless_one_is_left_out()
    {
        var cluster = Payments();

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], "payments");

        var names = string.Join(", ", result.Targets.Select(t => $"{t.Descriptor.Kind}/{t.Resource.Name}"));
        await Assert.That(names).IsEqualTo(
            "Deployment/checkout-worker, StatefulSet/ledger-db, Pod/api-5c6d-xyz12, Pod/checkout-worker-7d9f-abcde");
        await Assert.That(result.Problems.Count).IsEqualTo(0);
        await Assert.That(result.Truncations.Count).IsEqualTo(0);
        await Assert.That(cluster.Listed.OrderBy(k => k, StringComparer.Ordinal))
            .IsEquivalentTo(["DaemonSet", "Deployment", "Pod", "StatefulSet"]);
    }

    [Test]
    public async Task A_list_that_hits_the_cap_is_capped_and_says_so()
    {
        var cluster = Payments();
        cluster.Objects["Pod"] = [.. Enumerable.Range(0, 7).Select(i => TestObjects.Pod("payments", $"pod-{i}"))];

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], "payments", maxPerKind: 5);

        await Assert.That(result.Targets.Count(t => t.IsPod)).IsEqualTo(5);
        await Assert.That(result.Truncations).IsEquivalentTo(["only the first 5 pods in payments are listed"]);
    }

    [Test]
    public async Task The_list_already_on_screen_is_reused_instead_of_listing_pods_again_and_is_capped_too()
    {
        var cluster = Payments();
        IReadOnlyList<DynamicResource> onScreen = [.. Enumerable.Range(0, 3).Select(i => TestObjects.Pod("payments", $"shown-{i}"))];

        var result = await LogTargetLoader.LoadAsync([cluster.Source(knownPods: onScreen)], "payments", maxPerKind: 2);

        await Assert.That(cluster.Listed).DoesNotContain("Pod");
        await Assert.That(result.Targets.Where(t => t.IsPod).Select(t => t.Resource.Name)).IsEquivalentTo(["shown-0", "shown-1"]);
        await Assert.That(result.Truncations.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_pod_list_is_a_stated_permission_problem_and_the_workloads_still_come()
    {
        var cluster = Payments();
        cluster.Failures["Pod"] = Forbidden("pods");

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], "payments");

        await Assert.That(result.Targets.Any(t => t.IsPod)).IsFalse();
        await Assert.That(result.Targets.Count).IsEqualTo(2);
        var problem = result.Problems.Single();
        await Assert.That(problem.IsForbidden).IsTrue();
        await Assert.That(problem.Summary).IsEqualTo("not allowed to list pods in payments");
        await Assert.That(problem.Detail).Contains("forbidden");
    }

    [Test]
    public async Task Three_refused_workload_kinds_are_one_problem_not_three()
    {
        var cluster = Payments();
        cluster.Failures["Deployment"] = Forbidden("deployments");
        cluster.Failures["StatefulSet"] = Forbidden("statefulsets");
        cluster.Failures["DaemonSet"] = Forbidden("daemonsets");

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], null);

        await Assert.That(result.Problems.Select(p => p.Summary)).IsEquivalentTo(["not allowed to list deployments, statefulsets and daemonsets in every namespace"]);
        await Assert.That(result.Targets.Count(t => t.IsPod)).IsEqualTo(2);
    }

    [Test]
    public async Task A_failure_that_is_not_a_403_is_reported_as_an_error_not_a_permission()
    {
        var cluster = Payments();
        cluster.Failures["Pod"] = new HttpRequestException("Connection refused (127.0.0.1:1)");

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], "payments");

        var problem = result.Problems.Single();
        await Assert.That(problem.IsForbidden).IsFalse();
        await Assert.That(problem.Summary).IsEqualTo("couldn't list pods in payments");
    }

    [Test]
    public async Task An_unreadable_catalog_still_lists_pods_through_the_well_known_descriptor()
    {
        var cluster = Payments();
        cluster.CatalogFails = true;

        var result = await LogTargetLoader.LoadAsync([cluster.Source()], "payments");

        await Assert.That(result.Targets.All(t => t.IsPod)).IsTrue();
        await Assert.That(result.Targets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task In_a_fleet_every_target_and_every_problem_names_its_cluster()
    {
        var prod = Payments();
        var staging = Payments();
        staging.Failures["Deployment"] = Forbidden("deployments");

        var result = await LogTargetLoader.LoadAsync([prod.Source("prod-eu"), staging.Source("staging-eu")], "payments");

        await Assert.That(result.Targets.Count(t => t.ClusterName == "prod-eu")).IsEqualTo(4);
        await Assert.That(result.Targets.Count(t => t.ClusterName == "staging-eu")).IsEqualTo(3);
        await Assert.That(result.Problems.Single().Summary).IsEqualTo("staging-eu: not allowed to list deployments in payments");

        var rows = LogPaletteRows.Build(result.Targets, _ => { });
        await Assert.That(rows.All(r => r.Subtitle.EndsWith(" · prod-eu", StringComparison.Ordinal)
                                        || r.Subtitle.EndsWith(" · staging-eu", StringComparison.Ordinal))).IsTrue();
    }

    // ------------------------------------------------------------------ the rows

    [Test]
    public async Task A_row_names_the_object_its_namespace_and_its_state_and_matches_on_identity_only()
    {
        var pod = new LogTarget(TestObjects.Pod("payments", "api-5c6d-xyz12", phase: "Pending", ready: false),
            TestObjects.PodDescriptor, "", null);
        var deployment = new LogTarget(Workload("Deployment", "payments", "checkout-worker"), Deployments, "", null);

        var podRow = LogPaletteRows.Row(pod, _ => { });
        var deploymentRow = LogPaletteRows.Row(deployment, _ => { });

        await Assert.That(podRow.Title).IsEqualTo("Logs: api-5c6d-xyz12");
        await Assert.That(podRow.Subtitle).StartsWith("payments · ");
        await Assert.That(podRow.Scope).IsEqualTo(PaletteScope.Logs);
        await Assert.That(deploymentRow.Title).IsEqualTo("Logs: Deployment/checkout-worker");
        await Assert.That(deploymentRow.Subtitle).IsEqualTo("payments · 2/3 ready · every pod, one stream");

        // Status is shown, not searched (UI rule 13's reason): "Running" or "Pending"
        // would otherwise match most of a namespace.
        await Assert.That(podRow.SearchText!).DoesNotContain(podRow.Subtitle.Split(" · ")[1]);
        await Assert.That(podRow.SearchText!).Contains("payments");
    }

    [Test]
    public async Task The_notes_cover_disconnected_loading_refused_capped_and_empty()
    {
        static string Titles(LogTargetsState state) => string.Join(" | ", LogPaletteRows.Notes(state).Select(n => n.Title));

        await Assert.That(Titles(new(false, false, false, "payments", null))).IsEqualTo("Logs: not connected");
        await Assert.That(Titles(new(false, true, false, "payments", null))).IsEqualTo("Logs: waiting for the cluster to connect");
        await Assert.That(Titles(new(true, false, true, "payments", null))).IsEqualTo("Loading pods and workloads in payments…");
        await Assert.That(Titles(new(true, false, false, "payments", LogTargetList.Empty))).IsEqualTo("No pods or workloads in payments");

        var refused = new LogTargetList([], [new("not allowed to list pods in payments", "pods is forbidden", true)], ["only the first 2,000 deployments in payments are listed"]);
        await Assert.That(Titles(new(true, false, false, "payments", refused))).IsEqualTo(
            "Logs: not allowed to list pods in payments | Logs: only the first 2,000 deployments in payments are listed");

        // Every note is a note: nothing to run, so nothing Enter can do.
        await Assert.That(LogPaletteRows.Notes(new(true, false, false, "payments", refused)).All(n => n.IsNote)).IsTrue();
    }

    // ------------------------------------------------------------------ the palette

    private static CommandPaletteViewModel Palette(params PaletteItem[] items) => new(() => items);

    private static PaletteItem Command(string title) => new(title, "command", "CogIconGeometry", () => { });

    private static PaletteItem LogRow(string name) =>
        LogPaletteRows.Row(new LogTarget(TestObjects.Pod("payments", name), TestObjects.PodDescriptor, "", null), _ => { });

    [Test]
    public async Task The_logs_prefix_narrows_the_palette_to_log_rows_and_their_notes()
    {
        var loading = PaletteItem.Note("Loading pods…", "", PaletteScope.Logs);
        var palette = Palette(Command("Preferences…"), Command("Logs"), LogRow("checkout-1"), LogRow("api-1"), loading);

        palette.Open(CommandPaletteViewModel.LogsPrefix + "check");

        await Assert.That(string.Join(" | ", palette.FilteredItems.Select(i => i.Title)))
            .IsEqualTo("Loading pods… | Logs: checkout-1");
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: checkout-1");
    }

    [Test]
    public async Task Outside_the_prefix_a_log_row_is_found_by_name_and_a_note_speaks_only_when_nothing_matched()
    {
        var loading = PaletteItem.Note("Loading pods…", "", PaletteScope.Logs);
        var palette = Palette(Command("Preferences…"), LogRow("checkout-1"), loading);

        palette.Open("checkout");
        await Assert.That(palette.FilteredItems.Select(i => i.Title)).IsEquivalentTo(["Logs: checkout-1"]);

        palette.Query = "prefer";
        await Assert.That(palette.FilteredItems.Select(i => i.Title)).IsEquivalentTo(["Preferences…"]);

        palette.Query = "nothing-called-this";
        await Assert.That(palette.FilteredItems.Select(i => i.Title)).IsEquivalentTo(["Loading pods…"]);
        await Assert.That(palette.SelectedItem).IsNull();

        // Enter with only a note on screen does nothing and leaves the palette open.
        palette.ExecuteSelected();
        await Assert.That(palette.IsOpen).IsTrue();
    }

    [Test]
    public async Task A_note_can_never_be_the_selection()
    {
        var note = PaletteItem.Note("Loading pods…", "", PaletteScope.Logs);
        var palette = Palette(note, LogRow("a"), LogRow("b"));
        palette.Open(CommandPaletteViewModel.LogsPrefix);

        palette.SelectedItem = note;
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: a");

        palette.MoveSelection(-1);
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: a");
        palette.MoveSelection(1);
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: b");
    }

    [Test]
    public async Task More_matches_than_are_drawn_end_in_a_keep_typing_note()
    {
        var rows = Enumerable.Range(0, CommandPaletteViewModel.MaxRows + 12).Select(i => LogRow($"pod-{i:D3}")).ToArray();
        var palette = Palette(rows);

        palette.Open(CommandPaletteViewModel.LogsPrefix);

        await Assert.That(palette.FilteredItems.Count(i => i.IsActionable)).IsEqualTo(CommandPaletteViewModel.MaxRows);
        await Assert.That(palette.FilteredItems[^1].Title).IsEqualTo("12 more match");
    }

    [Test]
    public async Task Rows_arriving_while_the_palette_is_open_keep_the_query_and_the_selection()
    {
        var items = new List<PaletteItem> { LogRow("checkout-b"), LogRow("checkout-c") };
        var palette = new CommandPaletteViewModel(() => items);
        palette.Open(CommandPaletteViewModel.LogsPrefix + "checkout");
        palette.MoveSelection(1);

        items.Insert(0, LogRow("checkout-a"));
        palette.Refresh();

        await Assert.That(palette.Query).IsEqualTo(CommandPaletteViewModel.LogsPrefix + "checkout");
        await Assert.That(palette.FilteredItems.Count).IsEqualTo(3);
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: checkout-c");
    }

    [Test]
    public async Task A_row_that_narrows_the_palette_keeps_it_open()
    {
        CommandPaletteViewModel? palette = null;
        var narrow = new PaletteItem("Logs: find a pod or workload…", "", "ClockOutlineIconGeometry",
            () => palette!.Query = CommandPaletteViewModel.LogsPrefix) { KeepsPaletteOpen = true };
        palette = Palette(narrow, LogRow("a"));
        palette.Open("find");

        palette.ExecuteSelected();

        await Assert.That(palette.IsOpen).IsTrue();
        await Assert.That(palette.Query).IsEqualTo(CommandPaletteViewModel.LogsPrefix);
        await Assert.That(palette.SelectedItem!.Title).IsEqualTo("Logs: a");
    }

    // ------------------------------------------------------------------ the tab

    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    [Test]
    public async Task On_the_demo_cluster_the_rows_come_from_the_dataset_at_once()
    {
        var tab = DemoTab();
        var landed = 0;
        tab.LogTargetsChanged = () => landed++;

        tab.RequestLogTargets();

        await Assert.That(landed).IsEqualTo(1);
        await Assert.That(tab.LogTargetsState.IsLoading).IsFalse();
        await Assert.That(tab.LogTargetRows.Any(r => r.Title.StartsWith("Logs: Deployment/", StringComparison.Ordinal))).IsTrue();
        var podRows = tab.LogTargetRows.Count(r => r.IconKey == LogPaletteRows.PodIcon);
        await Assert.That(podRows).IsEqualTo(tab.Rows.Count);
    }

    [Test]
    public async Task A_pod_row_opens_pod_detail_on_logs_and_a_second_open_reuses_the_tab()
    {
        var tab = DemoTab();
        tab.RequestLogTargets();
        var row = tab.LogTargetRows.First(r => r.IconKey == LogPaletteRows.PodIcon);

        row.Execute!();
        row.Execute!();

        var detail = tab.SelectedInspectorTab as PodDetailTabViewModel;
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.SelectedDetailTabIndex).IsEqualTo(0);
        await Assert.That(detail.IsPreview).IsFalse();
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_workload_row_opens_the_one_stream_pane_like_the_list_key_does()
    {
        var tab = DemoTab();
        tab.RequestLogTargets();
        var row = tab.LogTargetRows.First(r => r.Title.StartsWith("Logs: Deployment/", StringComparison.Ordinal));

        row.Execute!();

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<WorkloadLogsTabViewModel>();
    }

    [Test]
    public async Task The_list_key_and_the_palette_open_the_same_tab()
    {
        var tab = DemoTab();
        tab.SelectedRow = tab.Rows.First();
        tab.OpenLogsCommand.Execute(null);
        var fromKey = tab.SelectedInspectorTab;

        tab.RequestLogTargets();
        tab.LogTargetRows.First(r => r.Title == $"Logs: {tab.Rows.First().Name}").Execute!();

        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(fromKey);
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_disconnected_tab_lists_nothing_and_says_why()
    {
        var tab = TestObjects.Tab();

        tab.RequestLogTargets();

        await Assert.That(tab.LogTargetRows.Count).IsEqualTo(0);
        await Assert.That(LogPaletteRows.Notes(tab.LogTargetsState).Single().Title).IsEqualTo("Logs: not connected");
    }

    [Test]
    public async Task Stale_rows_stay_while_the_same_scope_reloads_and_go_when_the_namespace_changes()
    {
        var tab = DemoTab();
        tab.RequestLogTargets();
        var count = tab.LogTargetRows.Count;
        var result = new LogTargetList(
            [.. Demo.DemoData.Pods.Select(p => new LogTarget(p, TestObjects.PodDescriptor, "", null))], [], []);

        tab.SetLogTargetsForFixture(result, loading: true);
        await Assert.That(tab.LogTargetRows.Count).IsEqualTo(result.Targets.Count);
        await Assert.That(LogPaletteRows.Notes(tab.LogTargetsState)[0].Title).StartsWith("Loading pods and workloads in ");
        await Assert.That(count).IsGreaterThan(0);

        tab.SelectedNamespace = tab.NamespaceOptions.First(n => n != tab.SelectedNamespace);
        await Assert.That(tab.LogTargetRows.Count).IsEqualTo(0);
    }
}
