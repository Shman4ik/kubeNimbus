using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;

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

    /// <summary>
    /// #305, the owner's decision of 2026-10-12: a bulk delete of more than one row always
    /// asks, even with "Confirm before deleting" off on a non-production cluster — the
    /// preference was set for one object, not for N. Mutation-checked by letting the bulk
    /// action follow the preference again: the run then starts on the press.
    /// </summary>
    [Test]
    public async Task Off_production_with_the_preference_off_a_bulk_delete_still_asks()
    {
        App.Update(s => s with { ConfirmDeletes = false });
        using var server = new StubApiServer();
        foreach (var name in Names)
        {
            server.Respond("DELETE", PodPath(name), HttpStatusCode.OK, StubApiServer.Deleted);
        }

        var tab = PodTab(server.Connect());
        tab.DeleteSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.IsBulk).IsTrue();
        await Assert.That(action.IsPromptVisible).IsTrue();
        await Task.Delay(200);
        await Assert.That(server.Requests.Count).IsEqualTo(0);

        await action.ConfirmCommand.ExecuteAsync(null);
        await DoneAsync(action);
        await Assert.That(action.Message).IsEqualTo("Deleted 3 Pods.");
    }

    /// <summary>#305: one row keeps following the preference — off, the delete runs on the press.</summary>
    [Test]
    public async Task Off_production_with_the_preference_off_one_row_is_deleted_on_the_press()
    {
        App.Update(s => s with { ConfirmDeletes = false });
        using var server = new StubApiServer();
        server.Respond("DELETE", PodPath("checkout-b"), HttpStatusCode.OK, StubApiServer.Deleted);
        var tab = PodTab(server.Connect());
        tab.SetSelectedRows([tab.Rows[1]]);
        tab.SelectedRow = tab.Rows[1];

        tab.DeleteSelectedCommand.Execute(null);
        await DoneAsync(tab.PendingRowAction!);

        await Assert.That(server.Requests.Single(r => r.Method == "DELETE").Path).IsEqualTo(PodPath("checkout-b"));
    }

    /// <summary>
    /// #305: with several rows selected, every action that acts on one object is disabled
    /// and says why; Delete is the one that acts on all of them. Back to one row, they come
    /// back. Mutation-checked by reading <c>SelectedRow</c> instead of <c>ActionRow</c> in
    /// <c>HasSelectedRow</c>: Edit YAML is then enabled under the selection.
    /// </summary>
    [Test]
    public async Task With_several_rows_selected_single_object_actions_are_disabled_and_say_why()
    {
        using var server = new StubApiServer();
        var tab = PodTab(server.Connect());

        await Assert.That(tab.HasMultipleSelection).IsTrue();
        await Assert.That(tab.EditSelectedYamlCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.OpenLogsCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.OpenPreviousLogsCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.ExecIntoSelectedCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.PortForwardSelectedCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.DeleteSelectedCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.SingleRowActionTip)
            .IsEqualTo("Acts on one object, and 3 rows are selected. Select one row to use it; Delete acts on all of them.");

        tab.SetSelectedRows([tab.Rows[0]]);

        await Assert.That(tab.EditSelectedYamlCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.OpenLogsCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.ExecIntoSelectedCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.SingleRowActionTip).IsNull();
    }

    /// <summary>
    /// #305: the palette runs a command's Execute, which does not consult CanExecute, so it
    /// must not list a single-object entry under a multi-row selection; Delete for all of them
    /// is the one row entry. Mutation-checked by removing the palette's multi-selection branch:
    /// Edit YAML for the current row is listed again, and Delete names that one row.
    /// </summary>
    [Test]
    public async Task The_palette_offers_only_delete_for_several_selected_rows()
    {
        using var server = new StubApiServer();
        var tab = PodTab(server.Connect());
        var shell = new MainWindowViewModel { Mode = ShellMode.Resources };
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;

        shell.Palette.Open();
        var rowEntries = shell.Palette.FilteredItems
            .Where(i => i.Subtitle.StartsWith("payments/", StringComparison.Ordinal) || i.Subtitle.Contains("selected", StringComparison.Ordinal))
            .Select(i => $"{i.Title} — {i.Subtitle}")
            .ToList();
        shell.Palette.Close();

        await Assert.That(string.Join(" | ", rowEntries)).IsEqualTo($"Delete resource — 3 selected · asks to confirm · {CommandBindings.ShortcutLabel(CommandId.DeleteResource)}");

        tab.SetSelectedRows([tab.Rows[0]]);
        shell.Palette.Open();
        var single = shell.Palette.FilteredItems.Where(i => i.Subtitle.StartsWith("payments/checkout-a", StringComparison.Ordinal)).ToList();
        shell.Palette.Close();
        await Assert.That(single.Any(i => i.Title == "Edit YAML")).IsTrue();
    }

    /// <summary>
    /// The issue's own example: three nodes selected, Cordon stays in the menu (its slot does
    /// not move) but is disabled, so it can no longer cordon the current row alone.
    /// </summary>
    [Test]
    public async Task Three_selected_nodes_keep_cordon_in_the_menu_disabled()
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.NodeDescriptor, "cluster"),
        };
        foreach (var name in (string[])["node-a", "node-b", "node-c"])
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                """{"apiVersion":"v1","kind":"Node","metadata":{"name":"NAME","uid":"NAME"},"spec":{}}""".Replace("NAME", name, StringComparison.Ordinal));
            tab.Apply(TestObjects.Added(new DynamicResource(document.RootElement.Clone())));
        }

        tab.Client = TestObjects.OfflineClient();
        tab.SelectedRow = tab.Rows[0];
        await Assert.That(tab.CordonSelectedCommand.CanExecute(null)).IsTrue();

        tab.SetSelectedRows([.. tab.Rows]);

        await Assert.That(tab.OffersCordon).IsTrue();
        await Assert.That(tab.CordonSelectedCommand.CanExecute(null)).IsFalse();
        tab.CordonSelectedCommand.Execute(null);
        await Assert.That(tab.PendingRowAction).IsNull();
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
