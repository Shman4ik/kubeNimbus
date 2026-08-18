using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The App-layer half of the node actions: which of cordon / uncordon / drain the
/// selected row offers, that the shared confirm strip cannot be re-armed over a running
/// drain, and that the demo cluster arms and refuses in place rather than hiding the
/// action or silently doing nothing.
///
/// <para>
/// It drives the real demo tab — <c>ConnectCommand</c> on <see cref="ClusterContext.Demo"/>,
/// the real <c>SelectKindCommand</c>, the real commands — because the thing being pinned
/// is the wiring, and a test over hand-built view models would pin a second copy of it.
/// No Avalonia application is started, for the same reason the row-filter tests start
/// none: none of this path touches the dispatcher (the demo tab has no client, so no
/// watch and no poll ever start).
/// </para>
/// </summary>
public class NodeActionTests
{
    /// <summary>A demo tab with the Node kind selected — the demo cluster ships three nodes.</summary>
    private static ClusterTabViewModel NodeTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" });
        tab.SelectKindCommand.Execute(kind);
        return tab;
    }

    private static ResourceRowViewModel Row(ClusterTabViewModel tab, string name) =>
        tab.Rows.First(r => r.Name == name);

    [Test]
    public async Task The_demo_cluster_lists_its_nodes()
    {
        var tab = NodeTab();

        await Assert.That(tab.RowNames()).IsEqualTo("demo-cp-1, demo-worker-1, demo-worker-2");
    }

    /// <summary>
    /// kubectl's own node status word, which <c>ResourceStatusSummary</c> already
    /// produced before this item and which the whole surface hangs off.
    /// </summary>
    [Test]
    public async Task A_cordoned_node_reads_as_scheduling_disabled()
    {
        var tab = NodeTab();

        await Assert.That(Row(tab, "demo-worker-1").Status).IsEqualTo("Ready");
        await Assert.That(Row(tab, "demo-worker-2").Status).IsEqualTo("Ready,SchedulingDisabled");
    }

    /// <summary>
    /// Cordon and uncordon are one slot, and which of the two it is comes from the
    /// node's own <c>spec.unschedulable</c> — never both at once, so the menu can show
    /// whichever applies rather than a live item beside a dead one (UI rule 11).
    /// </summary>
    [Test]
    public async Task Cordon_and_uncordon_are_offered_by_the_nodes_current_state()
    {
        var tab = NodeTab();

        tab.SelectedRow = Row(tab, "demo-worker-1");
        await Assert.That(tab.CanCordonSelectedRow).IsTrue();
        await Assert.That(tab.CanUncordonSelectedRow).IsFalse();

        tab.SelectedRow = Row(tab, "demo-worker-2");
        await Assert.That(tab.CanCordonSelectedRow).IsFalse();
        await Assert.That(tab.CanUncordonSelectedRow).IsTrue();
    }

    /// <summary>
    /// Drain is offered because the demo catalog's Pod descriptor declares
    /// <c>eviction</c>, exactly as a real server's discovery does — capability from
    /// discovery, not from a list of kinds.
    /// </summary>
    [Test]
    public async Task Drain_is_offered_when_the_cluster_serves_pods_eviction()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");

        await Assert.That(tab.CanDrainSelectedRow).IsTrue();
    }

    /// <summary>None of the three is offered on anything that is not a node.</summary>
    [Test]
    public async Task No_node_action_is_offered_for_a_pod_row()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedRow = tab.Rows.FirstOrDefault();

        await Assert.That(tab.SelectedRow).IsNotNull();
        await Assert.That(tab.CanCordonSelectedRow).IsFalse();
        await Assert.That(tab.CanUncordonSelectedRow).IsFalse();
        await Assert.That(tab.CanDrainSelectedRow).IsFalse();
    }

    /// <summary>
    /// Arming names the object and changes nothing yet (UI rule 17). On the demo cluster
    /// the confirm is dead and the strip says why — never a silent no-op.
    /// </summary>
    [Test]
    public async Task Cordon_arms_the_shared_strip_and_refuses_in_place_on_the_demo_cluster()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");

        tab.CordonSelectedCommand.Execute(null);

        var action = tab.PendingRowAction;
        await Assert.That(action).IsNotNull();
        await Assert.That(action!.Kind).IsEqualTo(RowActionKind.Cordon);
        await Assert.That(action.Target).Contains("Node/demo-worker-1");
        await Assert.That(action.IsDemo).IsTrue();
        await Assert.That(action.ConfirmCommand.CanExecute(null)).IsFalse();
    }

    /// <summary>
    /// The drain's confirm sentence has to carry the lifetime warning: the eviction loop
    /// runs in this process, so closing the tab or quitting stops it partway. That is the
    /// one thing someone must know before starting one, and it is why it is in the
    /// confirm rather than in a tooltip.
    /// </summary>
    [Test]
    public async Task The_drain_confirm_says_that_quitting_stops_it_partway()
    {
        var action = new RowActionViewModel(
            RowActionKind.Drain, client: null, TestObjects.NodeDescriptor, null, "demo-worker-1");

        await Assert.That(action.Question).Contains("runs inside kubeNimbus");
        await Assert.That(action.Question).Contains("stops it partway");
        await Assert.That(action.Question).Contains("cordoned");
    }

    /// <summary>
    /// The demo cluster plans for real — the classification is pure and the dataset has
    /// pods on nodes, so the refusals render offline exactly as they would against a
    /// cluster (demo rule 4). Only the eviction itself is unavailable.
    /// </summary>
    [Test]
    public async Task A_demo_drain_computes_a_real_plan_including_its_refusals()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");

        await tab.DrainSelectedCommand.ExecuteAsync(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.Kind).IsEqualTo(RowActionKind.Drain);
        await Assert.That(action.DrainPlan).IsNotNull();

        // demo-worker-1 carries: two report-generator replicas and checkout-worker
        // (evictable), a finished migration Job pod and a kube-proxy DaemonSet pod
        // (left in place), an unmanaged pod and one with an emptyDir (both refused).
        await Assert.That(action.DrainPlan!.EvictCount).IsEqualTo(3);
        await Assert.That(action.DrainPlan.SkippedCount).IsEqualTo(2);
        await Assert.That(action.IsDrainBlocked).IsTrue();
        await Assert.That(action.DrainBlockers.Count).IsEqualTo(2);
        await Assert.That(string.Join(" | ", action.DrainBlockers)).Contains("legacy-batch-runner");
        await Assert.That(string.Join(" | ", action.DrainBlockers)).Contains("emptyDir");
    }

    /// <summary>
    /// Ticking an option re-plans from the pods already read, so the refusal it clears
    /// disappears in front of you rather than on confirm — the plan is what is being
    /// agreed to, and agreeing to one you cannot see is not a confirm.
    /// </summary>
    [Test]
    public async Task Ticking_an_option_re_plans_immediately()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");
        await tab.DrainSelectedCommand.ExecuteAsync(null);

        var action = tab.PendingRowAction!;
        action.DrainForce = true;
        await Assert.That(action.DrainBlockers.Count).IsEqualTo(1);
        await Assert.That(action.DrainPlan!.EvictCount).IsEqualTo(4);

        action.DrainDeleteEmptyDirData = true;
        await Assert.That(action.IsDrainBlocked).IsFalse();
        await Assert.That(action.DrainPlan!.EvictCount).IsEqualTo(5);
    }

    /// <summary>
    /// One armed action at a time, and a running one is never replaced. It matters most
    /// for a drain, whose eviction loop lives in the strip: re-arming over it would leave
    /// the loop running with nothing on screen reporting it.
    /// </summary>
    [Test]
    public async Task A_busy_action_is_not_replaced_by_a_newly_armed_one()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");
        tab.CordonSelectedCommand.Execute(null);

        var first = tab.PendingRowAction!;
        first.IsBusy = true;

        tab.SelectedRow = Row(tab, "demo-worker-2");
        tab.UncordonSelectedCommand.Execute(null);

        await Assert.That(tab.PendingRowAction).IsSameReferenceAs(first);
    }

    /// <summary>
    /// A strip cannot be dismissed out from under a running drain: Cancel is replaced by
    /// Stop, and the command itself refuses so nothing reachable can orphan the loop.
    /// </summary>
    [Test]
    public async Task A_running_drain_cannot_be_dismissed()
    {
        var dismissed = false;
        var action = new RowActionViewModel(
            RowActionKind.Drain, client: null, TestObjects.NodeDescriptor, null, "demo-worker-1")
        {
            IsDraining = true,
        };
        action.Dismissed = () => dismissed = true;

        await Assert.That(action.CanDismiss).IsFalse();
        action.DismissCommand.Execute(null);
        await Assert.That(dismissed).IsFalse();

        action.IsDraining = false;
        action.DismissCommand.Execute(null);
        await Assert.That(dismissed).IsTrue();
    }

    /// <summary>
    /// Double-click on a node opens its detail pane, not its manifest (UI rule 2) — the
    /// conditions, taints and headroom are what the gesture is for.
    /// </summary>
    [Test]
    public async Task Opening_a_node_row_opens_the_node_detail_tab()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");

        await tab.OpenSelectedCommand.ExecuteAsync(null);

        var opened = tab.SelectedInspectorTab;
        await Assert.That(opened).IsTypeOf<NodeDetailTabViewModel>();
        await Assert.That(opened!.Title).IsEqualTo("Node/demo-worker-1");
    }

    /// <summary>
    /// The detail pane's own numbers, from the demo dataset through the production
    /// arithmetic: the pods on the node, and how much of it they have been promised.
    /// </summary>
    [Test]
    public async Task Node_detail_counts_the_pods_on_the_node_and_what_they_requested()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-1");
        await tab.OpenSelectedCommand.ExecuteAsync(null);

        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;

        // Seven pods are on demo-worker-1 in the dataset.
        await Assert.That(detail.Pods.Count).IsEqualTo(7);
        await Assert.That(detail.HasCountedPods).IsTrue();
        await Assert.That(detail.Conditions.Count).IsEqualTo(4);
        await Assert.That(detail.HasNoTaints).IsTrue();

        var cpu = detail.ResourceLines.First(l => l.Label == "CPU");
        // The finished migration Job pod is excluded, so this is six pods' requests.
        await Assert.That(cpu.Line.Requested).IsGreaterThan(0d);
        await Assert.That(cpu.Line.RequestedPercent).IsNotNull();
    }

    /// <summary>A cordoned node's pane carries both halves of "nothing lands here": the flag and the taint.</summary>
    [Test]
    public async Task Node_detail_shows_the_cordon_and_the_taint_it_comes_with()
    {
        var tab = NodeTab();
        tab.SelectedRow = Row(tab, "demo-worker-2");
        await tab.OpenSelectedCommand.ExecuteAsync(null);

        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;

        await Assert.That(detail.IsCordoned).IsTrue();
        await Assert.That(detail.StatusText).IsEqualTo("Ready,SchedulingDisabled");
        await Assert.That(detail.Taints.Select(t => t.Key)).Contains("node.kubernetes.io/unschedulable");
        await Assert.That(detail.Conditions.Single(c => c.Type == "DiskPressure").Health)
            .IsEqualTo(ResourceHealth.Error);
    }
}
