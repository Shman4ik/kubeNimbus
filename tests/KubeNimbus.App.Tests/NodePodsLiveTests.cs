using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-24: node detail's Pods tab is a field-selected watch now, not a one-shot list with
/// a Refresh. These drive the pane's own frame handler (<c>ApplyPodEvent</c>, the entry
/// point the watch and the demo replay share) on a real demo node pane.
/// </summary>
public class NodePodsLiveTests
{
    private static NodeDetailTabViewModel OpenWorker1()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" }));
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        tab.OpenSelectedCommand.Execute(null);
        return (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
    }

    private static DynamicResource NodePod(string ns, string name, string cpu = "250m", string phase = "Running")
    {
        using var document = JsonDocument.Parse("""
            {"apiVersion":"v1","kind":"Pod",
             "metadata":{"name":"NAME","namespace":"NS","uid":"NS-NAME",
               "ownerReferences":[{"apiVersion":"apps/v1","kind":"ReplicaSet","name":"rs","uid":"rs","controller":true}]},
             "spec":{"nodeName":"demo-worker-1","containers":[{"name":"app","resources":{"requests":{"cpu":"CPU"}}}]},
             "status":{"phase":"PHASE"}}
            """.Replace("NAME", name).Replace("NS", ns).Replace("CPU", cpu).Replace("PHASE", phase));
        return new DynamicResource(document.RootElement.Clone());
    }

    private static double RequestedCpu(NodeDetailTabViewModel detail) =>
        detail.ResourceLines.Single(l => l.Label == "CPU").Line.Requested;

    [Test]
    public async Task A_pod_scheduled_onto_the_node_appears_in_order_and_is_counted()
    {
        var detail = OpenWorker1();
        var before = detail.Pods.Count;
        var cpuBefore = RequestedCpu(detail);

        detail.ApplyPodEvent(TestObjects.Added(NodePod("aaa-first", "newcomer")));

        await Assert.That(detail.Pods.Count).IsEqualTo(before + 1);
        await Assert.That(detail.Pods[0].Name).IsEqualTo("newcomer");
        await Assert.That(RequestedCpu(detail) - cpuBefore).IsEqualTo(0.25).Within(0.0001);
        await Assert.That(detail.PodsCaption).IsEqualTo($"{before + 1} pods on this node");
    }

    [Test]
    public async Task An_evicted_pod_leaves_the_list_and_the_arithmetic()
    {
        var detail = OpenWorker1();
        detail.ApplyPodEvent(TestObjects.Added(NodePod("payments", "evict-me", cpu: "500m")));
        var cpuWith = RequestedCpu(detail);
        var count = detail.Pods.Count;
        detail.SelectedPod = detail.Pods.Single(p => p.Name == "evict-me");

        detail.ApplyPodEvent(TestObjects.Deleted(NodePod("payments", "evict-me", cpu: "500m")));

        await Assert.That(detail.Pods.Count).IsEqualTo(count - 1);
        await Assert.That(detail.Pods.Any(p => p.Name == "evict-me")).IsFalse();
        await Assert.That(cpuWith - RequestedCpu(detail)).IsEqualTo(0.5).Within(0.0001);

        // A selection on a pod that has gone is not left pointing at nothing.
        await Assert.That(detail.SelectedPod).IsNull();
    }

    /// <summary>
    /// A Modified updates the row in place — the same object, so the grid keeps the
    /// reader's selection while the pod's status changes under it.
    /// </summary>
    [Test]
    public async Task A_status_change_updates_the_row_in_place_and_keeps_the_selection()
    {
        var detail = OpenWorker1();
        detail.ApplyPodEvent(TestObjects.Added(NodePod("payments", "flaky")));
        var row = detail.Pods.Single(p => p.Name == "flaky");
        detail.SelectedPod = row;

        detail.ApplyPodEvent(TestObjects.Modified(NodePod("payments", "flaky", phase: "Failed")));

        await Assert.That(detail.Pods.Single(p => p.Name == "flaky")).IsSameReferenceAs(row);
        await Assert.That(detail.SelectedPod).IsSameReferenceAs(row);
        await Assert.That(row.Status).IsEqualTo("Failed");
        await Assert.That(row.StatusHealth).IsEqualTo(ResourceHealth.Error);
    }

    /// <summary>
    /// A relist (the first, or one after a 410) is a load: until it is Synced the pane
    /// says it is still reading and withholds the requested figures, rather than printing
    /// a half-read node as a half-empty one (UI rule 18).
    /// </summary>
    [Test]
    public async Task A_relist_reads_as_loading_until_synced()
    {
        var detail = OpenWorker1();

        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Reset);
        detail.ApplyPodEvent(TestObjects.Added(NodePod("payments", "only-one")));

        await Assert.That(detail.IsLoadingPods).IsTrue();
        await Assert.That(detail.HasCountedPods).IsFalse();
        await Assert.That(detail.PodsCaption).IsEqualTo("Reading the pods on this node…");

        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(detail.IsLoadingPods).IsFalse();
        await Assert.That(detail.HasCountedPods).IsTrue();
        await Assert.That(detail.PodsCaption).IsEqualTo("1 pod on this node");
        await Assert.That(RequestedCpu(detail)).IsEqualTo(0.25).Within(0.0001);
    }
}
