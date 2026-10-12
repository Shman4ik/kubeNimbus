using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #303: the Argo dashboard on one cluster is a list+watch. These drive the batches the watch
/// delivers through <see cref="ClusterTabViewModel.ApplyArgoBatch"/>: loading ends at the first
/// row or at Synced (never at Reset), a status change replaces only its own row and keeps the
/// selection, and a relist does not blank the grid. The live half — a sync started with
/// <c>kubectl</c> reaching the stream — is <c>Live/ArgoLiveTests</c>.
/// </summary>
public class ArgoDashboardWatchTests
{
    private static readonly ResourceDescriptor Applications =
        new(ArgoCd.Group, "v1alpha1", "Application", "applications", "application", true, ["app"], []);

    private static ResourceEvent<DynamicResource> Event(
        ResourceEventType type, string name, string sync = "Synced", string health = "Healthy", string phase = "")
    {
        var json = $$"""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application",
             "metadata":{"name":"{{name}}","namespace":"argocd"},
             "spec":{"project":"default"},
             "status":{"sync":{"status":"{{sync}}"},"health":{"status":"{{health}}"},
                       "operationState":{"phase":"{{phase}}"} } }
            """;
        using var document = JsonDocument.Parse(json);
        return new ResourceEvent<DynamicResource>(type, DynamicResource.FromListItem(document.RootElement.Clone(), Applications));
    }

    private static ClusterTabViewModel ArgoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.IsArgoDashboard));
        tab.ArgoApplications.Clear();
        tab.IsArgoLoading = true;
        return tab;
    }

    [Test]
    public async Task A_reset_keeps_loading_and_the_first_rows_end_it()
    {
        var tab = ArgoTab();
        var table = new ArgoApplicationTable();

        tab.ApplyArgoBatch(table, [ResourceEvent<DynamicResource>.Reset], Applications);
        await Assert.That(tab.IsArgoLoading).IsTrue();
        await Assert.That(tab.IsArgoEmpty).IsFalse();

        tab.ApplyArgoBatch(table, [Event(ResourceEventType.Added, "shop"), Event(ResourceEventType.Added, "ledger")], Applications);
        await Assert.That(tab.IsArgoLoading).IsFalse();
        await Assert.That(tab.ArgoApplications.Count).IsEqualTo(2);
        await Assert.That(tab.ArgoCounts!.Total).IsEqualTo(2);
    }

    [Test]
    public async Task A_sync_started_elsewhere_shows_its_phase_on_the_same_row_with_the_selection_kept()
    {
        var tab = ArgoTab();
        var table = new ArgoApplicationTable();
        tab.ApplyArgoBatch(
            table,
            [ResourceEvent<DynamicResource>.Reset, Event(ResourceEventType.Added, "alpha"), Event(ResourceEventType.Added, "beta"),
             ResourceEvent<DynamicResource>.Synced],
            Applications);
        tab.SelectedArgoApplication = tab.ArgoApplications.Single(a => a.Name == "beta");
        var alpha = tab.ArgoApplications.Single(a => a.Name == "alpha");

        tab.ApplyArgoBatch(table, [Event(ResourceEventType.Modified, "beta", phase: "Running")], Applications);

        var beta = tab.ArgoApplications.Single(a => a.Name == "beta");
        await Assert.That(beta.Application.OperationPhase).IsEqualTo("Running");
        await Assert.That(tab.SelectedArgoApplication).IsSameReferenceAs(beta);
        await Assert.That(tab.ArgoApplications.Single(a => a.Name == "alpha")).IsSameReferenceAs(alpha);
    }

    [Test]
    public async Task A_deleted_application_leaves_and_a_relist_does_not_blank_the_grid()
    {
        var tab = ArgoTab();
        var table = new ArgoApplicationTable();
        tab.ApplyArgoBatch(
            table,
            [Event(ResourceEventType.Added, "alpha"), Event(ResourceEventType.Added, "beta"), ResourceEvent<DynamicResource>.Synced],
            Applications);

        tab.ApplyArgoBatch(table, [Event(ResourceEventType.Deleted, "alpha")], Applications);
        await Assert.That(tab.ArgoApplications.Select(a => a.Name)).IsEquivalentTo(["beta"]);

        // 410 Gone: the stream relists. Until the relist has something, the rows stay.
        tab.ApplyArgoBatch(table, [ResourceEvent<DynamicResource>.Reset], Applications);
        await Assert.That(tab.ArgoApplications.Select(a => a.Name)).IsEquivalentTo(["beta"]);

        tab.ApplyArgoBatch(table, [ResourceEvent<DynamicResource>.Synced], Applications);
        await Assert.That(tab.ArgoApplications).IsEmpty();
        await Assert.That(tab.IsArgoEmpty).IsTrue();
    }
}
