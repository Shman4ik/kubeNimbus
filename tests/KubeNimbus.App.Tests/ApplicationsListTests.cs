using System.Text.Json;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Applications list: its order and groups, the chips and the search box, the watch
/// events that feed it, the loading verdict (UI rule 18), the narrow-RBAC statement, and the
/// mode's persistence. Driven through the real <see cref="ApplicationsViewModel.Apply"/> the
/// watches post to, with rebuilds made synchronous — no Avalonia application, no cluster.
/// </summary>
public class ApplicationsListTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 8, 56, 0, TimeSpan.Zero);

    private static ApplicationsViewModel List()
    {
        var tab = TestObjects.Tab();
        var apps = tab.Applications;
        apps.DeferRebuilds = false;
        apps.Clock = () => Now;
        return apps;
    }

    private static DynamicResource R(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static DynamicResource Deployment(string name, int replicas, int ready, string ns = "shop") => R("""
        {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"%n","namespace":"%ns","uid":"u-%n"},
         "spec":{"replicas":%r,"selector":{"matchLabels":{"app":"%n"}},"template":{"metadata":{"labels":{"app":"%n"}}}},
         "status":{"replicas":%r,"updatedReplicas":%r,"readyReplicas":%ready}}
        """.Replace("%ns", ns).Replace("%n", name).Replace("%ready", ready.ToString()).Replace("%r", replicas.ToString()));

    private static DynamicResource CrashingPod(string name, string app, string ns = "shop") => R("""
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"%name","namespace":"%ns","labels":{"app":"%app"}},
         "spec":{"containers":[{"name":"app","image":"a"}]},
         "status":{"phase":"Running","conditions":[{"type":"Ready","status":"False"}],
           "containerStatuses":[{"name":"app","ready":false,"restartCount":3,"state":{"waiting":{"reason":"CrashLoopBackOff"}},
             "lastState":{"terminated":{"exitCode":2,"reason":"Error","finishedAt":"2026-07-30T08:50:00Z","containerID":"c://1"}}}]}}
        """.Replace("%name", name).Replace("%ns", ns).Replace("%app", app));

    private static DynamicResource ReadyPod(string name, string app, string ns = "shop") => R("""
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"%name","namespace":"%ns","labels":{"app":"%app"}},
         "spec":{"containers":[{"name":"app","image":"a"}]},
         "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}],
           "containerStatuses":[{"name":"app","ready":true,"restartCount":0,"state":{"running":{}}}]}}
        """.Replace("%name", name).Replace("%ns", ns).Replace("%app", app));

    private static void Add(ApplicationsViewModel apps, string kind, DynamicResource resource) =>
        apps.Apply(kind, TestObjects.Added(resource));

    private static string Names(ApplicationsViewModel apps) => string.Join(", ", apps.VisibleRows.Select(r => r.Name));

    // ------------------------------------------------------------------ order

    [Test]
    public async Task Attention_sorts_first_and_the_two_groups_are_captioned_on_their_first_row()
    {
        var apps = List();
        Add(apps, "Deployment", Deployment("zeta", 1, 1));
        Add(apps, "Deployment", Deployment("alpha", 1, 1));
        Add(apps, "Deployment", Deployment("broken", 1, 0));
        Add(apps, "Pod", ReadyPod("zeta-1", "zeta"));
        Add(apps, "Pod", ReadyPod("alpha-1", "alpha"));
        Add(apps, "Pod", CrashingPod("broken-1", "broken"));

        await Assert.That(Names(apps)).IsEqualTo("broken, alpha, zeta");
        await Assert.That(apps.VisibleRows[0].GroupHeader).IsEqualTo("NEEDS ATTENTION · 1");
        await Assert.That(apps.VisibleRows[1].GroupHeader).IsEqualTo("EVERYTHING ELSE · 2");
        await Assert.That(apps.VisibleRows[2].GroupHeader).IsNull();
        await Assert.That(apps.VisibleRows[0].Reason).IsEqualTo("Crash-looping (exit 2)");
    }

    /// <summary>
    /// A row is updated in place when its objects change — the same instance, so the
    /// selection and the scroll position survive a watch event — and it moves between the
    /// groups when its verdict does.
    /// </summary>
    [Test]
    public async Task A_watch_event_updates_the_row_in_place_and_moves_it_between_groups()
    {
        var apps = List();
        Add(apps, "Deployment", Deployment("web", 1, 1));
        Add(apps, "Deployment", Deployment("api", 1, 1));
        Add(apps, "Pod", ReadyPod("web-1", "web"));
        Add(apps, "Pod", ReadyPod("api-1", "api"));
        var web = apps.VisibleRows.Single(r => r.Name == "web");
        apps.SelectedRow = web;

        apps.Apply("Pod", TestObjects.Modified(CrashingPod("web-1", "web")));

        await Assert.That(apps.VisibleRows[0]).IsSameReferenceAs(web);
        await Assert.That(web.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(apps.SelectedRow).IsSameReferenceAs(web);

        apps.Apply("Pod", TestObjects.Deleted(ReadyPod("web-1", "web")));
        apps.Apply("Deployment", TestObjects.Deleted(Deployment("web", 1, 1)));
        await Assert.That(Names(apps)).IsEqualTo("api");
        await Assert.That(apps.SelectedRow).IsNull();
    }

    /// <summary>
    /// One namespace's watch relisting (a 410) clears only what that namespace seeded — a
    /// Reset from one scope blanking the rest of the list is the multi-scope version of the
    /// bug UI rule 18 was written about.
    /// </summary>
    [Test]
    public async Task A_reset_in_one_namespace_keeps_the_rows_of_the_others()
    {
        var apps = List();
        apps.Apply("Deployment", TestObjects.Added(Deployment("a", 1, 1, ns: "team-a")), "team-a");
        apps.Apply("Deployment", TestObjects.Added(Deployment("b", 1, 1, ns: "team-b")), "team-b");

        apps.Apply("Deployment", ResourceEvent<DynamicResource>.Reset, "team-a");

        await Assert.That(Names(apps)).IsEqualTo("b");
    }

    // ---------------------------------------------------------- chips and search

    [Test]
    public async Task The_search_matches_name_and_namespace_but_never_status()
    {
        var apps = List();
        Add(apps, "Deployment", Deployment("checkout", 1, 0, ns: "payments"));
        Add(apps, "Deployment", Deployment("search", 1, 1, ns: "catalog"));
        Add(apps, "Pod", CrashingPod("checkout-1", "checkout", ns: "payments"));

        apps.Filter = "catalog";
        await Assert.That(Names(apps)).IsEqualTo("search");

        apps.Filter = "check";
        await Assert.That(Names(apps)).IsEqualTo("checkout");

        apps.Filter = "Degraded";
        await Assert.That(apps.VisibleRows.Count).IsEqualTo(0);
        await Assert.That(apps.IsFilterEmpty).IsTrue();
        await Assert.That(apps.IsEmpty).IsFalse();
        await Assert.That(apps.FilterEmptyText).IsEqualTo("Nothing matches “Degraded”");
    }

    [Test]
    public async Task The_chips_narrow_to_attention_recent_deploys_and_workloads_outside_argo()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        var apps = tab.Applications;
        apps.DeferRebuilds = false;
        apps.Activate();

        var all = apps.VisibleRows.Count;
        await Assert.That(apps.AllCount).IsEqualTo(all);

        apps.IsAttentionChip = true;
        await Assert.That(apps.VisibleRows.All(r => r.NeedsAttention)).IsTrue();
        await Assert.That(apps.VisibleRows.Count).IsEqualTo(apps.AttentionCount);

        apps.IsRecentDeployChip = true;
        await Assert.That(Names(apps)).IsEqualTo("checkout");

        apps.IsNotInArgoChip = true;
        await Assert.That(apps.VisibleRows.Any(r => r.IsArgo)).IsFalse();
        await Assert.That(apps.VisibleRows.Count).IsEqualTo(apps.NotInArgoCount);

        // A false written to the checked chip is refused: one chip is always on.
        apps.IsNotInArgoChip = false;
        await Assert.That(apps.Chip).IsEqualTo(ApplicationChip.NotInArgo);

        apps.IsAllChip = true;
        await Assert.That(apps.VisibleRows.Count).IsEqualTo(all);
    }

    [Test]
    public async Task Kube_system_workloads_are_hidden_behind_a_chip_that_states_their_count()
    {
        var apps = List();
        Add(apps, "Deployment", Deployment("coredns", 1, 1, ns: "kube-system"));
        Add(apps, "Deployment", Deployment("web", 1, 1));

        await Assert.That(Names(apps)).IsEqualTo("web");
        await Assert.That(apps.SystemCount).IsEqualTo(1);
        await Assert.That(apps.SystemChipText).IsEqualTo("kube-* · 1");

        apps.ShowSystemNamespaces = true;
        await Assert.That(Names(apps)).IsEqualTo("coredns, web");
    }

    // -------------------------------------------------------- loading and RBAC

    /// <summary>
    /// UI rule 18 for this list: "no applications" only after every read has answered.
    /// Rows appear as soon as they can be built, and the scope line says what is still
    /// being read.
    /// </summary>
    [Test]
    public async Task No_empty_verdict_before_every_read_has_synced()
    {
        var apps = List();
        apps.MarkPending("Deployment");
        apps.MarkPending("Pod");
        apps.Apply("Deployment", ResourceEvent<DynamicResource>.Reset);

        await Assert.That(apps.IsLoading).IsTrue();
        await Assert.That(apps.IsEmpty).IsFalse();

        apps.Apply("Deployment", ResourceEvent<DynamicResource>.Synced);
        await Assert.That(apps.IsLoading).IsTrue();
        await Assert.That(apps.IsEmpty).IsFalse();
        await Assert.That(apps.LoadingText).IsEqualTo("Reading Pods…");

        apps.Apply("Pod", ResourceEvent<DynamicResource>.Synced);
        await Assert.That(apps.IsLoading).IsFalse();
        await Assert.That(apps.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Rows_show_while_other_kinds_are_still_being_read()
    {
        var apps = List();
        apps.MarkPending("Deployment");
        apps.MarkPending("Pod");
        Add(apps, "Deployment", Deployment("web", 2, 2));

        await Assert.That(apps.IsLoading).IsFalse();
        await Assert.That(apps.IsPartiallyLoaded).IsTrue();
        await Assert.That(apps.StillReadingText).Contains("Deployment");
        await Assert.That(Names(apps)).IsEqualTo("web");
    }

    /// <summary>A cluster-wide refusal is stated, with the namespaces that are read instead.</summary>
    [Test]
    public async Task A_cluster_wide_refusal_is_stated_never_silent()
    {
        var apps = List();
        apps.SetFallbackForFixture(
            [("Pod", "pods")], ["team-a", "team-b"],
            [("Pod", "team-b", "pods is forbidden")]);

        await Assert.That(apps.ScopeText).IsEqualTo("Namespace team-a");
        await Assert.That(apps.ScopeWarning!).Contains("Listing pods across the cluster was refused");
        await Assert.That(apps.ScopeWarning!).Contains("Also refused: pods in team-b");
    }

    [Test]
    public async Task A_refusal_with_nowhere_to_fall_back_says_what_to_do()
    {
        var apps = List();
        apps.Refuse(client: null, "Pod", @namespace: null, "pods is forbidden");

        await Assert.That(apps.HasScopeWarning).IsTrue();
        await Assert.That(apps.ScopeWarning!).Contains("it knows none");
    }

    // ------------------------------------------------------------------- page

    [Test]
    public async Task Esc_from_the_page_returns_to_the_same_row()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        var apps = tab.Applications;
        apps.DeferRebuilds = false;
        apps.Activate();

        var checkout = apps.Rows.First(r => r.Name == "checkout");
        apps.SelectedRow = checkout;
        apps.OpenSelectedCommand.Execute(null);
        await Assert.That(apps.Page!.Name).IsEqualTo("checkout");
        await Assert.That(apps.IsListVisible).IsFalse();

        await apps.ClosePageCommand.ExecuteAsync(null);
        await Assert.That(apps.IsPageOpen).IsFalse();
        await Assert.That(apps.SelectedRow).IsSameReferenceAs(checkout);
    }

    [Test]
    public async Task The_demo_list_is_the_one_the_dataset_describes()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.Applications.Activate();
        var rows = tab.Applications.Rows;

        await Assert.That(rows[0].Name).IsEqualTo("checkout");
        await Assert.That(rows[0].Reason).IsEqualTo("Crash-looping (exit 1) · 2 pods not created: namespace quota");
        await Assert.That(rows.First(r => r.Name == "fraud-detector").Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(rows.First(r => r.Name == "settlement-batch").Status).IsEqualTo(AppStatus.Missing);
        await Assert.That(rows.First(r => r.Name == "risk-scoring").Status).IsEqualTo(AppStatus.Unknown);
        await Assert.That(rows.First(r => r.Name == "payment-service-report-generator").Status).IsEqualTo(AppStatus.Stalled);
        await Assert.That(rows.First(r => r.Name == "notification-dispatcher").Status).IsEqualTo(AppStatus.Progressing);
        await Assert.That(rows.First(r => r.Name == "quarterly-report").Status).IsEqualTo(AppStatus.Suspended);

        // A workload an Argo Application tracks has no row of its own.
        await Assert.That(rows.Any(r => r.Name == "checkout-worker")).IsFalse();
        await Assert.That(rows[0].LastDeployText).IsEqualTo("8f3c1d9 · 14 min ago");
        await Assert.That(rows[0].IsRecentDeploy).IsTrue();
    }
}

/// <summary>
/// The mode switch: persisted as session state, default Applications, and never restarting
/// anything. <c>[NotInParallel]</c> for the reason <c>GridLayoutStoreTests</c> gives: the
/// workspace is a real file behind the process-global <c>WorkspaceStore.DirectoryOverride</c>,
/// and a test redirecting it between this one's write and read would read someone else's.
/// </summary>
[NotInParallel]
public class ShellModeTests
{
    [Test]
    public async Task The_mode_defaults_to_Applications_and_persists_in_the_workspace()
    {
        TestObjects.RedirectStores();
        var first = new MainWindowViewModel();
        await Assert.That(first.Mode).IsEqualTo(ShellMode.Applications);
        await Assert.That(first.ModeIndex).IsEqualTo(0);

        first.ModeIndex = 1;
        await Assert.That(first.Mode).IsEqualTo(ShellMode.Resources);
        await Assert.That(WorkspaceStore.Load().ShellMode).IsEqualTo("Resources");

        var second = new MainWindowViewModel();
        await Assert.That(second.Mode).IsEqualTo(ShellMode.Resources);

        second.ShowApplicationsCommand.Execute(null);
        await Assert.That(WorkspaceStore.Load().ShellMode).IsEqualTo("Applications");
    }

    /// <summary>
    /// Switching to Resources and back keeps the Applications list's rows and the Resources
    /// list's rows — neither mode's state is rebuilt by the switch.
    /// </summary>
    [Test]
    public async Task Switching_modes_keeps_both_lists()
    {
        TestObjects.RedirectStores();
        var shell = new MainWindowViewModel();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;

        var apps = tab.Applications.Rows.ToList();
        var resources = tab.Rows.ToList();
        await Assert.That(apps.Count).IsGreaterThan(0);

        shell.Mode = ShellMode.Resources;
        shell.Mode = ShellMode.Applications;

        await Assert.That(tab.Applications.Rows.SequenceEqual(apps)).IsTrue();
        await Assert.That(tab.Rows.SequenceEqual(resources)).IsTrue();
        await Assert.That(shell.ShowsApplications).IsTrue();
    }
}
