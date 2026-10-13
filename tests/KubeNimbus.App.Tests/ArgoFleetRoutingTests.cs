using System.Net;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Argo dashboard's fleet half. The fleet list is one watch per member (#336), folded per
/// cluster into an <see cref="ArgoFleetTable"/>; these drive its batches through the view model
/// as the watch delivers them (no Avalonia dispatcher runs here), against two stub API servers
/// that answer the sync. #306: a row's sync goes to its own cluster's client — never the tab's,
/// which would sync a namesake on the wrong cluster — and to none once its cluster has left the
/// fleet; the list clears only the warning it set itself. #339: a refusal ends the wait and is
/// stated. The engine halves are <c>WatchCacheTests</c> (a 403 ends a shared watch) and
/// <c>Live/ArgoLiveTests</c> (the fleet watch against a real Argo CD).
/// </summary>
[NotInParallel]
public class ArgoFleetRoutingTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static readonly ResourceDescriptor ApplicationDescriptor =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []);

    private const string SyncPath = "/apis/argoproj.io/v1alpha1/namespaces/argocd/applications/checkout";

    private const string Refusal =
        "applications.argoproj.io is forbidden: User \"system:serviceaccount:team:viewer\" cannot list resource \"applications\" in API group \"argoproj.io\" at the cluster scope";

    private static ClusterTabViewModel ArgoTab(ClusterClient? own, Func<IReadOnlyList<FleetMember>>? members)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context) { Client = own, FleetMembersProvider = members };
        var section = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        section.Kinds.Add(new SidebarKindViewModel(ApplicationDescriptor, "argo"));
        tab.SidebarSections.Add(section);
        tab.IsFleetView = members is not null;
        tab.IsArgoView = true;
        return tab;
    }

    private static FleetResourceEvent Event(string cluster, ResourceEventType type, string name = "checkout", string phase = "")
    {
        var json = $$"""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application",
             "metadata":{"name":"{{name}}","namespace":"argocd"},"spec":{"project":"default"},
             "status":{"sync":{"status":"Synced"},"health":{"status":"Healthy"},"operationState":{"phase":"{{phase}}"} } }
            """;
        using var document = JsonDocument.Parse(json);
        return new FleetResourceEvent(
            cluster,
            new ResourceEvent<DynamicResource>(type, DynamicResource.FromListItem(document.RootElement.Clone(), ApplicationDescriptor)));
    }

    private static IReadOnlyList<FleetResourceEvent> Listed(params string[] clusters) =>
    [
        .. clusters.SelectMany(c => new[]
        {
            new FleetResourceEvent(c, ResourceEvent<DynamicResource>.Reset),
            Event(c, ResourceEventType.Added),
            new FleetResourceEvent(c, ResourceEvent<DynamicResource>.Synced),
        }),
    ];

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

    private static string Clusters(ClusterTabViewModel tab) =>
        string.Join(",", tab.ArgoApplications.Select(a => a.ClusterName).Order(StringComparer.Ordinal));

    /// <summary>
    /// Mutation-checked by making <c>ClientForArgoRow</c> return the tab's own client for every
    /// row: the sync then lands on the tab's server and this fails.
    /// </summary>
    [Test]
    public async Task A_fleet_rows_sync_goes_to_its_own_cluster_and_to_none_once_it_left()
    {
        using var own = new StubApiServer();
        using var east = new StubApiServer();
        using var west = new StubApiServer();
        List<FleetMember> members =
        [
            new("east", east.Connect(), ClusterEnvironment.Development),
            new("west", west.Connect(), ClusterEnvironment.Development),
        ];
        var tab = ArgoTab(own.Connect(), () => members);
        var table = new ArgoFleetTable(members.Select(m => new FleetTarget(m, ApplicationDescriptor)));

        tab.ApplyArgoFleetBatch(table, Listed("east", "west"), new ClusterTabViewModel.ArgoFleetProblems { Watched = 2 });
        await Assert.That(Clusters(tab)).IsEqualTo("east,west");

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
    /// #336: a change on one member — a sync started outside the app — replaces that member's
    /// row only and keeps the selection; a member's relist keeps its rows until it has synced
    /// (#339's flicker); the list waits until every member has answered or shown a row.
    /// Mutation-checked by dropping <c>ArgoApplicationTable</c>'s relist snapshot: west's Reset
    /// then takes its row off the list until it has relisted.
    /// </summary>
    [Test]
    public async Task The_fleet_list_follows_each_members_watch_on_its_own_rows()
    {
        var tab = ArgoTab(own: null, () => []);
        FleetTarget Target(string name) => new(new FleetMember(name, null!, ClusterEnvironment.Development), ApplicationDescriptor);
        var table = new ArgoFleetTable([Target("east"), Target("west")]);
        var problems = new ClusterTabViewModel.ArgoFleetProblems { Watched = 2 };
        tab.IsArgoLoading = true;

        tab.ApplyArgoFleetBatch(table, [new("east", ResourceEvent<DynamicResource>.Reset)], problems);
        await Assert.That(tab.IsArgoLoading).IsTrue();

        tab.ApplyArgoFleetBatch(table, Listed("east", "west"), problems);
        await Assert.That(tab.IsArgoLoading).IsFalse();
        tab.SelectedArgoApplication = tab.ArgoApplications.Single(a => a.ClusterName == "east");
        var east = tab.SelectedArgoApplication;

        tab.ApplyArgoFleetBatch(table, [Event("west", ResourceEventType.Modified, phase: "Running")], problems);
        await Assert.That(tab.ArgoApplications.Single(a => a.ClusterName == "west").Application.OperationPhase).IsEqualTo("Running");
        await Assert.That(tab.ArgoApplications.Single(a => a.ClusterName == "east")).IsSameReferenceAs(east);
        await Assert.That(tab.SelectedArgoApplication).IsSameReferenceAs(east);

        tab.ApplyArgoFleetBatch(table, [new("west", ResourceEvent<DynamicResource>.Reset)], problems);
        await Assert.That(Clusters(tab)).IsEqualTo("east,west");
        await Assert.That(tab.ArgoCounts!.Total).IsEqualTo(2);
    }

    /// <summary>
    /// #336: a member that leaves the fleet stops being read — the fleet change restarts the
    /// list's watch over the members that remain, and a batch the old watch still had in flight
    /// changes nothing.
    /// </summary>
    [Test]
    public async Task A_member_leaving_the_fleet_restarts_the_watch_without_it()
    {
        using var east = new StubApiServer();
        using var west = new StubApiServer();
        List<FleetMember> members =
        [
            new("east", east.Connect(), ClusterEnvironment.Development),
            new("west", west.Connect(), ClusterEnvironment.Development),
        ];
        var tab = ArgoTab(own: null, () => members);
        tab.Client = east.Connect();
        var table = new ArgoFleetTable(members.Select(m => new FleetTarget(m, ApplicationDescriptor)));
        tab.ApplyArgoFleetBatch(table, Listed("east", "west"), new ClusterTabViewModel.ArgoFleetProblems { Watched = 2 });

        members.RemoveAll(m => m.ClusterName == "west");
        tab.RefreshFleetMembership();

        // The old rows went with the old watch; the new one is reading the members that remain.
        await Assert.That(tab.ArgoApplications).IsEmpty();
        await Assert.That(tab.IsArgoLoading).IsTrue();
        await Assert.That(await ArrivesAsync(east, "GET", "/apis")).IsTrue();
        await Task.Delay(300);
        await Assert.That(west.Requests.Any(r => r.Path == "/apis")).IsFalse();
    }

    /// <summary>
    /// The fleet list clears only the warning it set itself (#306): an unconditional
    /// <c>ConnectionWarning = null</c> on a batch with no problems would erase the unrelated one.
    /// </summary>
    [Test]
    public async Task A_fleet_batch_keeps_an_unrelated_warning_and_clears_its_own()
    {
        var tab = ArgoTab(own: null, () => []);
        FleetTarget Target(string name) => new(new FleetMember(name, null!, ClusterEnvironment.Development), ApplicationDescriptor);
        var table = new ArgoFleetTable([Target("east")]);
        var problems = new ClusterTabViewModel.ArgoFleetProblems { Watched = 1 };

        tab.ConnectionWarning = "Watch connection lost — retrying.";
        tab.ApplyArgoFleetBatch(table, Listed("east"), problems);
        await Assert.That(tab.ConnectionWarning).IsEqualTo("Watch connection lost — retrying.");

        tab.ConnectionWarning = $"{ClusterTabViewModel.ArgoReadWarning} on east: timed out";
        tab.ApplyArgoFleetBatch(table, [Event("east", ResourceEventType.Modified)], problems);
        await Assert.That(tab.ConnectionWarning).IsNull();
    }

    /// <summary>
    /// #339: a 403 on the one-cluster dashboard's watch ends the wait with the refusal stated in
    /// the server's words — not "connection lost" over a spinner, and not "No Applications".
    /// Mutation-checked: with <c>EndArgoWatch</c> treating every ending as a warning, the
    /// refusal state never shows and the empty state claims there are no Applications.
    /// </summary>
    [Test]
    public async Task A_refused_dashboard_says_so_in_the_servers_words()
    {
        var tab = ArgoTab(own: null, members: null);
        tab.IsArgoLoading = true;
        tab.ConnectionWarning = $"{ClusterTabViewModel.ArgoReadWarning}: Watch connection lost (ForbiddenException); retrying in 1s.";

        tab.EndArgoWatch(
            WatchRefusedException.From(new HttpRequestException(Refusal, null, HttpStatusCode.Forbidden))!,
            CancellationToken.None);

        await Assert.That(tab.IsArgoLoading).IsFalse();
        await Assert.That(tab.ArgoRefusal).IsEqualTo(Refusal);
        await Assert.That(tab.IsArgoRefused).IsTrue();
        await Assert.That(tab.IsArgoEmpty).IsFalse();
        await Assert.That(tab.ConnectionWarning).IsNull();

        // Another ending is a warning over the (empty) list, and the wait is over all the same.
        var other = ArgoTab(own: null, members: null);
        other.IsArgoLoading = true;
        other.EndArgoWatch(new InvalidOperationException("The watch ended."), CancellationToken.None);
        await Assert.That(other.IsArgoLoading).IsFalse();
        await Assert.That(other.IsArgoRefused).IsFalse();
        await Assert.That(other.ConnectionWarning).IsEqualTo($"{ClusterTabViewModel.ArgoReadWarning}: The watch ended.");

        // A watch that was already replaced says nothing.
        var replaced = ArgoTab(own: null, members: null);
        replaced.IsArgoLoading = true;
        replaced.EndArgoWatch(new WatchRefusedException(Refusal), new CancellationToken(canceled: true));
        await Assert.That(replaced.IsArgoRefused).IsFalse();
        await Assert.That(replaced.IsArgoLoading).IsTrue();
    }

    /// <summary>
    /// #339, list allowed and watch refused: the dashboard has listed, so its rows are real and
    /// stay; the not-permitted panel must not print over them, and the status bar says live
    /// updates stopped, as the Resources list does. Mutation-checked: without the rows branch in
    /// <c>EndArgoWatch</c> the refusal panel shows over the rows and nothing says they stopped.
    /// </summary>
    [Test]
    public async Task A_watch_refused_after_the_list_keeps_the_rows_and_says_updates_stopped()
    {
        var tab = ArgoTab(own: null, members: null);
        var table = new ArgoApplicationTable();
        tab.ApplyArgoBatch(
            table,
            [ResourceEvent<DynamicResource>.Reset, Event("", ResourceEventType.Added).Event, ResourceEvent<DynamicResource>.Synced],
            ApplicationDescriptor);
        await Assert.That(tab.ArgoApplications.Count).IsEqualTo(1);

        tab.EndArgoWatch(new WatchRefusedException(Refusal), CancellationToken.None);

        await Assert.That(tab.ArgoApplications.Count).IsEqualTo(1);
        await Assert.That(tab.IsArgoRefused).IsFalse();
        await Assert.That(tab.IsArgoEmpty).IsFalse();
        await Assert.That(tab.IsArgoLoading).IsFalse();
        await Assert.That(tab.Status).IsEqualTo($"Live updates stopped: {Refusal}");
    }

    /// <summary>
    /// #339 in the fleet: a refused member has answered, so the list stops waiting for it and
    /// names it in the warning while the others' rows show; when every member is refused, the
    /// refusal is the dashboard's state instead of a warning.
    /// </summary>
    [Test]
    public async Task A_refused_fleet_member_is_named_and_the_rest_show()
    {
        var tab = ArgoTab(own: null, () => []);
        FleetTarget Target(string name) => new(new FleetMember(name, null!, ClusterEnvironment.Development), ApplicationDescriptor);
        var table = new ArgoFleetTable([Target("east"), Target("west")]);
        var problems = new ClusterTabViewModel.ArgoFleetProblems { Watched = 2 };
        tab.IsArgoLoading = true;

        tab.ApplyArgoFleetBatch(table, [new("east", ResourceEvent<DynamicResource>.Reset)], problems);
        tab.EndArgoFleetMember(table, problems, "west", new WatchRefusedException(Refusal));
        await Assert.That(tab.IsArgoLoading).IsTrue();

        tab.ApplyArgoFleetBatch(table, Listed("east"), problems);
        await Assert.That(Clusters(tab)).IsEqualTo("east");
        await Assert.That(tab.ConnectionWarning).IsEqualTo($"{ClusterTabViewModel.ArgoReadWarning} on west: {Refusal}");
        await Assert.That(tab.IsArgoRefused).IsFalse();
        await Assert.That(tab.IsArgoLoading).IsFalse();

        var alone = ArgoTab(own: null, () => []);
        var lone = new ArgoFleetTable([Target("west")]);
        var loneProblems = new ClusterTabViewModel.ArgoFleetProblems { Watched = 1 };
        alone.IsArgoLoading = true;
        alone.EndArgoFleetMember(lone, loneProblems, "west", new WatchRefusedException(Refusal));
        await Assert.That(alone.ArgoRefusal).IsEqualTo(Refusal);
        await Assert.That(alone.IsArgoLoading).IsFalse();
        await Assert.That(alone.IsArgoEmpty).IsFalse();
        await Assert.That(alone.ConnectionWarning).IsNull();
    }
}
