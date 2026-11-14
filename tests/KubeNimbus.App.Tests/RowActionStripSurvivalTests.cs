using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// A running drain or bulk loop lives in the row-action strip. A Refresh or a kind switch
/// rebuilds the list and used to drop the strip unconditionally, which left the loop running
/// with no Stop on screen and nothing for the tab's close to cancel. These pin that a running
/// strip survives the rebuild, an idle one is still cleared, and closing the tab stops what
/// is still running.
///
/// <para>
/// The rebuild tests drive the demo tab, which has no client, so no watch and no dispatcher
/// post ever start; the running state is set on the strip the way <c>NodeActionTests</c> sets
/// <c>IsBusy</c>. The close test runs a real bulk delete against <see cref="StubApiServer"/>
/// with the first DELETE held, so the cancel it checks is the loop's own.
/// </para>
/// </summary>
[NotInParallel]
public class RowActionStripSurvivalTests
{
    private static ClusterTabViewModel DemoTab(string kind)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        SelectKind(tab, kind);
        return tab;
    }

    private static void SelectKind(ClusterTabViewModel tab, string kind) =>
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "" } d && d.Kind == kind));

    private static async Task<RowActionViewModel> DrainingAsync(ClusterTabViewModel tab)
    {
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        await tab.DrainSelectedCommand.ExecuteAsync(null);
        var action = tab.PendingRowAction!;
        action.IsDraining = true;
        return action;
    }

    private static RowActionViewModel DeletingMany(ClusterTabViewModel tab)
    {
        tab.SetSelectedRows([.. tab.Rows.Take(2)]);
        tab.DeleteSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;
        action.IsDeletingMany = true;
        return action;
    }

    [Test]
    public async Task A_running_drain_keeps_its_strip_through_a_refresh_and_a_kind_switch()
    {
        var tab = DemoTab("Node");
        var action = await DrainingAsync(tab);

        tab.RefreshCommand.Execute(null);
        await Assert.That(tab.PendingRowAction).IsSameReferenceAs(action);

        SelectKind(tab, "Pod");
        await Assert.That(tab.PendingRowAction).IsSameReferenceAs(action);
    }

    [Test]
    public async Task A_running_bulk_delete_keeps_its_strip_through_a_refresh_and_a_kind_switch()
    {
        var tab = DemoTab("Pod");
        var action = DeletingMany(tab);
        await Assert.That(action.IsBulk).IsTrue();

        tab.RefreshCommand.Execute(null);
        await Assert.That(tab.PendingRowAction).IsSameReferenceAs(action);

        SelectKind(tab, "Service");
        await Assert.That(tab.PendingRowAction).IsSameReferenceAs(action);
    }

    [Test]
    public async Task An_idle_strip_is_cleared_by_a_refresh_and_by_a_kind_switch()
    {
        var tab = DemoTab("Node");
        var drain = await DrainingAsync(tab);
        drain.IsDraining = false;
        tab.RefreshCommand.Execute(null);
        await Assert.That(tab.PendingRowAction).IsNull();

        var after = await DrainingAsync(tab);
        after.IsDraining = false;
        SelectKind(tab, "Pod");
        await Assert.That(tab.PendingRowAction).IsNull();
    }

    [Test]
    public async Task Closing_the_tab_stops_a_running_bulk_delete()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer { Gate = new TaskCompletionSource<bool>() };
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
        };
        foreach (var name in (string[])["checkout-a", "checkout-b", "checkout-c"])
        {
            tab.Apply(TestObjects.Added(TestObjects.Pod("payments", name)));
        }

        tab.Client = server.Connect();
        tab.SelectedRow = tab.Rows[0];
        tab.SetSelectedRows([.. tab.Rows]);
        tab.DeleteSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;

        var run = action.ConfirmCommand.ExecuteAsync(null);
        await server.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(action.IsDeletingMany).IsTrue();

        await tab.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(action.IsDeletingMany).IsFalse();
        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(action.Message).StartsWith("Stopped.");
    }

    /// <summary>
    /// A batch is posted to the UI thread and can run after <c>StopWatch</c> cleared the list
    /// for another kind: the batch of a cancelled watch is dropped, and so is its "connection
    /// lost" line. The guard is tested at the entry point the watch's post calls, since the
    /// tests run no dispatcher.
    /// </summary>
    [Test]
    public async Task A_batch_from_a_cancelled_watch_is_not_applied()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
        };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        tab.ApplyBatch([TestObjects.Added(TestObjects.Pod("payments", "stale"))], cancelled.Token);
        tab.OnListConnectionLost(new InvalidOperationException("old watch lost"), cancelled.Token);
        await Assert.That(tab.Rows.Count).IsEqualTo(0);
        await Assert.That(tab.ConnectionWarning).IsNull();

        tab.ApplyBatch([TestObjects.Added(TestObjects.Pod("payments", "live"))], CancellationToken.None);
        await Assert.That(tab.RowNames()).IsEqualTo("live");
    }
}
