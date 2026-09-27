using System.Text.Json.Nodes;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-33: workload detail's Refresh writes a fresh read into the very row the list holds,
/// in place — a second in-place update path beside the watch's Modified, and one that
/// reaches no <c>CollectionChanged</c>. The list has to be told, or a Refresh that heals
/// or breaks a workload leaves the unhealthy-only view wrong until the watch catches up.
/// </summary>
public class WorkloadRefreshVisibilityTests
{
    private static (ClusterTabViewModel Tab, WorkloadDetailTabViewModel Detail, ResourceRowViewModel Row) OpenDeployment()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" }));
        var row = tab.Rows.First(r => !r.IsUnhealthy);
        tab.SelectedRow = row;
        tab.OpenSelectedCommand.Execute(null);
        return (tab, (WorkloadDetailTabViewModel)tab.SelectedInspectorTab!, row);
    }

    /// <summary>The same Deployment with no ready replicas — "Unavailable", the error verdict.</summary>
    private static DynamicResource Broken(DynamicResource deployment)
    {
        var node = JsonNode.Parse(deployment.Raw.GetRawText())!;
        node["spec"]!["replicas"] = 3;
        node["status"] = new JsonObject { ["replicas"] = 3, ["readyReplicas"] = 0, ["availableReplicas"] = 0 };
        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return new DynamicResource(document.RootElement.Clone());
    }

    [Test]
    public async Task A_refresh_that_breaks_a_hidden_workload_shows_it_under_unhealthy_only()
    {
        var (tab, detail, row) = OpenDeployment();
        tab.IsUnhealthyOnly = true;
        await Assert.That(tab.VisibleRows.Contains(row)).IsFalse();

        detail.ApplyRefreshed(Broken(row.Resource));

        await Assert.That(row.IsUnhealthy).IsTrue();
        await Assert.That(tab.VisibleRows.Contains(row)).IsTrue();
    }

    [Test]
    public async Task A_refresh_that_heals_a_shown_workload_takes_it_out_of_unhealthy_only()
    {
        var (tab, detail, row) = OpenDeployment();
        var healthy = row.Resource;
        detail.ApplyRefreshed(Broken(healthy));
        tab.IsUnhealthyOnly = true;
        await Assert.That(tab.VisibleRows.Contains(row)).IsTrue();

        detail.ApplyRefreshed(healthy);

        await Assert.That(tab.VisibleRows.Contains(row)).IsFalse();
        await Assert.That(tab.Rows.Contains(row)).IsTrue();
    }

    /// <summary>
    /// A pane holding a row the list does not — the list moved on to another kind — must
    /// not put that row on screen: it is not one of the objects the list is showing.
    /// </summary>
    [Test]
    public async Task A_refresh_of_a_row_the_list_no_longer_holds_does_not_insert_it()
    {
        var (tab, detail, row) = OpenDeployment();
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.IsUnhealthyOnly = true;

        detail.ApplyRefreshed(Broken(row.Resource));

        await Assert.That(tab.VisibleRows.Contains(row)).IsFalse();
    }
}
