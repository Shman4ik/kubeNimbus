using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #306: the Argo dashboard's fleet half. Two clusters each serve an <c>argocd/checkout</c>
/// Application, read by <c>ClusterFleet.ListArgoApplicationsAsync</c> from two stub API
/// servers. A row's sync goes to its own cluster's client — never the tab's, which would sync
/// a namesake on the wrong cluster — and to none once its cluster has left the fleet. A fleet
/// read clears only the warning it set itself.
/// </summary>
[NotInParallel]
public class ArgoFleetRoutingTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static readonly ResourceDescriptor ApplicationDescriptor =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []);

    private const string SyncPath = "/apis/argoproj.io/v1alpha1/namespaces/argocd/applications/checkout";

    private static StubApiServer ArgoCluster()
    {
        var server = new StubApiServer();
        server.Respond("GET", "/api", HttpStatusCode.OK, """
            {"apiVersion":"apidiscovery.k8s.io/v2","kind":"APIGroupDiscoveryList","items":[]}
            """);
        server.Respond("GET", "/apis", HttpStatusCode.OK, """
            {"apiVersion":"apidiscovery.k8s.io/v2","kind":"APIGroupDiscoveryList","items":[
              {"metadata":{"name":"argoproj.io"},"versions":[
                {"version":"v1alpha1","freshness":"Current","resources":[
                  {"resource":"applications","responseKind":{"kind":"Application"},"scope":"Namespaced",
                   "singularResource":"application","verbs":["get","list","watch","patch"]}]}]}]}
            """);
        server.Respond("GET", "/apis/argoproj.io/v1alpha1/applications", HttpStatusCode.OK, """
            {"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationList","metadata":{"resourceVersion":"1"},"items":[
              {"metadata":{"name":"checkout","namespace":"argocd","uid":"u1"},"spec":{"project":"default"},
               "status":{"sync":{"status":"Synced"},"health":{"status":"Healthy"}}}]}
            """);
        return server;
    }

    private static ClusterTabViewModel ArgoTab(ClusterClient own, Func<IReadOnlyList<FleetMember>> members)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context) { Client = own, FleetMembersProvider = members };
        var section = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        section.Kinds.Add(new SidebarKindViewModel(ApplicationDescriptor, "argo"));
        tab.SidebarSections.Add(section);
        tab.IsFleetView = true;
        tab.IsArgoView = true;
        return tab;
    }

    private static async Task<bool> ArrivesAsync(StubApiServer server, string method, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (server.Requests.Any(r => r.Method == method && r.Path == path))
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    /// <summary>
    /// Mutation-checked by making <c>ClientForArgoRow</c> return the tab's own client for every
    /// row: the sync then lands on the tab's server and this fails.
    /// </summary>
    [Test]
    public async Task A_fleet_rows_sync_goes_to_its_own_cluster_and_to_none_once_it_left()
    {
        using var own = new StubApiServer();
        using var east = ArgoCluster();
        using var west = ArgoCluster();
        List<FleetMember> members =
        [
            new("east", east.Connect(), ClusterEnvironment.Development),
            new("west", west.Connect(), ClusterEnvironment.Development),
        ];
        var tab = ArgoTab(own.Connect(), () => members);

        await tab.ReloadArgoCommand.ExecuteAsync(null);

        await Assert.That(string.Join(",", tab.ArgoApplications.Select(a => a.ClusterName).Order(StringComparer.Ordinal)))
            .IsEqualTo("east,west");

        tab.SelectedArgoApplication = tab.ArgoApplications.Single(a => a.ClusterName == "west");
        tab.SyncArgoApplicationCommand.Execute(null);

        await Assert.That(await ArrivesAsync(west, "PATCH", SyncPath)).IsTrue();
        await Assert.That(east.Requests.Any(r => r.Method == "PATCH")).IsFalse();
        await Assert.That(own.Requests.Any(r => r.Method == "PATCH")).IsFalse();

        // West leaves the fleet: its row's action has no client to go to, and none is borrowed.
        members.RemoveAll(m => m.ClusterName == "west");
        tab.PendingRowAction = null;
        var westPatches = west.Requests.Count(r => r.Method == "PATCH");
        tab.SyncArgoApplicationCommand.Execute(null);
        await Task.Delay(200);

        await Assert.That(tab.PendingRowAction).IsNull();
        await Assert.That(west.Requests.Count(r => r.Method == "PATCH")).IsEqualTo(westPatches);
        await Assert.That(east.Requests.Any(r => r.Method == "PATCH")).IsFalse();
        await Assert.That(own.Requests.Any(r => r.Method == "PATCH")).IsFalse();
    }

    /// <summary>
    /// Mutation-checked by restoring the fleet branch's <c>ConnectionWarning = null</c> on a
    /// read with no failures: the unrelated warning is then erased.
    /// </summary>
    [Test]
    public async Task A_successful_fleet_read_keeps_an_unrelated_warning_and_clears_its_own()
    {
        using var own = new StubApiServer();
        using var east = ArgoCluster();
        List<FleetMember> members = [new("east", east.Connect(), ClusterEnvironment.Development)];
        var tab = ArgoTab(own.Connect(), () => members);

        tab.ConnectionWarning = "Watch connection lost — retrying.";
        await tab.ReloadArgoCommand.ExecuteAsync(null);
        await Assert.That(tab.ConnectionWarning).IsEqualTo("Watch connection lost — retrying.");

        tab.ConnectionWarning = $"{ClusterTabViewModel.ArgoReadWarning} on east: timed out";
        await tab.ReloadArgoCommand.ExecuteAsync(null);
        await Assert.That(tab.ConnectionWarning).IsNull();
    }
}
