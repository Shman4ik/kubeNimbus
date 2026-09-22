using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The list's "unhealthy only" mode (<see cref="ClusterTabViewModel.IsUnhealthyOnly"/>),
/// held to the same invariant as the name search in <see cref="ClusterTabRowFilterTests"/>:
/// <c>Rows</c> is the watch's own complete list and only <c>VisibleRows</c> is narrowed.
///
/// <para>
/// This filter has one way of going wrong that the name search never had, and it is the
/// reason most of these tests exist. A name does not change under an object, so the name
/// search only ever had to be evaluated when a row was added. Health does change — a
/// Modified is exactly how a pod becomes CrashLoopBackOff — and the watch updates the row
/// object <em>in place</em>, so nothing reaches the <c>Rows</c>→<c>VisibleRows</c> mirror.
/// An implementation that only filtered on add, or only on rebuild, renders correctly in
/// every screenshot and then misses the one event the mode is for.
/// </para>
/// </summary>
public class ClusterTabHealthFilterTests
{
    private static readonly ResourceDescriptor WidgetDescriptor =
        new("example.com", "v1", "Widget", "widgets", "widget", Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>A healthy pod (Running, ready).</summary>
    private static DynamicResource Healthy(string name, string ns = "payments") => TestObjects.Pod(ns, name);

    /// <summary>A degraded pod (Running, not ready) — the warn verdict.</summary>
    private static DynamicResource Degraded(string name, string ns = "payments") =>
        TestObjects.Pod(ns, name, ready: false);

    /// <summary>A failed pod — the error verdict.</summary>
    private static DynamicResource Failed(string name, string ns = "payments") =>
        TestObjects.Pod(ns, name, phase: "Failed");

    private static ClusterTabViewModel PodTab()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        return tab;
    }

