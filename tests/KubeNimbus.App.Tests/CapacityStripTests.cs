using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-25 (#206) at the tab: the capacity strip sits above the Nodes list and nowhere else,
/// is worded from the list's own rows, and says in place what it could not read — no metrics
/// API, metrics refused, pods refused — rather than showing a zero it does not have. Driven on
/// the demo cluster through the real Connect and SelectKind, then degraded through the same
/// entry points the metrics poll and the pod read use.
/// </summary>
public class CapacityStripTests
{
    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    private static void Select(ClusterTabViewModel tab, string group, string kind) =>
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind));

    [Test]
    public async Task The_nodes_list_opens_with_counts_usage_and_requests()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");
        var strip = tab.Capacity;

        await Assert.That(strip.IsVisible).IsTrue();
        await Assert.That(strip.NodesText).StartsWith($"{DemoData.Nodes.Count} nodes · ");
        await Assert.That(strip.IsUsageStated).IsFalse();
        await Assert.That(strip.IsRequestsStated).IsFalse();
        await Assert.That(strip.CpuUsedText).Contains(" used (");
        await Assert.That(strip.CpuRequestedText).Contains(" requested (");
        await Assert.That(strip.MemoryUsedText).Contains("GiB used (");

        // The figure is the demo's own usage, summed: 0.412 + 1.93 + 0.64 cores.
        await Assert.That(Math.Abs(strip.Summary!.Cpu.Used!.Value - 2.982d)).IsLessThan(0.0001d);
        await Assert.That(strip.Summary.Cpu.Requested!.Value).IsGreaterThan(0d);
    }

    [Test]
    public async Task Any_other_kind_has_no_strip()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");
        Select(tab, "", "Pod");

        await Assert.That(tab.Capacity.IsVisible).IsFalse();
    }

    /// <summary>The central one: without metrics-server the usage half says so, and requests stay.</summary>
    [Test]
    public async Task Without_a_metrics_api_usage_says_so_and_requests_stay()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");

        tab.SetCapacityUsage(CapacityUsageState.NoMetricsApi);

        var strip = tab.Capacity;
        await Assert.That(strip.CpuUsedText).IsEqualTo(CapacityStripViewModel.UsageNoMetrics);
        await Assert.That(strip.MemoryUsedText).IsEqualTo(CapacityStripViewModel.UsageNoMetrics);
        await Assert.That(strip.IsUsageStated).IsTrue();
        await Assert.That(strip.Summary!.Cpu.Used).IsNull();
        await Assert.That(strip.CpuUsedText).DoesNotContain("0");
        await Assert.That(strip.IsRequestsStated).IsFalse();
        await Assert.That(strip.CpuRequestedText).Contains(" requested (");
    }

    [Test]
    public async Task Under_narrow_rbac_each_refused_half_says_so_with_the_servers_reason()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");
        const string podsReason = "pods is forbidden: User \"system:serviceaccount:bundle-m:viewer\" cannot list resource \"pods\" in API group \"\" at the cluster scope";

        tab.SetCapacityUsage(CapacityUsageState.Refused, "nodes.metrics.k8s.io is forbidden");
        tab.ApplyCapacityPods(null, CapacityPodsState.Refused, podsReason);

        var strip = tab.Capacity;
        await Assert.That(strip.IsVisible).IsTrue();
        await Assert.That(strip.NodesText).StartsWith($"{DemoData.Nodes.Count} nodes");
        await Assert.That(strip.CpuUsedText).IsEqualTo(CapacityStripViewModel.UsageRefused);
        await Assert.That(strip.UsageTip).Contains("nodes.metrics.k8s.io is forbidden");
        await Assert.That(strip.CpuRequestedText).IsEqualTo(CapacityStripViewModel.RequestsRefused);
        await Assert.That(strip.MemoryRequestedText).IsEqualTo(CapacityStripViewModel.RequestsRefused);
        await Assert.That(strip.RequestsTip).Contains(podsReason);
        await Assert.That(strip.Summary!.Cpu.Requested).IsNull();
    }

    /// <summary>
    /// The 0.7.0 QA finding M-4: a poll whose node-usage read was refused hands the strip an
    /// empty sample set, and that empty set is not a measurement. The strip used to take it as
    /// one and say "no node has reported usage yet" under a 403, with metrics-server's
    /// "answered with no node in it" as the reason; it must say "usage not permitted" with
    /// the server's sentence, through the same entry point the poll uses.
    /// </summary>
    [Test]
    public async Task A_refused_poll_says_usage_not_permitted_not_that_no_node_reported()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");
        const string reason = "nodes.metrics.k8s.io is forbidden: User \"system:serviceaccount:kn-live:viewer\" cannot list resource \"nodes\" in API group \"metrics.k8s.io\" at the cluster scope";

        tab.ApplyUsage([], refused: reason);

        var strip = tab.Capacity;
        await Assert.That(strip.CpuUsedText).IsEqualTo(CapacityStripViewModel.UsageRefused);
        await Assert.That(strip.MemoryUsedText).IsEqualTo(CapacityStripViewModel.UsageRefused);
        await Assert.That(strip.IsUsageStated).IsTrue();
        await Assert.That(strip.UsageTip).Contains(reason);
        await Assert.That(strip.Summary!.Cpu.Used).IsNull();

        // The refusal is this poll's answer, not a latch: a later poll that reads (the user was
        // granted access) is worded from what it read.
        tab.ApplyUsage([]);
        await Assert.That(strip.CpuUsedText).IsEqualTo(CapacityStripViewModel.UsageNoneYet);
    }

    [Test]
    public async Task A_failed_re_read_keeps_the_last_good_requests()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");
        var before = tab.Capacity.CpuRequestedText;

        tab.ApplyCapacityPods(null, CapacityPodsState.Failed, "connection reset");

        await Assert.That(tab.Capacity.CpuRequestedText).IsEqualTo(before);
        await Assert.That(tab.Capacity.IsRequestsStated).IsFalse();
    }

    [Test]
    public async Task While_waiting_each_half_says_it_is_reading()
    {
        var strip = new CapacityStripViewModel();

        strip.Update(DemoData.Nodes, _ => null, CapacityUsageState.Reading, null, null, CapacityPodsState.Reading, null);

        await Assert.That(strip.CpuUsedText).IsEqualTo(CapacityStripViewModel.UsageReading);
        await Assert.That(strip.CpuRequestedText).IsEqualTo(CapacityStripViewModel.RequestsReading);
        await Assert.That(strip.IsVisible).IsTrue();
    }

    [Test]
    public async Task An_empty_node_list_has_no_strip()
    {
        var strip = new CapacityStripViewModel();

        strip.Update([], _ => null, CapacityUsageState.Available, null, [], CapacityPodsState.Read, null);

        await Assert.That(strip.IsVisible).IsFalse();
    }
}
