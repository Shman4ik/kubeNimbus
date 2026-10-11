using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-6: extended selection in the resource list and one Delete for all of it. One strip,
/// one sentence naming the count and the cluster, the same confirm rule as a single delete
/// (production always asks), and one request per object, so a narrow-RBAC reader's run that
/// is refused for some objects reports every outcome rather than a silent partial.
///
/// <para>
/// The server is <see cref="StubApiServer"/>, a real HTTP listener behind a real
/// <see cref="ClusterClient"/>, so a 403 here is the API server's own Status body travelling
/// the same path a real refusal does. <c>[NotInParallel]</c> because "Confirm before deleting"
/// is written to <c>settings.json</c> and read back at the press.
/// </para>
/// </summary>
[NotInParallel]
public class BulkDeleteTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static readonly string[] Names = ["checkout-a", "checkout-b", "checkout-c"];

    private static string PodPath(string name) => $"/api/v1/namespaces/payments/pods/{name}";

    private static ClusterTabViewModel PodTab(ClusterClient client, ClusterEnvironment environment = ClusterEnvironment.Development)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
        };
        foreach (var name in Names)
        {
            tab.Apply(TestObjects.Added(TestObjects.Pod("payments", name)));
        }

        tab.Client = client;
        tab.Environment = environment;
        tab.SelectedRow = tab.Rows[0];
        tab.SetSelectedRows([.. tab.Rows]);
        return tab;
    }

    private static async Task<RowActionViewModel> DoneAsync(RowActionViewModel action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!action.IsDone && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return action;
    }

    [Test]
    public async Task The_selection_is_counted_and_one_strip_names_the_count_and_the_cluster()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer();
        var tab = PodTab(server.Connect());

        await Assert.That(tab.SelectionSummary).IsEqualTo("3 selected");
        await Assert.That(tab.CanDeleteSelectedRow).IsTrue();

        tab.DeleteSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.IsBulk).IsTrue();
        await Assert.That(action.TargetCount).IsEqualTo(3);
        await Assert.That(action.Headline).IsEqualTo("Delete 3 Pods in payments on test-cluster");
        await Assert.That(action.BulkItems.Select(i => i.Key)).IsEquivalentTo(Names.Select(n => $"payments/{n}"));

        // Armed, not sent: nothing reached the server before the confirm.
        await Assert.That(server.Requests.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The narrow-RBAC case the item is about: some deletes allowed, one refused. Every
    /// allowed one goes through, the refused one carries the server's own sentence, and the
    /// result line counts both — mutation-checked by making the loop stop at the first refusal.
    /// </summary>
    [Test]
    public async Task A_refusal_among_successes_is_reported_per_object_and_does_not_stop_the_rest()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer();
        server.Respond("DELETE", PodPath("checkout-a"), HttpStatusCode.OK, StubApiServer.Deleted);
        server.Respond("DELETE", PodPath("checkout-b"), HttpStatusCode.Forbidden, StubApiServer.Forbidden("checkout-b"));
        server.Respond("DELETE", PodPath("checkout-c"), HttpStatusCode.OK, StubApiServer.Deleted);
        var tab = PodTab(server.Connect());

        tab.DeleteSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;
        await action.ConfirmCommand.ExecuteAsync(null);
        await DoneAsync(action);

        await Assert.That(server.Requests.Count(r => r.Method == "DELETE")).IsEqualTo(3);
        await Assert.That(action.IsError).IsTrue();
        await Assert.That(action.IsSuccess).IsFalse();
        await Assert.That(action.Message).IsEqualTo("Deleted 2 of 3 Pods. 1 refused — each one's reason is in the list below.");
        var refused = action.BulkItems.Single(i => i.IsFailed);
        await Assert.That(refused.Key).IsEqualTo("payments/checkout-b");
        await Assert.That(refused.Detail).Contains("cannot delete resource \"pods\"");
        await Assert.That(action.BulkItems.Count(i => i.IsDone)).IsEqualTo(2);
    }

    [Test]
    public async Task Every_delete_allowed_reads_as_a_plain_success()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer();
        foreach (var name in Names)
        {
            server.Respond("DELETE", PodPath(name), HttpStatusCode.OK, StubApiServer.Deleted);
        }

        var tab = PodTab(server.Connect());
        tab.DeleteSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;
        await action.ConfirmCommand.ExecuteAsync(null);
        await DoneAsync(action);

        await Assert.That(action.IsSuccess).IsTrue();
        await Assert.That(action.Message).IsEqualTo("Deleted 3 Pods.");
    }

    /// <summary>
    /// Stop while a request is in flight: nothing after it is sent, the abandoned one says the
    /// server may or may not have acted, and the result line counts what is left.
    /// </summary>
    [Test]
    public async Task Stop_ends_the_run_after_the_request_in_flight()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer { Gate = new TaskCompletionSource<bool>() };
        var tab = PodTab(server.Connect());
        tab.DeleteSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;

        var run = action.ConfirmCommand.ExecuteAsync(null);
        await server.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(action.IsDeletingMany).IsTrue();
        await Assert.That(action.CanDismiss).IsFalse();
        action.StopBulkCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(action.IsDone).IsTrue();
        await Assert.That(action.IsError).IsTrue();
        await Assert.That(action.Message).IsEqualTo("Stopped. 0 of 3 Pods deleted, 3 left. Select what is still listed and delete again to finish.");
        await Assert.That(action.BulkItems[0].IsUncertain).IsTrue();
        await Assert.That(action.BulkItems[1].Detail).IsEqualTo("");
    }

    [Test]
    public async Task Production_always_asks_even_with_the_preference_off()
    {
        App.Update(s => s with { ConfirmDeletes = false });
        using var server = new StubApiServer();
        var tab = PodTab(server.Connect(), ClusterEnvironment.Production);

        tab.DeleteSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.IsBulk).IsTrue();
        await Assert.That(action.Headline).IsEqualTo("Delete 3 Pods in payments on test-cluster (production)");
        await Assert.That(action.IsPromptVisible).IsTrue();
        await Task.Delay(200);
        await Assert.That(server.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Off_production_with_the_preference_off_the_run_starts_on_the_press()
    {
        App.Update(s => s with { ConfirmDeletes = false });
        using var server = new StubApiServer();
        foreach (var name in Names)
        {
            server.Respond("DELETE", PodPath(name), HttpStatusCode.OK, StubApiServer.Deleted);
        }

        var tab = PodTab(server.Connect());
        tab.DeleteSelectedCommand.Execute(null);
        var action = await DoneAsync(tab.PendingRowAction!);

        await Assert.That(action.Message).IsEqualTo("Deleted 3 Pods.");
    }

    [Test]
    public async Task One_selected_row_is_an_ordinary_delete_and_a_kind_change_clears_the_selection()
    {
        App.Update(s => s with { ConfirmDeletes = true });
        using var server = new StubApiServer();
        var tab = PodTab(server.Connect());
        tab.SetSelectedRows([tab.Rows[1]]);
        tab.SelectedRow = tab.Rows[1];

        await Assert.That(tab.SelectionSummary).IsEqualTo("");
        tab.DeleteSelectedCommand.Execute(null);
        await Assert.That(tab.PendingRowAction!.IsBulk).IsFalse();
        await Assert.That(tab.PendingRowAction.Headline).IsEqualTo("Delete Pod checkout-b in payments on test-cluster");

        tab.SetSelectedRows([.. tab.Rows]);
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config");
        await Assert.That(tab.SelectedRows.Count).IsEqualTo(0);
        await Assert.That(tab.HasMultipleSelection).IsFalse();
    }

    [Test]
    public async Task The_summary_counts_every_outcome()
    {
        await Assert.That(RowActionViewModel.BulkSummary(0, 4, 4, "Pods", stopped: false))
            .IsEqualTo("Nothing was deleted: the server refused all 4. Each one's reason is in the list below.");
        await Assert.That(RowActionViewModel.BulkSummary(2, 1, 5, "Pods", stopped: true))
            .IsEqualTo("Stopped. 2 of 5 Pods deleted, 1 refused, 2 left. Select what is still listed and delete again to finish.");
    }
}
