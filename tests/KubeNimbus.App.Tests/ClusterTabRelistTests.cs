using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// What survives the list starting over (a Refresh, or a Reset from the watch): the selected row,
/// found again by key since every row is a new object, and the pod and node panes that read a
/// row of the list (<see cref="ILiveRowTab"/>), which are handed the new row and told when their
/// object is gone. Before, both were held by object: F5 dropped the selection, and an open pod
/// pane went on reading a row nothing updated, with no sign that the pod had been deleted.
/// </summary>
public class ClusterTabRelistTests
{
    private static ClusterTabViewModel PodsTab(params string[] names)
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        List(tab, names);
        return tab;
    }

    private static void List(ClusterTabViewModel tab, params string[] names)
    {
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        foreach (var name in names)
        {
            tab.Apply(TestObjects.Added(TestObjects.Pod("team", name)));
        }

        tab.Apply(ResourceEvent<DynamicResource>.Synced);
    }

    private static ResourceRowViewModel Row(ClusterTabViewModel tab, string name) => tab.Rows.Single(r => r.Name == name);

    private static PodDetailTabViewModel OpenPod(ClusterTabViewModel tab, string name)
    {
        var detail = new PodDetailTabViewModel(null, Row(tab, name), _ => { }, (_, _) => Task.CompletedTask);
        tab.InspectorTabs.Add(detail);
        return detail;
    }

    [Test]
    public async Task Refresh_selects_the_same_object_again_once_it_is_listed()
    {
        var tab = PodsTab("web-1", "web-2");
        tab.SelectedRow = Row(tab, "web-2");
        var before = tab.SelectedRow;

        tab.RefreshCommand.Execute(null);
        List(tab, "web-1", "web-2");

        await Assert.That(tab.SelectedRow).IsNotNull();
        await Assert.That(tab.SelectedRow!.Name).IsEqualTo("web-2");
        await Assert.That(tab.SelectedRow).IsSameReferenceAs(Row(tab, "web-2"));
        await Assert.That(tab.SelectedRow).IsNotSameReferenceAs(before);
    }

    [Test]
    public async Task A_Reset_from_the_watch_keeps_the_selection_by_key()
    {
        var tab = PodsTab("web-1", "web-2");
        tab.SelectedRow = Row(tab, "web-1");

        List(tab, "web-2", "web-1");

        await Assert.That(tab.SelectedRow?.Name).IsEqualTo("web-1");
        await Assert.That(tab.SelectedRow).IsSameReferenceAs(Row(tab, "web-1"));
    }

    [Test]
    public async Task A_selection_whose_object_did_not_come_back_is_dropped_and_not_revived_later()
    {
        var tab = PodsTab("web-1", "web-2");
        tab.SelectedRow = Row(tab, "web-2");

        List(tab, "web-1");
        await Assert.That(tab.SelectedRow).IsNull();

        // Created again under the same name after the list synced: a new object nobody chose.
        tab.Apply(TestObjects.Added(TestObjects.Pod("team", "web-2")));
        await Assert.That(tab.SelectedRow).IsNull();
    }

    [Test]
    public async Task A_kind_change_does_not_carry_the_selection_to_an_object_of_the_same_name()
    {
        var tab = PodsTab("web-1");
        tab.SelectedRow = Row(tab, "web-1");

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.Apply(TestObjects.Added(TestObjects.ConfigMap("team", "web-1")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.SelectedRow).IsNull();
    }

    [Test]
    public async Task An_open_pod_pane_reads_the_new_row_after_a_Refresh()
    {
        var tab = PodsTab("web-1", "web-2");
        var detail = OpenPod(tab, "web-2");

        tab.RefreshCommand.Execute(null);
        List(tab, "web-1", "web-2");

        ILiveRowTab live = detail;
        await Assert.That(live.Row).IsSameReferenceAs(Row(tab, "web-2"));
        await Assert.That(detail.IsDeleted).IsFalse();

        // And it follows that row from here on: a change the watch delivers reaches the row it reads.
        tab.Apply(TestObjects.Modified(TestObjects.Pod("team", "web-2", phase: "Failed")));
        await Assert.That(live.Row.Resource.Raw.GetProperty("status").GetProperty("phase").GetString()).IsEqualTo("Failed");
    }

    [Test]
    public async Task A_pod_deleted_while_its_pane_is_open_says_so_and_a_new_one_of_the_same_name_clears_it()
    {
        var tab = PodsTab("web-1", "web-2");
        var detail = OpenPod(tab, "web-2");

        tab.Apply(TestObjects.Deleted(TestObjects.Pod("team", "web-2")));
        await Assert.That(detail.IsDeleted).IsTrue();

        tab.Apply(TestObjects.Added(TestObjects.Pod("team", "web-2")));
        await Assert.That(detail.IsDeleted).IsFalse();
        await Assert.That(((ILiveRowTab)detail).Row).IsSameReferenceAs(Row(tab, "web-2"));
    }

    [Test]
    public async Task A_pod_that_a_Refresh_no_longer_lists_is_marked_deleted()
    {
        var tab = PodsTab("web-1", "web-2");
        var detail = OpenPod(tab, "web-2");

        tab.RefreshCommand.Execute(null);
        List(tab, "web-1");

        await Assert.That(detail.IsDeleted).IsTrue();
    }

    [Test]
    public async Task A_list_of_another_kind_says_nothing_about_an_open_pod_and_coming_back_rebinds_it()
    {
        var tab = PodsTab("web-1");
        var detail = OpenPod(tab, "web-1");

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.Apply(TestObjects.Added(TestObjects.ConfigMap("team", "settings")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(detail.IsDeleted).IsFalse();

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        List(tab, "web-1");

        await Assert.That(detail.IsDeleted).IsFalse();
        await Assert.That(((ILiveRowTab)detail).Row).IsSameReferenceAs(Row(tab, "web-1"));
    }

    [Test]
    public async Task A_node_removed_while_its_pane_is_open_says_so()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.NodeDescriptor, "cluster");
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.Apply(TestObjects.Added(Node("worker-1")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        var detail = new NodeDetailTabViewModel(null, tab.Rows.Single());
        tab.InspectorTabs.Add(detail);

        tab.Apply(TestObjects.Deleted(Node("worker-1")));

        await Assert.That(detail.IsDeleted).IsTrue();
    }

    private static DynamicResource Node(string name)
    {
        using var document = JsonDocument.Parse($$$"""
            {"apiVersion":"v1","kind":"Node","metadata":{"name":"{{{name}}}","uid":"{{{name}}}"},"spec":{},
             "status":{"conditions":[{"type":"Ready","status":"True"}]}}
            """);
        return new DynamicResource(document.RootElement.Clone());
    }
}