    /// <summary>api (ok), cache (error), web (ok), worker (warn) — in that arrival order.</summary>
    private static ClusterTabViewModel SeededPodTab()
    {
        var tab = PodTab();
        tab.Apply(TestObjects.Added(Healthy("api")));
        tab.Apply(TestObjects.Added(Failed("cache")));
        tab.Apply(TestObjects.Added(Healthy("web")));
        tab.Apply(TestObjects.Added(Degraded("worker")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        return tab;
    }

    // ------------------------------------------------------------ the predicate

    [Test]
    public async Task Warn_and_error_are_unhealthy_and_ok_and_idle_are_not()
    {
        await Assert.That(ResourceHealth.IsUnhealthy(ResourceHealth.Error)).IsTrue();
        await Assert.That(ResourceHealth.IsUnhealthy(ResourceHealth.Warn)).IsTrue();

        // Idle claims nothing (scaled to zero, an unrecognised CRD phase): listing it as
        // a problem would fill the filtered list with things nobody has to fix.
        await Assert.That(ResourceHealth.IsUnhealthy(ResourceHealth.Ok)).IsFalse();
        await Assert.That(ResourceHealth.IsUnhealthy(ResourceHealth.Idle)).IsFalse();

        // And the row reads the same verdict that colours its pill.
        await Assert.That(new ResourceRowViewModel(Failed("x")).IsUnhealthy).IsTrue();
        await Assert.That(new ResourceRowViewModel(Degraded("x")).IsUnhealthy).IsTrue();
        await Assert.That(new ResourceRowViewModel(Healthy("x")).IsUnhealthy).IsFalse();
    }

    [Test]
    public async Task Turning_it_on_narrows_visible_rows_and_leaves_rows_alone()
    {
        var tab = SeededPodTab();
        var before = tab.Rows.ToArray();

        tab.IsUnhealthyOnly = true;

        await Assert.That(tab.IsHealthFiltering).IsTrue();
        await Assert.That(tab.VisibleNames()).IsEqualTo("cache, worker");
        await Assert.That(tab.RowNames()).IsEqualTo("api, cache, web, worker");
        await Assert.That(tab.Rows.SequenceEqual(before)).IsTrue();

        tab.IsUnhealthyOnly = false;
        await Assert.That(tab.VisibleNames()).IsEqualTo("api, cache, web, worker");
    }

    // ------------------------------------------- Modified changes the verdict

    /// <summary>
    /// The headline case. A hidden healthy row that a Modified turns unhealthy has to
    /// appear — and where it would have been all along (arrival order), not appended,
    /// or turning the mode off and on again would produce a different list.
    /// </summary>
    [Test]
    public async Task Modified_that_breaks_a_hidden_row_makes_it_appear_in_arrival_order()
    {
        var tab = SeededPodTab();
        tab.IsUnhealthyOnly = true;
        var api = tab.Rows.Single(r => r.Name == "api");

        tab.Apply(TestObjects.Modified(Failed("api")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api, cache, worker");
        await Assert.That(tab.RowNames()).IsEqualTo("api, cache, web, worker");
        await Assert.That(ReferenceEquals(tab.VisibleRows[0], api)).IsTrue();

        // Same object, updated in place: the informer never lost it.
        await Assert.That(ReferenceEquals(tab.Rows.Single(r => r.Name == "api"), api)).IsTrue();

        // A row between two visible ones lands between them.
        tab.Apply(TestObjects.Modified(Degraded("web")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("api, cache, web, worker");

        // Rebuilding from scratch agrees with what the events produced.
        tab.IsUnhealthyOnly = false;
        tab.IsUnhealthyOnly = true;
        await Assert.That(tab.VisibleNames()).IsEqualTo("api, cache, web, worker");
    }

    [Test]
    public async Task Modified_that_heals_a_shown_row_removes_it_from_view_and_keeps_it_in_rows()
    {
        var tab = SeededPodTab();
        tab.IsUnhealthyOnly = true;
        var cache = tab.Rows.Single(r => r.Name == "cache");

        tab.Apply(TestObjects.Modified(Healthy("cache")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("worker");
        await Assert.That(tab.RowNames()).IsEqualTo("api, cache, web, worker");
        await Assert.That(ReferenceEquals(tab.Rows.Single(r => r.Name == "cache"), cache)).IsTrue();

        // Repeated ticks on a hidden row neither resurface nor duplicate it.
        for (var i = 0; i < 3; i++)
        {
            tab.Apply(TestObjects.Modified(Healthy("cache")));
        }

        await Assert.That(tab.Rows.Count).IsEqualTo(4);
        await Assert.That(tab.VisibleNames()).IsEqualTo("worker");

        tab.IsUnhealthyOnly = false;
        await Assert.That(tab.VisibleNames()).IsEqualTo("api, cache, web, worker");
    }

    /// <summary>
    /// With a sort on, a row that becomes unhealthy is inserted where the sort puts it —
    /// the maintained-sort promise (resource-grid-resize-sort.md, item 7) holds under
    /// this filter too.
    /// </summary>
    [Test]
    public async Task Modified_that_breaks_a_hidden_row_lands_in_sorted_position()
    {
        var tab = SeededPodTab();
        tab.SetSort(ResourceColumn.Name, descending: true, persist: false);
        tab.IsUnhealthyOnly = true;

        await Assert.That(tab.VisibleNames()).IsEqualTo("worker, cache");

        tab.Apply(TestObjects.Modified(Degraded("web")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("worker, web, cache");

        tab.Apply(TestObjects.Modified(Healthy("worker")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("web, cache");

        // Rows keeps arrival order; only the projection is sorted.
        await Assert.That(tab.RowNames()).IsEqualTo("api, cache, web, worker");
    }

    [Test]
    public async Task Added_and_deleted_rows_go_through_the_filter()
    {
        var tab = SeededPodTab();
        tab.IsUnhealthyOnly = true;

        tab.Apply(TestObjects.Added(Healthy("fresh")));
        tab.Apply(TestObjects.Added(Failed("broken")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("cache, worker, broken");

        tab.Apply(TestObjects.Deleted(Failed("cache")));
        tab.Apply(TestObjects.Deleted(Healthy("api")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("worker, broken");
        await Assert.That(tab.RowNames()).IsEqualTo("web, worker, fresh, broken");
    }

    // --------------------------------------------------- composition, fleet

    [Test]
    public async Task It_composes_with_the_name_search()
    {
        var tab = SeededPodTab();
        tab.Apply(TestObjects.Added(Failed("api-canary")));

        tab.RowFilter = "api";
        tab.IsUnhealthyOnly = true;
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-canary");

        tab.RowFilter = "";
        await Assert.That(tab.VisibleNames()).IsEqualTo("cache, worker, api-canary");

        tab.RowFilter = "w";
        await Assert.That(tab.VisibleNames()).IsEqualTo("worker");
    }

    [Test]
    public async Task Fleet_rows_are_filtered_and_follow_their_modifications()
    {
        var tab = PodTab();
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Added(Healthy("api"))));
        tab.ApplyFleet(new FleetResourceEvent("us", TestObjects.Added(Failed("api"))));
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Added(Healthy("cache"))));

        tab.IsUnhealthyOnly = true;
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(1);
        await Assert.That(tab.VisibleRows[0].ClusterName).IsEqualTo("us");

        // The same namespace/name on another cluster is another row, and breaking it
        // must not touch the us row — nor heal it.
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Modified(Degraded("api"))));
        await Assert.That(string.Join(", ", tab.VisibleRows.Select(r => $"{r.ClusterName}/{r.Name}")))
            .IsEqualTo("eu/api, us/api");

        tab.ApplyFleet(new FleetResourceEvent("us", TestObjects.Modified(Healthy("api"))));
        await Assert.That(string.Join(", ", tab.VisibleRows.Select(r => $"{r.ClusterName}/{r.Name}")))
            .IsEqualTo("eu/api");
        await Assert.That(tab.Rows.Count).IsEqualTo(3);

        // A cluster-scoped relist removes only that cluster's rows from both lists.
        tab.ApplyFleet(new FleetResourceEvent("eu", ResourceEvent<DynamicResource>.Reset));
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(0);
        await Assert.That(tab.Rows.Count).IsEqualTo(1);
    }

    // ------------------------------------------------ a kind with no verdict

    /// <summary>
    /// A ConfigMap has no status summary, so every row is idle and "unhealthy only" could
    /// only ever show an empty list. The toggle is disabled there instead — and the mode
    /// itself survives, so it applies again on the next kind that has a verdict.
    /// </summary>
    [Test]
    public async Task A_kind_without_a_health_verdict_disables_it_without_emptying_the_list()
    {
        var tab = TestObjects.Tab();
        var pods = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        var configMaps = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        var section = new SidebarSectionViewModel("Workloads");
        section.Kinds.Add(pods);
        section.Kinds.Add(configMaps);
        tab.SidebarSections.Add(section);

        tab.SelectKindCommand.Execute(pods);
        await Assert.That(tab.CanFilterUnhealthy).IsTrue();
        tab.IsUnhealthyOnly = true;
        tab.RowFilter = "something";

        tab.SelectKindCommand.Execute(configMaps);

        await Assert.That(tab.CanFilterUnhealthy).IsFalse();
        await Assert.That(tab.IsUnhealthyOnly).IsTrue();      // a mode: kept across kinds
        await Assert.That(tab.IsHealthFiltering).IsFalse();   // …but not applied here
        await Assert.That(tab.RowFilter).IsEqualTo("");       // unlike the search, which clears
        await Assert.That(tab.UnhealthyToggleTip).Contains("no health status");

        tab.Apply(TestObjects.Added(TestObjects.ConfigMap("payments", "settings")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.VisibleNames()).IsEqualTo("settings");
        await Assert.That(tab.IsHealthFilterEmpty).IsFalse();
        await Assert.That(tab.RowFilterSummary).IsEqualTo("");

        tab.SelectKindCommand.Execute(pods);
        await Assert.That(tab.IsHealthFiltering).IsTrue();
    }

    [Test]
    public async Task No_kind_selected_means_nothing_to_filter()
    {
        var tab = TestObjects.Tab();
        await Assert.That(tab.CanFilterUnhealthy).IsFalse();

        tab.IsUnhealthyOnly = true;
        tab.Apply(TestObjects.Added(Healthy("api")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("api");
    }

    // ------------------------------------------------------ the empty states

    [Test]
    public async Task All_healthy_is_its_own_empty_state()
    {
        var tab = PodTab();
        tab.IsListLoading = true;
        tab.IsUnhealthyOnly = true;
        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        // Loading wins over every verdict (UI rule 18) — including a row already here.
        tab.Apply(TestObjects.Added(Healthy("api")));
        tab.IsListLoading = true;
        await Assert.That(tab.IsHealthFilterEmpty).IsFalse();
        await Assert.That(tab.IsFilterEmpty).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();

        tab.Apply(TestObjects.Added(Healthy("web")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.IsHealthFilterEmpty).IsTrue();
        await Assert.That(tab.IsFilterEmpty).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.HealthFilterEmptyTitle).IsEqualTo("Nothing unhealthy among 2 Pods");
        await Assert.That(tab.RowFilterSummary).IsEqualTo("0 of 2 unhealthy");

        // Something breaks: the good-news state goes away and the row appears.
        tab.Apply(TestObjects.Modified(Failed("web")));
        await Assert.That(tab.IsHealthFilterEmpty).IsFalse();
        await Assert.That(tab.VisibleNames()).IsEqualTo("web");
        await Assert.That(tab.RowFilterSummary).IsEqualTo("1 of 2 unhealthy");

        // The way back.
        tab.Apply(TestObjects.Modified(Healthy("web")));
        await Assert.That(tab.IsHealthFilterEmpty).IsTrue();
        tab.ShowAllRowsCommand.Execute(null);
        await Assert.That(tab.IsUnhealthyOnly).IsFalse();
        await Assert.That(tab.IsHealthFilterEmpty).IsFalse();
        await Assert.That(tab.VisibleNames()).IsEqualTo("api, web");
        await Assert.That(tab.RowFilterSummary).IsEqualTo("");
    }

    /// <summary>
    /// With the search box in play the two narrowing empty states split on whether the
    /// search matched anything: a search that matches nothing is the search's empty state
    /// (the typo is what to fix); a search that matches only healthy rows is this one.
    /// And an empty namespace is neither.
    /// </summary>
    [Test]
    public async Task Search_and_health_empty_states_stay_distinct()
    {
        var tab = SeededPodTab();
        tab.IsUnhealthyOnly = true;

        tab.RowFilter = "no-such-pod";
        await Assert.That(tab.IsFilterEmpty).IsTrue();
        await Assert.That(tab.IsHealthFilterEmpty).IsFalse();

        tab.RowFilter = "api";
        await Assert.That(tab.IsFilterEmpty).IsFalse();
        await Assert.That(tab.IsHealthFilterEmpty).IsTrue();
        await Assert.That(tab.HealthFilterEmptyTitle).IsEqualTo("Nothing unhealthy among 1 Pods");
        await Assert.That(tab.HealthFilterEmptyDetail).Contains("matching “api”");

        var empty = PodTab();
        empty.IsUnhealthyOnly = true;
        empty.Apply(ResourceEvent<DynamicResource>.Reset);
        empty.Apply(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(empty.IsListEmpty).IsTrue();
        await Assert.That(empty.IsHealthFilterEmpty).IsFalse();
        await Assert.That(empty.IsFilterEmpty).IsFalse();
    }

    /// <summary>
    /// A CRD whose objects carry no status this app can read passes the kind gate (it is
    /// not a known statusless kind) and then judges nothing — the empty state says so
    /// rather than claiming the objects are healthy.
    /// </summary>
    [Test]
    public async Task A_list_where_nothing_reports_a_status_says_so()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(WidgetDescriptor, "crd");
        await Assert.That(tab.CanFilterUnhealthy).IsTrue();

        tab.IsUnhealthyOnly = true;
        tab.Apply(TestObjects.Added(Widget("blue")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.IsHealthFilterEmpty).IsTrue();
        await Assert.That(tab.HealthFilterEmptyDetail).IsEqualTo("None of them reports a status this list can judge");
    }

    private static DynamicResource Widget(string name)
    {
        var json = $$"""
        {
          "apiVersion": "example.com/v1",
          "kind": "Widget",
          "metadata": { "name": "{{name}}", "namespace": "payments", "uid": "w-{{name}}",
                        "creationTimestamp": "2026-08-01T10:00:00Z" },
          "spec": { "colour": "{{name}}" }
        }
        """;

        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }
}
