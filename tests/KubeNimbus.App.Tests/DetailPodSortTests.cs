using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The header sort of workload detail's and node detail's Pods grids (<see cref="GridSort{T}"/>):
/// the three states, comparison by meaning, the sort kept through watch events, and the
/// selection kept through a move. Driven through each pane's own watch-frame handler.
/// </summary>
public class DetailPodSortTests
{
    private static WorkloadDetailTabViewModel Workload()
    {
        TestObjects.RedirectStores();
        using var doc = JsonDocument.Parse("""
            {"kind":"Deployment","apiVersion":"apps/v1","metadata":{"name":"web","namespace":"payments"},
            "spec":{"replicas":3,"selector":{"matchLabels":{"app":"not-in-demo"}},"template":{"spec":{"containers":[]}}},
            "status":{"readyReplicas":3}}
            """);
        var descriptor = new ResourceDescriptor("apps", "v1", "Deployment", "deployments", "deployment", true, [], []);
        var row = new ResourceRowViewModel(new DynamicResource(doc.RootElement.Clone()), "");
        return new WorkloadDetailTabViewModel(null, descriptor, row, _ => { },
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
    }

    private static string Names(IEnumerable<ResourceRowViewModel> pods) => string.Join(", ", pods.Select(p => p.Name));

    private static string Names(IEnumerable<NodePodViewModel> pods) => string.Join(", ", pods.Select(p => p.Name));

    [Test]
    public async Task Workload_pods_sort_by_restarts_and_back_to_name_order()
    {
        var detail = Workload();
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-c", restarts: 2)));
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-a", restarts: 10)));
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-b", restarts: 9)));
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-a, web-b, web-c");

        detail.PodSort.Toggle(ResourceColumn.Restarts);
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-c, web-b, web-a");
        await Assert.That(detail.PodSort.Header(ResourceColumn.Restarts, "Restarts")).IsEqualTo("Restarts ↑");

        detail.PodSort.Toggle(ResourceColumn.Restarts);
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-a, web-b, web-c");

        detail.PodSort.Toggle(ResourceColumn.Restarts);
        await Assert.That(detail.PodSort.Column).IsNull();
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-a, web-b, web-c");
    }

    /// <summary>
    /// The sort is maintained: a new pod is placed where the order puts it, and a pod whose
    /// restarts climb moves — as the same row, still selected.
    /// </summary>
    [Test]
    public async Task Workload_pods_stay_sorted_through_watch_events_and_keep_the_selection()
    {
        var detail = Workload();
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-a", restarts: 5)));
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-b", restarts: 1)));
        detail.PodSort.Toggle(ResourceColumn.Restarts);
        detail.PodSort.Toggle(ResourceColumn.Restarts);
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-a, web-b");

        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-c", restarts: 3)));
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-a, web-c, web-b");

        var b = detail.Pods.Single(p => p.Name == "web-b");
        detail.SelectedPod = b;
        detail.ApplyPod(new(ResourceEventType.Modified, TestObjects.Pod("payments", "web-b", restarts: 40)));
        await Assert.That(Names(detail.Pods)).IsEqualTo("web-b, web-a, web-c");
        await Assert.That(detail.Pods[0]).IsSameReferenceAs(b);
        await Assert.That(detail.SelectedPod).IsSameReferenceAs(b);
    }

    private static NodeDetailTabViewModel Node()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        tab.OpenSelectedCommand.Execute(null);
        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Reset);
        return detail;
    }

    private static DynamicResource NodePod(string ns, string name, string cpu, string memory = "64Mi", string created = "2026-08-01T10:00:00Z")
    {
        using var document = JsonDocument.Parse("""
            {"apiVersion":"v1","kind":"Pod",
             "metadata":{"name":"NAME","namespace":"NS","uid":"NS-NAME","creationTimestamp":"CREATED"},
             "spec":{"nodeName":"demo-worker-1","containers":[{"name":"app","resources":{"requests":{"cpu":"CPU","memory":"MEM"}}}]},
             "status":{"phase":"Running"}}
            """.Replace("NAME", name).Replace("NS", ns).Replace("CPU", cpu).Replace("MEM", memory).Replace("CREATED", created));
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>
    /// CPU req compares the requests as numbers: "1" is more than "250m", which text would
    /// put the other way round. Age puts the youngest first. The default is namespace/name.
    /// </summary>
    [Test]
    public async Task Node_pods_sort_by_requests_as_numbers_and_by_age_youngest_first()
    {
        var detail = Node();
        detail.ApplyPodEvent(TestObjects.Added(NodePod("b", "small", cpu: "250m", created: "2026-08-01T10:00:00Z")));
        detail.ApplyPodEvent(TestObjects.Added(NodePod("a", "big", cpu: "1", created: "2026-08-03T10:00:00Z")));
        detail.ApplyPodEvent(TestObjects.Added(NodePod("c", "mid", cpu: "500m", created: "2026-08-02T10:00:00Z")));
        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(Names(detail.Pods)).IsEqualTo("big, small, mid");

        detail.PodSort.Toggle(NodePodComparer.CpuRequest);
        detail.PodSort.Toggle(NodePodComparer.CpuRequest);
        await Assert.That(Names(detail.Pods)).IsEqualTo("big, mid, small");

        detail.PodSort.Toggle(NodePodComparer.Age);
        await Assert.That(Names(detail.Pods)).IsEqualTo("big, mid, small");
        detail.PodSort.Toggle(NodePodComparer.Age);
        await Assert.That(Names(detail.Pods)).IsEqualTo("small, mid, big");

        // A pod scheduled while sorted lands where the sort puts it.
        detail.ApplyPodEvent(TestObjects.Added(NodePod("d", "newest", cpu: "100m", created: "2026-08-04T10:00:00Z")));
        await Assert.That(detail.Pods.Last().Name).IsEqualTo("newest");

        detail.PodSort.Toggle(NodePodComparer.Age);
        await Assert.That(Names(detail.Pods)).IsEqualTo("big, small, mid, newest");
    }
}
