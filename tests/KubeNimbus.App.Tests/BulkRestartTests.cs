using System.Net;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #301, FEAT-6's next slice: a rollout restart of several selected workloads, on the bulk
/// delete's strip. It is disruptive (every selected workload's pods roll at once), so it arms
/// the strip and asks, naming the count and the cluster (UI rule 17); then it is one request
/// per object with each object's outcome, like the bulk delete. And a multi-row selection
/// survives a re-sort that replaces the list. <c>[NotInParallel]</c>: settings are redirected.
/// </summary>
[NotInParallel]
public class BulkRestartTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static readonly string[] Names = ["api", "web", "worker"];

    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: ["all"]);

    private static string DeploymentPath(string name) => $"/apis/apps/v1/namespaces/payments/deployments/{name}";

    private static DynamicResource Deployment(string name)
    {
        var json = $$"""
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "{{name}}", "namespace": "payments", "uid": "payments-{{name}}", "creationTimestamp": "2026-08-01T10:00:00Z" },
          "spec": { "replicas": 2, "template": { "spec": { "containers": [ { "name": "app" } ] } } },
          "status": { "replicas": 2, "readyReplicas": 2 }
        }
        """;
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static string Patched(string name) =>
        $$$"""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"{{{name}}}","namespace":"payments"}}""";

    private static ClusterTabViewModel DeploymentTab(ClusterClient client)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(DeploymentDescriptor, "workload"),
        };
        foreach (var name in Names)
        {
            tab.Apply(TestObjects.Added(Deployment(name)));
        }

        tab.Client = client;
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

    /// <summary>
    /// The central check, mutation-checked: with <c>RestartSelected</c> arming the single-row
    /// strip again, the command was disabled under the selection (ActionRow is null) and no
    /// strip was armed.
    /// </summary>
    [Test]
    public async Task Several_workloads_arm_one_strip_that_asks_and_names_the_count_and_the_cluster()
    {
        using var server = new StubApiServer();
        var tab = DeploymentTab(server.Connect());

        await Assert.That(tab.CanRestartSelectedRow).IsTrue();
        tab.RestartSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.Kind).IsEqualTo(RowActionKind.Restart);
        await Assert.That(action.IsBulk).IsTrue();
        await Assert.That(action.Headline).IsEqualTo("Restart 3 Deployments in payments on test-cluster");
        await Assert.That(action.BulkItems.Select(i => i.Key)).IsEquivalentTo(Names.Select(n => $"payments/{n}"));
        await Assert.That(action.IsQuestionVisible).IsTrue();
        await Assert.That(action.StopBulkLabel).IsEqualTo("Stop restarting");

        // Armed, not sent (UI rule 17): a restart of three workloads waits for the confirm.
        await Assert.That(server.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_refusal_among_successes_is_reported_per_object_and_does_not_stop_the_rest()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", DeploymentPath("api"), HttpStatusCode.OK, Patched("api"));
        server.Respond("PATCH", DeploymentPath("web"), HttpStatusCode.Forbidden,
            """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"deployments.apps \"web\" is forbidden: User \"narrow\" cannot patch resource \"deployments\" in API group \"apps\" in the namespace \"payments\"","reason":"Forbidden","code":403}""");
        server.Respond("PATCH", DeploymentPath("worker"), HttpStatusCode.OK, Patched("worker"));
        var tab = DeploymentTab(server.Connect());

        tab.RestartSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;
        await action.ConfirmCommand.ExecuteAsync(null);
        await DoneAsync(action);

        await Assert.That(server.Requests.Count(r => r.Method == "PATCH")).IsEqualTo(3);
        await Assert.That(action.Message)
            .IsEqualTo("Restart requested for 2 of 3 Deployments. 1 refused — each one's reason is in the list below.");
        var refused = action.BulkItems.Single(i => i.IsFailed);
        await Assert.That(refused.Key).IsEqualTo("payments/web");
        await Assert.That(refused.Detail).Contains("cannot patch resource \"deployments\"");
        await Assert.That(action.BulkItems.Count(i => i.IsDone)).IsEqualTo(2);
        await Assert.That(action.BulkItems.First(i => i.IsDone).Detail).StartsWith("Restart requested, stamped ");
    }

    [Test]
    public async Task Every_restart_allowed_reads_as_a_plain_success()
    {
        using var server = new StubApiServer();
        foreach (var name in Names)
        {
            server.Respond("PATCH", DeploymentPath(name), HttpStatusCode.OK, Patched(name));
        }

        var tab = DeploymentTab(server.Connect());
        tab.RestartSelectedCommand.Execute(null);
        var action = tab.PendingRowAction!;
        await action.ConfirmCommand.ExecuteAsync(null);
        await DoneAsync(action);

        await Assert.That(action.IsSuccess).IsTrue();
        await Assert.That(action.Message).IsEqualTo("Restart requested for 3 Deployments.");
        // Each one is a strategic stamp on its own pod template, kubectl's own key.
        await Assert.That(server.Requests.Where(r => r.Method == "PATCH").All(r => r.Body.Contains("kubectl.kubernetes.io/restartedAt", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>A selection of pods has no pod template to stamp: no bulk restart, in the menu or the palette.</summary>
    [Test]
    public async Task Pods_selected_offer_no_bulk_restart()
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
        };
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "a")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "b")));
        tab.SetSelectedRows([.. tab.Rows]);

        await Assert.That(tab.CanRestartSelectedRow).IsFalse();
        await Assert.That(tab.SingleRowActionTip).EndsWith("Delete acts on all of them.");
    }

    [Test]
    public async Task The_palette_offers_restart_beside_delete_for_several_workloads()
    {
        using var server = new StubApiServer();
        var tab = DeploymentTab(server.Connect());
        var shell = new MainWindowViewModel { Mode = ShellMode.Resources };
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;

        shell.Palette.Open();
        var rowEntries = shell.Palette.FilteredItems
            .Where(i => i.Subtitle.Contains("selected", StringComparison.Ordinal))
            .Select(i => i.Title)
            .ToList();
        shell.Palette.Close();

        await Assert.That(rowEntries).IsEquivalentTo(
            [CommandCatalog.Get(CommandId.DeleteResource).Title, CommandCatalog.Get(CommandId.RolloutRestart).Title]);
        await Assert.That(tab.RestartRowActionTip).StartsWith("Stamps the pod template of all 3 selected objects");
    }

    // ------------------------------------------------------ selection through a re-sort

    /// <summary>
    /// A metrics poll on a list sorted by CPU that leaves a few rows out of place moves each one
    /// (a Remove and an Insert), and the DataGrid drops the selection of a row removed under it.
    /// The three selected rows come back. The test plays the grid's part, as the
    /// <c>ux-bulk-selection-resort</c> harness scenario saw it: on a Remove the selection shrinks
    /// to what the grid still holds. Mutation-checked: with <c>RestoreMultipleSelection</c> taken
    /// out of the move pass, SelectedRows came back as two rows.
    /// </summary>
    [Test]
    public async Task A_multi_row_selection_survives_a_re_sort_that_moves_a_selected_row()
    {
        var tab = TestObjects.Tab();
        for (var i = 0; i < 10; i++)
        {
            tab.Apply(TestObjects.Added(TestObjects.Pod("payments", $"pod-{i}")));
            tab.Rows[^1].ApplyUsage(1_000_000L * (i + 1), 1);
        }

        tab.ToggleSort(ResourceColumn.Cpu);
        var selection = new[] { tab.VisibleRows[1], tab.VisibleRows[2], tab.VisibleRows[5] };
        tab.SelectedRow = selection[0];
        tab.SetSelectedRows(selection);

        IReadOnlyList<ResourceRowViewModel>? handedToView = null;
        tab.MultipleSelectionRestored += (_, rows) => handedToView = rows;
        var resets = 0;
        tab.VisibleRows.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                var removed = e.OldItems!.OfType<ResourceRowViewModel>().ToHashSet();
                tab.SetSelectedRows([.. tab.SelectedRows.Where(r => !removed.Contains(r))]);
            }
        };

        // The next poll: one selected pod becomes the busiest. One row out of place, so it moves.
        selection[1].ApplyUsage(1_000_000_000, 1);
        tab.ResortVisibleRows();

        await Assert.That(resets).IsEqualTo(0);
        await Assert.That(tab.VisibleRows[^1]).IsSameReferenceAs(selection[1]);
        await Assert.That(tab.SelectedRows).IsEquivalentTo(selection);
        await Assert.That(tab.SelectionSummary).IsEqualTo("3 selected");
        await Assert.That(handedToView).IsNotNull();
        await Assert.That(handedToView!).IsEquivalentTo(selection);
    }

    [Test]
    public async Task A_re_sort_that_moves_nothing_hands_nothing_to_the_view()
    {
        var tab = TestObjects.Tab();
        for (var i = 0; i < 4; i++)
        {
            tab.Apply(TestObjects.Added(TestObjects.Pod("payments", $"pod-{i}")));
            tab.Rows[^1].ApplyUsage(1_000_000L * (i + 1), 1);
        }

        tab.ToggleSort(ResourceColumn.Cpu);
        tab.SetSelectedRows([tab.VisibleRows[0], tab.VisibleRows[1]]);
        var handed = false;
        tab.MultipleSelectionRestored += (_, _) => handed = true;

        tab.ResortVisibleRows();

        await Assert.That(handed).IsFalse();
    }
}
