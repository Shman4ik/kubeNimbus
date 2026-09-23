using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Node detail's System card, Events tab and Usage tab, driven through the real demo tab:
/// the node is opened the way a double-click opens it, so the wiring is what is pinned,
/// not a hand-built pane. Nothing here starts a poll — the demo tab has no client.
/// </summary>
public class NodeDetailTests
{
    private static NodeDetailTabViewModel Open(string name)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);

        return (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
    }

    private static string? Value(NodeDetailTabViewModel detail, string label) =>
        detail.SystemRows.FirstOrDefault(r => r.Label == label)?.Value;

    // ---------------------------------------------------------------------- system

    [Test]
    public async Task The_system_card_reads_the_machine_from_the_node_object()
    {
        var detail = Open("demo-worker-1");

        await Assert.That(Value(detail, "Kubelet")).IsEqualTo("v1.31.2");
        await Assert.That(Value(detail, "Platform")).IsEqualTo("linux/amd64");
        await Assert.That(Value(detail, "InternalIP")).IsEqualTo("10.0.1.21");
        await Assert.That(Value(detail, "Hostname")).IsEqualTo("demo-worker-1");
        await Assert.That(Value(detail, "Pod CIDR")).IsEqualTo("10.42.1.0/24");
        await Assert.That(Value(detail, "Zone")).IsEqualTo("eu-west-1b (eu-west-1)");
        await Assert.That(Value(detail, "Instance type")).IsEqualTo("n2-standard-8");
        await Assert.That(Value(detail, "Created")).StartsWith("2026-05-02 09:14 UTC · ");
    }

    /// <summary>
    /// A field the node did not report has no row. A label beside an empty value reads
    /// as a field that failed to load, which is the opposite of what it means.
    /// </summary>
    [Test]
    public async Task A_field_the_node_did_not_report_has_no_row()
    {
        var detail = Open("demo-worker-1");

        await Assert.That(Value(detail, "Provider ID")).IsNull();
        await Assert.That(Value(detail, "ExternalIP")).IsNull();
        await Assert.That(detail.SystemRows.All(r => r.Value.Length > 0)).IsTrue();
    }

    /// <summary>A dual-stack node reports two InternalIPs and two pod ranges; each reads as one line.</summary>
    [Test]
    public async Task Dual_stack_addresses_and_ranges_share_one_line_each()
    {
        var info = new NodeInfo("v1.31.2", "", "", "", "arm64", "10.0.0.5")
        {
            Addresses = [new("InternalIP", "10.0.0.5"), new("InternalIP", "fd00::5"), new("ExternalIP", "203.0.113.7")],
            PodCidrs = ["10.244.1.0/24", "fd00:10:244:1::/64"],
            Region = "us-east-1",
        };

        var rows = NodeDetailTabViewModel.BuildSystemRows(info, DateTimeOffset.UtcNow);

        await Assert.That(rows.Count(r => r.Label == "InternalIP")).IsEqualTo(1);
        await Assert.That(rows.First(r => r.Label == "InternalIP").Value).IsEqualTo("10.0.0.5, fd00::5");
        await Assert.That(rows.First(r => r.Label == "Pod CIDRs").Value).IsEqualTo("10.244.1.0/24, fd00:10:244:1::/64");
        await Assert.That(rows.First(r => r.Label == "Platform").Value).IsEqualTo("arm64");
        // No zone to hang it off, so the region gets a line of its own.
        await Assert.That(rows.First(r => r.Label == "Region").Value).IsEqualTo("us-east-1");
        await Assert.That(rows.Any(r => r.Label == "Created")).IsFalse();
    }

    // ---------------------------------------------------------------------- events

    [Test]
    public async Task The_events_tab_holds_only_this_nodes_events_newest_first()
    {
        var detail = Open("demo-worker-2");

        await Assert.That(detail.Events.Select(e => e.Reason).ToArray()).IsEquivalentTo(
            new[] { "EvictionThresholdMet", "NodeHasDiskPressure", "FreeDiskSpaceFailed", "NodeNotSchedulable" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(detail.EventsCaption).IsEqualTo("4 events");
        await Assert.That(detail.HasNoEvents).IsFalse();
    }

    /// <summary>
    /// The kubelet records node events with the node's <em>name</em> as the UID and the
    /// node controller with the real one. Both have to land on the tab — which is why the
    /// live selector matches kind and name rather than UID.
    /// </summary>
    [Test]
    public async Task Kubelet_and_node_controller_events_both_reach_the_tab()
    {
        var detail = Open("demo-worker-1");

        await Assert.That(detail.Events.Select(e => e.Reason).ToArray()).Contains("RegisteredNode");
        await Assert.That(detail.Events.Select(e => e.Reason).ToArray()).Contains("Starting");
    }

    [Test]
    public async Task A_node_with_no_events_says_so()
    {
        var detail = Open("demo-cp-1");

        await Assert.That(detail.Events.Count).IsEqualTo(0);
        await Assert.That(detail.HasNoEvents).IsTrue();
        await Assert.That(detail.EventsCaption).IsEqualTo("No recent events");
    }

    /// <summary>The dataset's node events belong to node detail, not to every pod's feed.</summary>
    [Test]
    public async Task Pod_detail_on_the_demo_cluster_does_not_show_node_events()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedRow = tab.Rows.First();
        tab.OpenSelectedCommand.Execute(null);

        var pod = tab.InspectorTabs.OfType<PodDetailTabViewModel>().First();

        await Assert.That(pod.Events.Count).IsGreaterThan(0);
        await Assert.That(pod.Events.Any(e => e.InvolvedObject?.Kind == "Node")).IsFalse();
    }

    // ----------------------------------------------------------------------- usage

    /// <summary>
    /// The pane starts from the history the list row already holds, so a node that has
    /// been on screen for a while opens with a chart rather than "collecting".
    /// </summary>
    [Test]
    public async Task Usage_opens_with_the_rows_history_and_the_datasets_latest_reading()
    {
        var detail = Open("demo-worker-1");
        var expected = DemoData.NodeUsage.First(n => n.Name == "demo-worker-1");

        await Assert.That(detail.HasUsageSamples).IsTrue();
        await Assert.That(detail.IsCollectingUsage).IsFalse();
        await Assert.That(detail.History.Count).IsEqualTo(DemoUsage.SampleCount);
        await Assert.That(detail.CpuText).IsEqualTo(Quantity.FormatCpu(expected.CpuNanocores));
        await Assert.That(detail.MemoryText).IsEqualTo(Quantity.FormatMemory(expected.MemoryBytes));
        await Assert.That(detail.CpuShareText).EndsWith("% of allocatable)");
    }

    /// <summary>A node that stops reporting is a gap in the line, never a drop to zero.</summary>
    [Test]
    public async Task A_missed_reading_is_a_gap_not_a_zero()
    {
        var detail = Open("demo-worker-1");

        detail.ApplyMetrics(null, null);

        await Assert.That(detail.CpuSeries[^1]).IsNull();
        await Assert.That(detail.CpuText).IsEqualTo("—");
        await Assert.That(detail.CpuShareText).IsEqualTo("");
        await Assert.That(detail.HasUsageSamples).IsTrue();
    }

    [Test]
    public async Task Usage_is_a_share_of_allocatable_and_silent_without_one()
    {
        await Assert.That(NodeDetailTabViewModel.Share(1.5, 6)).IsEqualTo(" (25% of allocatable)");
        await Assert.That(NodeDetailTabViewModel.Share(1.5, null)).IsEqualTo("");
        await Assert.That(NodeDetailTabViewModel.Share(null, 6)).IsEqualTo("");
    }
}
