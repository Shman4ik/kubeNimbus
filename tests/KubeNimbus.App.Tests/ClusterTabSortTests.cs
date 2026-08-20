using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The sort is a <em>view</em> concern over <c>VisibleRows</c>, and UI rule 13's
/// invariant survives it: <c>Rows</c> stays the informer's own list, in the order the
/// watch delivered it.
///
/// <para>
/// The two ways of getting this wrong are the same two the row filter has, one step
/// further on. Sorting <c>Rows</c> instead would look right on screen and break the
/// informer underneath — its key map still resolves, but the arrival order is gone for
/// good, so clearing the sort could never come back to it. And sorting only on the
/// header click would leave a sorted list that silently stops being sorted: a watch is
/// a stream of Added and Modified events, and a list that appends new objects at the
/// bottom and leaves a changed one where it was is a list whose order means nothing a
/// few seconds after it was chosen. Both are asserted against below, and both were
/// written into the code and confirmed to turn this suite red before it was called
/// done.
/// </para>
///
/// <para>
/// These drive the real <see cref="ClusterTabViewModel.Apply"/> and the real
/// <c>ToggleSort</c>, the same entry points the watch pump and the grid's header click
/// use.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> because the stored layout is a real file behind a
/// process-global <c>WorkspaceStore.DirectoryOverride</c>: another test redirecting
/// that override mid-test would move the file out from under these assertions, which
/// is exactly what it did before this attribute was here.
/// </para>
/// </summary>
[NotInParallel]
public class ClusterTabSortTests
{
    private static ClusterTabViewModel SeededTab()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "web-1", restarts: 9)));
        tab.Apply(TestObjects.Added(TestObjects.Pod("shop", "api-7f9", restarts: 10)));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "cache-0", restarts: 2)));
        return tab;
    }

    // ------------------------------------------------------ the informer's own list

    /// <summary>
    /// The headline invariant. Sorting reorders what is rendered and leaves the watch's
    /// list exactly as it arrived — including through a sort, a reverse and a clear.
    /// </summary>
    [Test]
    public async Task Sorting_reorders_the_rendered_list_and_never_the_watch_list()
    {
        var tab = SeededTab();
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9, cache-0");

        tab.ToggleSort(ResourceColumn.Name);
        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, cache-0, web-1");
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9, cache-0");

        tab.ToggleSort(ResourceColumn.Name);
        await Assert.That(tab.SortDescending).IsTrue();
        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1, cache-0, api-7f9");
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9, cache-0");
    }

    /// <summary>
    /// The third click is the one worth having: it comes back to arrival order, which
    /// on a live list is information rather than the absence of a choice.
    /// </summary>
    [Test]
    public async Task A_third_click_returns_the_list_to_arrival_order()
    {
        var tab = SeededTab();

        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name);

        await Assert.That(tab.SortColumnId).IsNull();
        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1, api-7f9, cache-0");
    }

    /// <summary>A different column starts over at ascending rather than inheriting the
    /// previous column's direction.</summary>
    [Test]
    public async Task Clicking_a_different_column_starts_ascending()
    {
        var tab = SeededTab();

        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name);
        await Assert.That(tab.SortDescending).IsTrue();

        tab.ToggleSort(ResourceColumn.Namespace);
        await Assert.That(tab.SortColumnId).IsEqualTo(ResourceColumn.Namespace);
        await Assert.That(tab.SortDescending).IsFalse();
    }

    // -------------------------------------------------------------- live watch events

    /// <summary>
    /// A pod created while the list is sorted lands where the sort says, not at the
    /// bottom. This is the difference between a sorted list and a list that was sorted
    /// once.
    /// </summary>
    [Test]
    public async Task An_object_created_while_sorted_is_inserted_in_order()
    {
        var tab = SeededTab();
        tab.ToggleSort(ResourceColumn.Name);

        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "b-new")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, b-new, cache-0, web-1");

        // ...and the watch's own list still simply appended it.
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9, cache-0, b-new");
    }

    /// <summary>
    /// A Modified that changes the very value the list is ordered by moves the row —
    /// the CrashLoopBackOff case, on a list sorted by Status — without duplicating it
    /// and without replacing the row object (a new object means the informer lost it).
    /// </summary>
    [Test]
    public async Task A_modified_row_moves_to_where_the_sort_puts_it()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "a-pod")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "b-pod")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "c-pod")));

        tab.ToggleSort(ResourceColumn.Status);
        var before = tab.VisibleRows.Single(r => r.Name == "a-pod");

        tab.Apply(TestObjects.Modified(TestObjects.Pod("payments", "a-pod", phase: "Zombie")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("b-pod, c-pod, a-pod");
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(3);
        await Assert.That(tab.Rows.Count).IsEqualTo(3);
        await Assert.That(tab.VisibleRows.Single(r => r.Name == "a-pod")).IsSameReferenceAs(before);
    }

    /// <summary>A Modified that changes nothing the sort cares about moves nothing —
    /// a row jumping under the pointer on an unrelated status refresh is its own bug.</summary>
    [Test]
    public async Task A_modified_row_that_still_sorts_the_same_does_not_move()
    {
        var tab = SeededTab();
        tab.ToggleSort(ResourceColumn.Name);

        tab.Apply(TestObjects.Modified(TestObjects.Pod("shop", "api-7f9", restarts: 11)));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, cache-0, web-1");
    }

    [Test]
    public async Task A_deleted_row_leaves_both_lists()
    {
        var tab = SeededTab();
        tab.ToggleSort(ResourceColumn.Name);

        tab.Apply(TestObjects.Deleted(TestObjects.Pod("payments", "cache-0")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, web-1");
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9");
    }

    /// <summary>The sort and the search box compose: the filter decides which rows,
    /// the sort decides their order.</summary>
    [Test]
    public async Task The_sort_and_the_filter_compose()
    {
        var tab = SeededTab();
        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name); // descending

        tab.RowFilter = "payments";

        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1, cache-0");
        await Assert.That(tab.RowNames()).IsEqualTo("web-1, api-7f9, cache-0");
    }

    // ------------------------------------------------------- columns are not strings

    /// <summary>
    /// Restarts is a number. As text "10" sorts above "9", and the rendered cell is
    /// worse still — it carries "(43m ago)" with it.
    /// </summary>
    [Test]
    public async Task Restarts_sorts_as_a_number()
    {
        var tab = SeededTab();

        tab.ToggleSort(ResourceColumn.Restarts);

        await Assert.That(tab.VisibleNames()).IsEqualTo("cache-0, web-1, api-7f9");
    }

    /// <summary>
    /// Ascending Age means the smallest age — the newest object — first, which is the
    /// opposite direction to the instants behind it.
    /// </summary>
    [Test]
    public async Task Age_ascending_is_the_youngest_first()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "old", created: "2026-01-01T00:00:00Z")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "new", created: "2026-08-19T00:00:00Z")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "middle", created: "2026-05-01T00:00:00Z")));

        tab.ToggleSort(ResourceColumn.Age);

        await Assert.That(tab.VisibleNames()).IsEqualTo("new, middle, old");
    }

    /// <summary>
    /// A row with nothing in the sorted column sorts after the rows that have one, not
    /// as a zero or an empty string above them. A ConfigMap has no Ready column value;
    /// a pod that has not reported CPU is not a pod using none.
    /// </summary>
    [Test]
    public async Task A_row_with_no_value_sorts_after_the_rows_that_have_one()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "ready-pod")));
        tab.Apply(TestObjects.Added(TestObjects.ConfigMap("payments", "a-config")));

        tab.ToggleSort(ResourceColumn.Ready);

        await Assert.That(tab.VisibleNames()).IsEqualTo("ready-pod, a-config");
    }

    /// <summary>
    /// CPU is the latest metrics sample, not the rendered cell — "1200m" and "5m" sort
    /// the wrong way round as text, and "—" (not reported) is not a zero.
    /// </summary>
    [Test]
    public async Task Cpu_sorts_by_the_measured_value_and_puts_the_unmeasured_last()
    {
        var tab = SeededTab();
        tab.Rows.Single(r => r.Name == "web-1").ApplyUsage(1_200_000_000, 64 * 1024 * 1024);
        tab.Rows.Single(r => r.Name == "cache-0").ApplyUsage(5_000_000, 8 * 1024 * 1024);
        // api-7f9 reports nothing at all.

        tab.ToggleSort(ResourceColumn.Cpu);

        await Assert.That(tab.VisibleNames()).IsEqualTo("cache-0, web-1, api-7f9");
    }

    /// <summary>
    /// The metrics poll re-orders a usage-sorted list <b>in place</b>. What is being
    /// pinned is the absence of a Reset: a DataGrid answers one by dropping the scroll
    /// position and the selection, and a list that jumped to the top every fifteen
    /// seconds would be useless for the one job a CPU sort has.
    /// </summary>
    [Test]
    public async Task A_usage_re_sort_reorders_without_resetting_the_collection()
    {
        var tab = SeededTab();
        tab.Rows.Single(r => r.Name == "web-1").ApplyUsage(10_000_000, 1);
        tab.Rows.Single(r => r.Name == "cache-0").ApplyUsage(20_000_000, 2);
        tab.Rows.Single(r => r.Name == "api-7f9").ApplyUsage(30_000_000, 3);
        tab.ToggleSort(ResourceColumn.Cpu);
        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1, cache-0, api-7f9");

        var reset = false;
        tab.VisibleRows.CollectionChanged += (_, e) =>
            reset |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;

        // The next poll: the busiest pod goes quiet and the quietest becomes the busiest.
        tab.Rows.Single(r => r.Name == "api-7f9").ApplyUsage(1_000_000, 3);
        tab.ResortVisibleRows();

        await Assert.That(tab.VisibleNames()).IsEqualTo("api-7f9, web-1, cache-0");
        await Assert.That(reset).IsFalse();
    }

    // ----------------------------------------------------------------- per-kind memory

    /// <summary>
    /// The item's own acceptance criterion: the choice survives switching kinds within a
    /// tab. Each kind remembers its own — a sort chosen for Pods says nothing about
    /// ConfigMaps, which have different columns and answer a different question.
    /// </summary>
    [Test]
    public async Task The_sort_is_remembered_per_kind()
    {
        var tab = TestObjects.Tab();
        var pods = new SidebarKindViewModel(TestObjects.PodDescriptor, "cube");
        var configMaps = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "cog");

        tab.SelectedKind = pods;
        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name); // descending

        tab.SelectedKind = configMaps;
        await Assert.That(tab.SortColumnId).IsNull();

        tab.SelectedKind = pods;
        await Assert.That(tab.SortColumnId).IsEqualTo(ResourceColumn.Name);
        await Assert.That(tab.SortDescending).IsTrue();
    }

    /// <summary>
    /// Restoring a remembered sort must not re-record it: reading a choice back is not
    /// making one, and a kind whose sort has been cleared has to stay cleared.
    /// </summary>
    [Test]
    public async Task Clearing_the_sort_forgets_it()
    {
        var tab = TestObjects.Tab();
        var pods = new SidebarKindViewModel(TestObjects.PodDescriptor, "cube");

        tab.SelectedKind = pods;
        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name);
        tab.ToggleSort(ResourceColumn.Name); // off again

        await Assert.That(GridLayoutStore.Load(GridLayoutStore.KeyFor(TestObjects.PodDescriptor)).SortColumn).IsNull();
    }

    /// <summary>
    /// A CRD column that the kind in front of you no longer declares cannot order the
    /// list — the cells behind it are gone, so a list claiming to be sorted by it would
    /// just be shuffled. It falls back to arrival order rather than to nonsense.
    /// </summary>
    [Test]
    public async Task A_sort_by_a_printer_column_the_kind_does_not_declare_falls_back_to_arrival_order()
    {
        var tab = SeededTab();

        tab.SetSort(ResourceColumn.Printer("Issuer"), descending: false, persist: false);

        await Assert.That(tab.VisibleNames()).IsEqualTo("web-1, api-7f9, cache-0");
    }

    /// <summary>...and it can, once the kind declares it.</summary>
    [Test]
    public async Task A_printer_column_orders_the_list_by_its_own_cells()
    {
        var tab = SeededTab();
        tab.PrinterColumns = [new PrinterColumn("Owner", "string", ".metadata.namespace")];

        tab.SetSort(ResourceColumn.Printer("Owner"), descending: false, persist: false);

        // payments/payments/shop — the two payments rows keep their arrival order
        // relative to each other, since the tie-break is the row key and not the sort.
        await Assert.That(tab.VisibleNames()).IsEqualTo("cache-0, web-1, api-7f9");
    }
}
