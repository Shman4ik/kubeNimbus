using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// UI rule 13's central invariant: <c>Rows</c> stays the watch's own complete list and
/// <c>VisibleRows</c> is the rendered projection through <c>RowFilter</c>.
///
/// <para>
/// Worth stating why this needs a test at all. The two collections agree with each other
/// in every state a screenshot can capture, so nothing in the harness — the only other
/// automated check the App layer has — can tell a correct implementation from one that
/// filters <c>Rows</c> in place. The difference only shows up on the *next* watch event:
/// with <c>Rows</c> filtered, the informer's key map has lost the hidden object, so a
/// Modified for it finds no entry and reads as a fresh add. The row then reappears in
/// the middle of a filtered list, which looks like a watch bug (rows arriving that were
/// never asked for) and is a filter bug. That is the failure these tests exist to catch,
/// and it is why the assertions below are as much about <c>Rows</c> and row *identity*
/// as about what is on screen.
/// </para>
///
/// <para>
/// These drive the real <see cref="ClusterTabViewModel.Apply"/> — the same method the
/// watch pump posts each frame to — rather than a stand-in for the mirroring logic. A
/// reproduction would pin nothing: the bug being guarded against is precisely one that a
/// second implementation would not have.
/// </para>
/// </summary>
public class ClusterTabRowFilterTests
{
    private static ClusterTabViewModel SeededTab(out ClusterTabViewModel tab)
    {
        tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "api-7f9")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "cache-0")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("shop", "web-1")));
        return tab;
    }

    // ------------------------------------------ the Modified-while-filtered case

    /// <summary>
    /// The item's headline case. With a filter on, a Modified for an object the filter
    /// excludes must update the row in place and change nothing else: the row must not
    /// appear in <c>VisibleRows</c>, must not be duplicated in <c>Rows</c>, and must
    /// still be the same row object it was before (an object identity change means the
    /// informer lost it and rebuilt it, which is the bug wearing a disguise).
    /// </summary>
    [Test]
    public async Task Modified_for_a_filtered_out_row_neither_resurfaces_nor_duplicates_it()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");

        // Rows is the informer's view, not the screen's: filtering must not shrink it.
        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, cache-0, web-1");

        var hidden = tab.Rows.Single(r => r.Name == "cache-0");

        tab.Apply(TestObjects.Modified(TestObjects.Pod("payments", "cache-0", phase: "Failed")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");
        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, cache-0, web-1");
        await Assert.That(tab.Rows.Count(r => r.Name == "cache-0")).IsEqualTo(1);

        // Same row object, updated in place — the object was never lost and re-added.
        await Assert.That(ReferenceEquals(tab.Rows.Single(r => r.Name == "cache-0"), hidden)).IsTrue();
        await Assert.That(hidden.Status).IsEqualTo("Failed");
    }

    /// <summary>
    /// The same event repeated. A key map that has lost the hidden row grows the list by
    /// one row per watch tick, so a busy object behind a filter is what turns the bug
    /// from "a row reappeared" into "the list is full of duplicates".
    /// </summary>
    [Test]
    public async Task Repeated_modifications_of_a_filtered_out_row_do_not_grow_the_list()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        for (var i = 0; i < 5; i++)
        {
            tab.Apply(TestObjects.Modified(TestObjects.Pod("payments", "cache-0", phase: "Running")));
        }

        await Assert.That(tab.Rows.Count).IsEqualTo(3);
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Clearing the filter has to hand back everything the watch has, including whatever
    /// arrived while the filter was on — the strongest single statement of "Rows is not
    /// what is on screen". An implementation that filtered <c>Rows</c> fails here even if
    /// it somehow kept the screen right: the rows it dropped are simply gone.
    /// </summary>
    [Test]
    public async Task Clearing_the_filter_restores_every_row_including_ones_that_arrived_while_it_was_on()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        tab.Apply(TestObjects.Added(TestObjects.Pod("shop", "worker-2")));
        tab.Apply(TestObjects.Modified(TestObjects.Pod("payments", "cache-0", phase: "Failed")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");

        tab.RowFilter = "";

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, cache-0, web-1, worker-2");
        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, cache-0, web-1, worker-2");
    }

    /// <summary>
    /// A Deleted for a hidden object still has to remove it, or clearing the filter would
    /// bring back a pod that no longer exists. The mirror's Remove arm is a no-op here
    /// (the row was never in <c>VisibleRows</c>) and must stay one.
    /// </summary>
    [Test]
    public async Task Deleted_for_a_filtered_out_row_removes_it_from_rows()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        tab.Apply(TestObjects.Deleted(TestObjects.Pod("payments", "cache-0")));

        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, web-1");
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");

        tab.RowFilter = "";
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, web-1");
    }

    /// <summary>
    /// A watch relist (410 Gone) resets the list while a filter is on. Both collections
    /// have to empty, and the rows that come back must be re-projected through the
    /// filter that is still typed in the box.
    /// </summary>
    [Test]
    public async Task Reset_while_filtering_clears_both_collections_and_refills_through_the_filter()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        await Assert.That(tab.Rows.Count).IsEqualTo(0);
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(0);

        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "api-7f9")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "cache-0")));

        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, cache-0");
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");
    }

    /// <summary>
    /// The fleet path is a second way to get this wrong — same mirror, cluster-qualified
    /// keys — so it gets the same assertion. Two clusters serving the same
    /// namespace/name is the normal case there, and both rows must survive a filter that
    /// hides them.
    /// </summary>
    [Test]
    public async Task Fleet_modified_for_a_filtered_out_row_neither_resurfaces_nor_duplicates_it()
    {
        var tab = TestObjects.Tab();
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Added(TestObjects.Pod("payments", "api-7f9"))));
        tab.ApplyFleet(new FleetResourceEvent("us", TestObjects.Added(TestObjects.Pod("payments", "api-7f9"))));
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Added(TestObjects.Pod("payments", "cache-0"))));

        // Matches on the cluster name, which only fleet rows carry.
        tab.RowFilter = "us";
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(1);
        await Assert.That(tab.Rows.Count).IsEqualTo(3);

        var hidden = tab.Rows.Single(r => r.ClusterName == "eu" && r.Name == "cache-0");

        tab.ApplyFleet(new FleetResourceEvent(
            "eu", TestObjects.Modified(TestObjects.Pod("payments", "cache-0", phase: "Failed"))));

        await Assert.That(tab.Rows.Count).IsEqualTo(3);
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(
            tab.Rows.Single(r => r.ClusterName == "eu" && r.Name == "cache-0"), hidden)).IsTrue();
    }

    // ------------------------------------------------- the mirror, path by path

    /// <summary>
    /// The incremental append arm: a row added at the tail of <c>Rows</c> — which is what
    /// every producer does — lands in <c>VisibleRows</c> only if it matches.
    /// </summary>
    [Test]
    public async Task Appending_to_rows_mirrors_through_the_filter()
    {
        var tab = TestObjects.Tab();
        tab.RowFilter = "api";

        tab.Rows.Add(new ResourceRowViewModel(TestObjects.Pod("payments", "api-7f9")));
        tab.Rows.Add(new ResourceRowViewModel(TestObjects.Pod("payments", "cache-0")));

        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9, cache-0");
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");
    }

    /// <summary>The incremental remove arm, for a visible row and a hidden one alike.</summary>
    [Test]
    public async Task Removing_from_rows_mirrors_through_the_filter()
    {
        SeededTab(out var tab);
        tab.RowFilter = "a"; // api-7f9 and cache-0 (both in "payments"); web-1/shop has no "a"

        var visible = tab.VisibleRows.ToArray();
        foreach (var row in visible)
        {
            tab.Rows.Remove(row);
            await Assert.That(tab.VisibleRows).DoesNotContain(row);
        }

        await Assert.That(tab.VisibleRows.Count).IsEqualTo(0);
        await Assert.That(tab.RowNames()).IsEqualTo("web-1");
    }

    /// <summary>
    /// The rebuild fallback. An insert in the middle and a Clear are not appends or
    /// removes-at-a-known-index, so the mirror rebuilds rather than guessing — and the
    /// result must still be exactly <c>Rows</c> projected through the filter, in order.
    /// </summary>
    [Test]
    public async Task Insert_and_clear_fall_back_to_a_rebuild_that_still_mirrors_rows()
    {
        SeededTab(out var tab);
        tab.RowFilter = "";

        tab.Rows.Insert(1, new ResourceRowViewModel(TestObjects.Pod("payments", "api-zzz")));
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, api-zzz, cache-0, web-1");

        tab.RowFilter = "api";
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, api-zzz");

        tab.Rows.Clear();
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Retyping the filter over a populated list re-projects it, in <c>Rows</c> order,
    /// every time — including the namespace, which <c>Matches</c> covers because
    /// "All namespaces" is the default view.
    /// </summary>
    [Test]
    public async Task Changing_the_filter_with_rows_present_reprojects_in_rows_order()
    {
        SeededTab(out var tab);

        tab.RowFilter = "payments";
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, cache-0");

        tab.RowFilter = "WEB"; // case-insensitive
        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1");

        tab.RowFilter = "   api   "; // trimmed before matching
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9");

        tab.RowFilter = "";
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, cache-0, web-1");

        // Status is deliberately not matched: "Running" would match most of a healthy
        // list, and what people type is a name they half-remember.
        tab.RowFilter = "Running";
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(0);
    }

    // ------------------------------------------- the two empty states, and the box

    /// <summary>
    /// "This namespace has no pods" and "no pod here is called that" send you looking for
    /// opposite problems, so they are two states, never one. And neither may fire while
    /// the initial list is still in flight — that is a third state (loading), which is
    /// what stops the list flashing an empty card on every kind switch.
    /// </summary>
    [Test]
    public async Task Empty_list_and_empty_filter_are_distinct_states()
    {
        var tab = TestObjects.Tab();

        // A watch that starts and syncs an empty namespace: Reset, then no items. This
        // is how the settled-empty state is actually reached — a tab that has never
        // watched anything shows no empty card, which is right (there is no list yet to
        // be empty), so asserting on a bare constructor would be asserting on nothing.
        tab.IsListLoading = true;
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsFilterEmpty).IsFalse();

        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        await Assert.That(tab.IsListEmpty).IsTrue();
        await Assert.That(tab.IsFilterEmpty).IsFalse();

        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "api-7f9")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "cache-0")));

        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsFilterEmpty).IsFalse();

        // Rows exist; the filter matches none of them.
        tab.RowFilter = "nothing-matches-this";
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsFilterEmpty).IsTrue();

        // Still loading: neither state may claim anything yet.
        tab.IsListLoading = true;
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsFilterEmpty).IsFalse();
        tab.IsListLoading = false;

        tab.RowFilter = "api";
        await Assert.That(tab.IsFilterEmpty).IsFalse();
    }

    /// <summary>
    /// The "12 of 87" caption counts the projection against the informer's full list — so
    /// it is also a direct read-out of the invariant. A list filtered in place would
    /// print "1 of 1".
    /// </summary>
    [Test]
    public async Task The_filter_caption_counts_visible_rows_against_every_row_the_watch_has()
    {
        SeededTab(out var tab);

        await Assert.That(tab.RowFilterSummary).IsEqualTo("");
        await Assert.That(tab.IsRowFiltering).IsFalse();

        tab.RowFilter = "api";
        await Assert.That(tab.IsRowFiltering).IsTrue();
        await Assert.That(tab.RowFilterSummary).IsEqualTo("1 of 3");

        tab.Apply(TestObjects.Added(TestObjects.Pod("shop", "worker-2")));
        await Assert.That(tab.RowFilterSummary).IsEqualTo("1 of 4");

        tab.RowFilter = "";
        await Assert.That(tab.RowFilterSummary).IsEqualTo("");
    }

    // ------------------------------------------------------ filter lifetime

    /// <summary>
    /// A name filter is a question about the list it was typed into: carrying "nginx"
    /// from Pods over to ConfigMaps lands on an empty list that looks like a broken
    /// watch. Driven through the real <c>SelectKindCommand</c> — with no client and no
    /// demo dataset the watch it restarts returns immediately, which is exactly the
    /// disconnected state, and the filter clearing is not conditional on any of that.
    /// </summary>
    [Test]
    public async Task Selecting_another_kind_clears_the_row_filter()
    {
        SeededTab(out var tab);
        tab.RowFilter = "api";

        var pods = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        var configMaps = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        var section = new SidebarSectionViewModel("Workloads");
        section.Kinds.Add(pods);
        section.Kinds.Add(configMaps);
        tab.SidebarSections.Add(section);

        tab.SelectKindCommand.Execute(pods);
        await Assert.That(tab.RowFilter).IsEqualTo("");

        tab.RowFilter = "nginx";
        tab.SelectKindCommand.Execute(configMaps);

        await Assert.That(tab.RowFilter).IsEqualTo("");
        await Assert.That(tab.IsRowFiltering).IsFalse();
        await Assert.That(tab.RowFilterSummary).IsEqualTo("");
    }
}
