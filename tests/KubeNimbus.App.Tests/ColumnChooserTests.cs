using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-13: columns hidden per kind through the column chooser, remembered in
/// <c>workspace.json</c> beside the widths and the sort (session state, not a preference),
/// the identity column never hidden, and a reset back to what the kind ships with.
/// <c>[NotInParallel]</c> because the layout is a real file behind a process-wide override.
/// </summary>
[NotInParallel]
public class ColumnChooserTests
{
    private static string PodKey => GridLayoutStore.KeyFor(TestObjects.PodDescriptor);

    private static ClusterTabViewModel PodTab()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        return tab;
    }

    [Test]
    public async Task A_hidden_column_is_remembered_for_its_kind_and_only_its_kind()
    {
        var tab = PodTab();
        var pods = tab.SelectedKind;

        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: true);
        await Assert.That(tab.HiddenColumns).Contains(ResourceColumn.Restarts);
        await Assert.That(GridLayoutStore.Load(PodKey).Hidden).IsEquivalentTo([ResourceColumn.Restarts]);

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        await Assert.That(tab.HiddenColumns.Count).IsEqualTo(0);

        tab.SelectedKind = pods;
        await Assert.That(tab.HiddenColumns).Contains(ResourceColumn.Restarts);

        // A new tab on the same kind (a restart, another cluster) reads the same choice.
        await Assert.That(PodTabWithoutRedirect().HiddenColumns).Contains(ResourceColumn.Restarts);
    }

    private static ClusterTabViewModel PodTabWithoutRedirect() =>
        new(TestObjects.Context) { SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload") };

    /// <summary>
    /// A kind whose only choice is a hidden column is still a choice, and must stay in the
    /// file — the store drops a layout it thinks is default. Mutation-checked by putting the
    /// old "no sort and no widths" test back in <c>GridLayoutStore.Update</c>.
    /// </summary>
    [Test]
    public async Task A_layout_whose_only_choice_is_a_hidden_column_is_kept()
    {
        var tab = PodTab();

        tab.SetColumnHidden(ResourceColumn.Age, hidden: true);

        var stored = WorkspaceStore.Load().GridLayouts!;
        await Assert.That(stored.ContainsKey(PodKey)).IsTrue();
        await Assert.That(stored[PodKey].Hidden).IsEquivalentTo([ResourceColumn.Age]);
    }

    [Test]
    public async Task The_identity_columns_cannot_be_hidden()
    {
        var tab = PodTab();

        tab.SetColumnHidden(ResourceColumn.Name, hidden: true);
        tab.SetColumnHidden(ResourceColumn.EventObject, hidden: true);

        await Assert.That(tab.HiddenColumns.Count).IsEqualTo(0);
        await Assert.That(WorkspaceStore.Load().GridLayouts!.ContainsKey(PodKey)).IsFalse();
    }

    [Test]
    public async Task Showing_a_column_again_removes_it_and_an_empty_choice_leaves_the_file()
    {
        var tab = PodTab();
        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: true);

        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: false);

        await Assert.That(tab.HiddenColumns.Count).IsEqualTo(0);
        await Assert.That(WorkspaceStore.Load().GridLayouts!.ContainsKey(PodKey)).IsFalse();
    }

    [Test]
    public async Task Reset_brings_every_column_back_at_its_starting_width_and_keeps_the_sort()
    {
        var tab = PodTab();
        tab.SetSort(ResourceColumn.Age, descending: true);
        GridLayoutStore.Update(PodKey, layout => layout with
        {
            ColumnWidths = new Dictionary<string, GridColumnWidth> { [ResourceColumn.Name] = new(GridColumnWidth.Star, 3) },
        });
        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: true);
        await Assert.That(tab.HasCustomColumns).IsTrue();

        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tab.ResetColumns();

        var layout = GridLayoutStore.Load(PodKey);
        await Assert.That(tab.HiddenColumns.Count).IsEqualTo(0);
        await Assert.That(layout.Hidden.Count).IsEqualTo(0);
        await Assert.That(layout.Widths.Count).IsEqualTo(0);
        await Assert.That(layout.SortColumn).IsEqualTo(ResourceColumn.Age);
        await Assert.That(tab.HasCustomColumns).IsFalse();
        await Assert.That(raised).Contains(ClusterTabViewModel.ColumnLayoutChangedProperty);
    }

    /// <summary>
    /// #301: the sort arrow lives in its column's header, so hiding the column the list is
    /// sorted by clears the sort (back to arrival order, remembered), and a stored sort by a
    /// hidden column is not applied when the kind opens. Hiding another column keeps it.
    /// Mutation-checked by removing the clear from <c>SetColumnHidden</c>: the sort then
    /// stays on Age with Age hidden.
    /// </summary>
    [Test]
    public async Task Hiding_the_sorted_column_clears_the_sort()
    {
        var tab = PodTab();
        var pods = tab.SelectedKind;
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "b")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "a")));
        tab.SetSort(ResourceColumn.Name, descending: false);
        await Assert.That(tab.VisibleNames()).IsEqualTo("a, b");

        tab.SetSort(ResourceColumn.Age, descending: true);
        tab.SetColumnHidden(ResourceColumn.Restarts, hidden: true);
        await Assert.That(tab.SortColumnId).IsEqualTo(ResourceColumn.Age);

        tab.SetColumnHidden(ResourceColumn.Age, hidden: true);
        await Assert.That(tab.SortColumnId).IsNull();
        await Assert.That(tab.VisibleNames()).IsEqualTo("b, a");
        await Assert.That(GridLayoutStore.Load(PodKey).SortColumn).IsNull();

        // A sort stored by a column that is hidden (an older file, a hand edit) is not applied.
        GridLayoutStore.Update(PodKey, layout => layout with { SortColumn = ResourceColumn.Age, SortDescending = true });
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        tab.SelectedKind = pods;
        await Assert.That(tab.SortColumnId).IsNull();
    }

    [Test]
    public async Task Rows_stay_the_watchs_list_when_a_column_is_hidden()
    {
        var tab = PodTab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "b")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "a")));

        tab.SetColumnHidden(ResourceColumn.Status, hidden: true);

        await Assert.That(tab.RowNames()).IsEqualTo("b, a");
        await Assert.That(tab.VisibleNames()).IsEqualTo("b, a");
    }
}
