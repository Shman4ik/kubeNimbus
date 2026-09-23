using System.Text.Json;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// L3 — logs from everywhere a pod is named: workload detail's and node detail's pod lists,
/// an Event about a pod, and an Argo Application's managed workloads. What is pinned is that
/// every one of them routes to the cluster tab's one open-logs path — so the pane opened, the
/// inspector tab reused and the "Open logs maximized" preference are the resource list's own
/// — and that a pod which is not there any more is <em>said</em>, never a dead click.
///
/// <para>
/// Driven on the demo cluster through the real panes the real double-click opens, not on
/// hand-built view models: the wiring (which delegate a pane is handed, bound to which
/// cluster) is the thing that could be wrong. The "gone", "refused" and "names no pods"
/// sentences for a real cluster go through the same method with a stand-in
/// <see cref="NamedObjectSource"/>, the <see cref="LogTargetSource"/> precedent.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> for <see cref="RowLogsTests"/>' reason: the preference tests
/// write the process-wide settings file.
/// </para>
/// </summary>
[NotInParallel]
public class PaneLogsTests
{
    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    private static void SelectKind(ClusterTabViewModel tab, string group, string kind) =>
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { } d && d.Group == group && d.Kind == kind));

    private static async Task WithOpenLogsMaximized(Func<Task> body)
    {
        App.Update(s => s with { OpenLogsMaximized = true });
        try
        {
            await body();
        }
        finally
        {
            App.Update(s => s with { OpenLogsMaximized = false });
        }
    }

    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static DynamicResource EventAbout(string kind, string apiVersion, string name, string eventApiVersion = "v1") =>
        Parse($$"""
            {
              "apiVersion": "{{eventApiVersion}}",
              "kind": "Event",
              "metadata": { "name": "{{name}}.17f2a1", "namespace": "payments", "uid": "{{name}}-event" },
              "type": "Warning",
              "reason": "BackOff",
              "message": "Back-off restarting failed container",
              "{{(eventApiVersion == "v1" ? "involvedObject" : "regarding")}}":
                { "apiVersion": "{{apiVersion}}", "kind": "{{kind}}", "name": "{{name}}", "namespace": "payments" }
            }
            """);

    // ------------------------------------------------------------ workload detail

    private static (ClusterTabViewModel Tab, WorkloadDetailTabViewModel Detail) WorkloadDetail()
    {
        var tab = DemoTab();
        SelectKind(tab, "apps", "Deployment");
        tab.SelectedRow = tab.Rows.First(r => r.Name == "checkout-worker");
        tab.OpenSelectedCommand.Execute(null);
        return (tab, (WorkloadDetailTabViewModel)tab.SelectedInspectorTab!);
    }

    [Test]
    public async Task L_in_workload_detail_opens_the_selected_pods_logs_in_a_tab_of_its_own()
    {
        var (tab, detail) = WorkloadDetail();
        await Assert.That(detail.Pods.Count).IsGreaterThan(0);
        var pod = detail.Pods[^1];
        detail.SelectedPod = pod;

        await detail.PodLogsCommand.ExecuteAsync(null);

        // The pane is the resource list's own — pod detail on its Logs tab — and it is a
        // second inspector tab, never a replacement of the workload pane (UI rule 5).
        var logs = tab.SelectedInspectorTab as PodDetailTabViewModel;
        await Assert.That(logs).IsNotNull();
        await Assert.That(logs!.PodName).IsEqualTo(pod.Name);
        await Assert.That(logs.SelectedDetailTabIndex).IsEqualTo(0);
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(2);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
        await Assert.That(detail.LogsNotice).IsNull();
    }

    [Test]
    public async Task L_again_reuses_the_pods_tab_as_the_list_does()
    {
        var (tab, detail) = WorkloadDetail();
        detail.SelectedPod = detail.Pods[0];
        await detail.PodLogsCommand.ExecuteAsync(null);
        var first = tab.SelectedInspectorTab;

        tab.SelectedInspectorTab = detail;
        await detail.PodLogsCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(first);
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Shift_L_in_workload_detail_opens_them_maximized()
    {
        var (tab, detail) = WorkloadDetail();
        detail.SelectedPod = detail.Pods[0];

        await detail.PodLogsMaximizedCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task Workload_detail_follows_the_open_logs_maximized_preference() => await WithOpenLogsMaximized(async () =>
    {
        // The proof that the pane goes through OpenLogsForAsync rather than building its
        // own log tab: only that path reads the preference.
        var (tab, detail) = WorkloadDetail();

        await detail.OpenPodLogsAsync(detail.Pods[0], maximized: false);

        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    });

    [Test]
    public async Task The_logs_icon_selects_its_row_in_workload_detail()
    {
        var (_, detail) = WorkloadDetail();
        var pod = detail.Pods[^1];
        detail.SelectedPod = detail.Pods[0];

        await detail.OpenPodLogsAsync(pod, maximized: false);

        await Assert.That(detail.SelectedPod).IsSameReferenceAs(pod);
    }

    [Test]
    public async Task A_pod_that_is_not_there_is_stated_in_workload_detail_not_a_dead_click()
    {
        var (tab, detail) = WorkloadDetail();
        var ghost = new ResourceRowViewModel(TestObjects.Pod("payments", "checkout-worker-gone-xyz"));

        await detail.OpenPodLogsAsync(ghost, maximized: true);

        await Assert.That(detail.LogsNotice).IsNotNull();
        await Assert.That(detail.LogsNotice!).Contains("checkout-worker-gone-xyz");
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(detail);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();

        // The next open that works clears it.
        await detail.OpenPodLogsAsync(detail.Pods[0], maximized: false);
        await Assert.That(detail.LogsNotice).IsNull();
    }

    [Test]
    public async Task Workload_detail_offers_no_logs_without_a_selected_pod()
    {
        var (_, detail) = WorkloadDetail();
        detail.SelectedPod = null;

        await Assert.That(detail.PodLogsCommand.CanExecute(null)).IsFalse();
        await Assert.That(detail.PodLogsMaximizedCommand.CanExecute(null)).IsFalse();
    }

    // ---------------------------------------------------------------- node detail

    private static (ClusterTabViewModel Tab, NodeDetailTabViewModel Detail) NodeDetail()
    {
        var tab = DemoTab();
        SelectKind(tab, "", "Node");
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        tab.OpenSelectedCommand.Execute(null);
        return (tab, (NodeDetailTabViewModel)tab.SelectedInspectorTab!);
    }

    [Test]
    public async Task L_in_node_detail_opens_the_selected_pods_logs()
    {
        var (tab, detail) = NodeDetail();
        await Assert.That(detail.Pods.Count).IsGreaterThan(0);
        var pod = detail.Pods[0];
        detail.SelectedPod = pod;

        await detail.PodLogsCommand.ExecuteAsync(null);

        var logs = tab.SelectedInspectorTab as PodDetailTabViewModel;
        await Assert.That(logs).IsNotNull();
        await Assert.That(logs!.PodName).IsEqualTo(pod.Name);
        await Assert.That(logs.SelectedDetailTabIndex).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Shift_L_in_node_detail_opens_them_maximized()
    {
        var (tab, detail) = NodeDetail();
        detail.SelectedPod = detail.Pods[0];

        await detail.PodLogsMaximizedCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task Node_detail_follows_the_open_logs_maximized_preference() => await WithOpenLogsMaximized(async () =>
    {
        var (tab, detail) = NodeDetail();

        await detail.OpenPodLogsAsync(detail.Pods[0], maximized: false);

        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    });

    [Test]
    public async Task A_pod_gone_from_the_node_is_stated_above_its_list()
    {
        var (tab, detail) = NodeDetail();
        var ghost = new NodePodViewModel(TestObjects.Pod("payments", "rescheduled-away"));

        await detail.OpenPodLogsAsync(ghost, maximized: false);

        await Assert.That(detail.HasLogsNotice).IsTrue();
        await Assert.That(detail.LogsNotice!).Contains("rescheduled-away");
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(detail);
    }

    // --------------------------------------------------------------- Events list

    private static ClusterTabViewModel EventsTab()
    {
        var tab = DemoTab();
        SelectKind(tab, "", "Event");
        return tab;
    }

    [Test]
    public async Task An_event_about_a_pod_has_the_logs_icon_and_one_about_anything_else_does_not()
    {
        var tab = EventsTab();

        var aboutPods = tab.Rows.Where(r => r.Resource.InvolvedObject() is { Kind: "Pod" }).ToList();
        var aboutOthers = tab.Rows.Where(r => r.Resource.InvolvedObject() is not { Kind: "Pod" }).ToList();
        await Assert.That(aboutPods.Count).IsGreaterThan(0);
        await Assert.That(aboutOthers.Count).IsGreaterThan(0);

        await Assert.That(aboutPods.All(r => r.HasLogs)).IsTrue();
        await Assert.That(aboutOthers.Any(r => r.HasLogs)).IsFalse();
    }

    [Test]
    [Arguments("v1")]
    [Arguments("events.k8s.io/v1")]
    public async Task Both_event_shapes_name_their_pod(string eventApiVersion)
    {
        var pod = LogTarget.InvolvedPod(EventAbout("Pod", "v1", "web-1", eventApiVersion));

        await Assert.That(pod).IsNotNull();
        await Assert.That(pod!.Name).IsEqualTo("web-1");
    }

    [Test]
    public async Task An_event_about_a_workload_or_an_impostor_pod_names_no_pod()
    {
        await Assert.That(LogTarget.InvolvedPod(EventAbout("Deployment", "apps/v1", "web"))).IsNull();

        // A CRD called Pod in some other group is not a pod.
        await Assert.That(LogTarget.InvolvedPod(EventAbout("Pod", "example.com/v1", "p"))).IsNull();

        // And a pod is not an event about itself.
        await Assert.That(LogTarget.InvolvedPod(TestObjects.Pod("shop", "web-1"))).IsNull();
    }

    [Test]
    public async Task L_on_an_event_about_a_pod_opens_that_pods_logs()
    {
        var tab = EventsTab();
        var row = tab.Rows.First(r => r.Resource.InvolvedObject() is { Kind: "Pod" });
        var podName = row.Resource.InvolvedObject()!.Name;
        tab.SelectedRow = row;

        await Assert.That(tab.OpenLogsCommand.CanExecute(null)).IsTrue();
        await tab.OpenLogsCommand.ExecuteAsync(null);

        var logs = tab.SelectedInspectorTab as PodDetailTabViewModel;
        await Assert.That(logs).IsNotNull();
        await Assert.That(logs!.PodName).IsEqualTo(podName);
        await Assert.That(logs.SelectedDetailTabIndex).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }

    [Test]
    public async Task Shift_L_and_the_icon_on_an_event_row_open_the_pods_logs_maximized()
    {
        var tab = EventsTab();
        var row = tab.Rows.First(r => r.Resource.InvolvedObject() is { Kind: "Pod" });

        await tab.OpenRowLogsAsync(row, maximized: true);

        await Assert.That(tab.SelectedRow).IsSameReferenceAs(row);
        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();

        tab.IsInspectorMaximized = false;
        await Assert.That(tab.OpenLogsMaximizedCommand.CanExecute(null)).IsTrue();
        await tab.OpenLogsMaximizedCommand.ExecuteAsync(null);
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_event_about_something_else_offers_no_logs()
    {
        var tab = EventsTab();
        tab.SelectedRow = tab.Rows.First(r => r.Resource.InvolvedObject() is { Kind: not "Pod" });

        await Assert.That(tab.OpenLogsCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.OpenLogsMaximizedCommand.CanExecute(null)).IsFalse();

        // Exec, port-forward and previous logs act on a pod row itself, never on an event.
        tab.SelectedRow = tab.Rows.First(r => r.Resource.InvolvedObject() is { Kind: "Pod" });
        await Assert.That(tab.ExecIntoSelectedCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.OpenPreviousLogsCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task An_event_whose_pod_is_gone_says_so_in_the_list()
    {
        var tab = EventsTab();
        var row = new ResourceRowViewModel(EventAbout("Pod", "v1", "long-gone-pod"));

        await tab.OpenRowLogsAsync(row, maximized: true);

        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
        await Assert.That(tab.ConnectionWarning).IsNotNull();
        await Assert.That(tab.ConnectionWarning!).Contains("long-gone-pod");
    }

    // ------------------------------------------------------------------ Argo CD

    private static (ClusterTabViewModel Tab, ArgoApplicationTabViewModel Detail) ArgoDetail(string application)
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.IsArgoDashboard));
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == application);
        tab.OpenArgoApplicationCommand.Execute(null);
        return (tab, (ArgoApplicationTabViewModel)tab.SelectedInspectorTab!);
    }

    [Test]
    public async Task Argo_offers_logs_on_pods_and_workloads_only()
    {
        var (_, detail) = ArgoDetail("checkout");

        await Assert.That(detail.Resources.Single(r => r.Kind == "Deployment").HasLogs).IsTrue();
        await Assert.That(detail.Resources.Single(r => r.Kind == "Service").HasLogs).IsFalse();
        await Assert.That(detail.Resources.Single(r => r.Kind == "ConfigMap").HasLogs).IsFalse();
        await Assert.That(detail.Resources.Single(r => r.Kind == "Ingress").HasLogs).IsFalse();
    }

    [Test]
    public async Task An_Argo_managed_workload_opens_its_pods_logs()
    {
        var (tab, detail) = ArgoDetail("fraud-detector");
        var deployment = detail.Resources.Single(r => r.Kind == "Deployment");

        await deployment.OpenLogsAsync(maximized: true);

        await Assert.That(detail.LogsNotice).IsNull();
        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<WorkloadLogsTabViewModel>();
        await Assert.That(tab.IsInspectorMaximized).IsTrue();
    }

    [Test]
    public async Task An_Argo_workload_with_nothing_behind_it_is_stated()
    {
        // ledger-api is a managed Deployment the demo dataset has no object for — the
        // demo's version of Argo reporting a resource Missing.
        var (tab, detail) = ArgoDetail("ledger-api");

        await detail.Resources.Single(r => r.Kind == "Deployment").OpenLogsAsync(maximized: false);

        await Assert.That(detail.HasLogsNotice).IsTrue();
        await Assert.That(detail.LogsNotice!).Contains("ledger-api");
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(detail);
    }

    [Test]
    public async Task An_Argo_row_offers_no_logs_when_the_pane_cannot_open_them()
    {
        var row = new ArgoResourceRowViewModel(
            new ArgoResource("apps", "v1", "Deployment", "payments", "web", ArgoSyncState.Synced, ArgoHealthState.Healthy),
            _ => Task.CompletedTask);

        await Assert.That(row.HasLogs).IsFalse();
    }

    // -------------------------------------------- the one resolver, on a real cluster

    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: [], Categories: []);

    private static NamedObjectSource Cluster(Func<string, DynamicResource?> read) => new(
        _ => Task.FromResult<IReadOnlyList<ResourceDescriptor>>([TestObjects.PodDescriptor, Deployments]),
        (_, _, name, _) => Task.FromResult(read(name)));

    private static readonly OwnerRef GonePod = new("v1", "Pod", "web-7f9c-abcde", null, false);

    [Test]
    public async Task A_pod_deleted_since_it_was_listed_is_named_as_gone()
    {
        var tab = TestObjects.Tab();

        var problem = await tab.OpenNamedLogsAsync(GonePod, "shop", "", null, Cluster(_ => null), maximized: true);

        await Assert.That(problem).IsEqualTo(
            "Pod shop/web-7f9c-abcde no longer exists — it was deleted or replaced since this was listed.");
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(0);
        await Assert.That(tab.IsInspectorMaximized).IsFalse();
    }

    [Test]
    public async Task A_refused_read_carries_the_servers_own_sentence()
    {
        var tab = TestObjects.Tab();
        const string forbidden =
            "pods \"web-7f9c-abcde\" is forbidden: User \"dev\" cannot get resource \"pods\" in the namespace \"shop\"";
        var source = new NamedObjectSource(
            _ => Task.FromResult<IReadOnlyList<ResourceDescriptor>>([TestObjects.PodDescriptor]),
            (_, _, _, _) => Task.FromException<DynamicResource?>(new InvalidOperationException(forbidden)));

        var problem = await tab.OpenNamedLogsAsync(GonePod, "shop", "", null, source, maximized: null);

        await Assert.That(problem).IsEqualTo(forbidden);
    }

    [Test]
    public async Task A_workload_that_names_no_pods_is_stated()
    {
        var tab = TestObjects.Tab();
        var empty = Parse("""
            { "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "web", "namespace": "shop" }, "spec": { "selector": {} } }
            """);

        var problem = await tab.OpenNamedLogsAsync(
            new OwnerRef("apps/v1", "Deployment", "web", null, false), "shop", "", null, Cluster(_ => empty), maximized: null);

        await Assert.That(problem).IsEqualTo("Deployment/web names no pods, so there are no logs to open.");
    }

    [Test]
    public async Task A_kind_the_cluster_does_not_serve_is_stated()
    {
        var tab = TestObjects.Tab();

        var problem = await tab.OpenNamedLogsAsync(
            new OwnerRef("batch/v1", "Job", "nightly", null, false), "shop", "", null, Cluster(_ => null), maximized: null);

        await Assert.That(problem).IsEqualTo("This cluster does not serve Job (batch/v1).");
    }

    [Test]
    public async Task A_tab_with_no_connection_says_so()
    {
        var tab = TestObjects.Tab();

        await Assert.That(await tab.OpenNamedLogsAsync(GonePod, "shop", "", null, maximized: null))
            .IsEqualTo("Not connected to this cluster.");
    }

    // ------------------------------------------------------------------ the keys

    [Test]
    public async Task Every_pod_list_reads_L_and_Shift_L_from_the_one_catalog_row()
    {
        // RowLogsGesture.MatchLogsKey is what workload detail's and node detail's grids
        // (and the resource list) ask; true is full-size, false is the split, null leaves
        // the key alone.
        await Assert.That(RowLogsGesture.MatchLogsKey(new KeyEventArgs { Key = Key.L })).IsEqualTo(false);
        await Assert.That(RowLogsGesture.MatchLogsKey(new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.Shift }))
            .IsEqualTo(true);
        await Assert.That(RowLogsGesture.MatchLogsKey(new KeyEventArgs { Key = Key.S })).IsNull();
        await Assert.That(RowLogsGesture.MatchLogsKey(new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.Control }))
            .IsNull();
    }
}
