using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #268: every Conditions card (pod, node, workload) shows how long each condition has held,
/// as an age in the list's Age format with the exact instant on the tooltip, and nothing when
/// the server sent no <c>lastTransitionTime</c>.
/// </summary>
public class ConditionSinceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 19, 1, 54, TimeSpan.Zero);
    private static readonly DateTimeOffset SixDaysAgo = Now.AddDays(-6);

    [Test]
    public async Task A_pod_condition_shows_its_age_and_the_instant()
    {
        var condition = new PodCondition("Ready", "False", "ContainersNotReady", "containers with unready status: [app]",
            SixDaysAgo, PodConditionPolarity.Positive);

        var row = new PodConditionViewModel(condition, Now);

        await Assert.That(row.Age).IsEqualTo("6d");
        await Assert.That(row.AgeTooltip).IsEqualTo(
            $"Last transition {SixDaysAgo.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    [Test]
    public async Task A_condition_without_a_transition_time_shows_none()
    {
        var condition = new PodCondition("PodScheduled", "True", "", "", null, PodConditionPolarity.Positive);

        var row = new PodConditionViewModel(condition, Now);

        await Assert.That(row.Age).IsEqualTo("");
        await Assert.That(row.AgeTooltip).IsNull();
    }

    [Test]
    public async Task A_node_condition_shows_its_age()
    {
        var row = new NodeConditionViewModel(new NodeCondition("Ready", "True", "KubeletReady", "", Now.AddSeconds(-20)), Now);

        await Assert.That(row.Age).IsEqualTo("20s");
        await Assert.That(row.AgeTooltip).StartsWith("Last transition ");
    }

    /// <summary>The workload pane reads the field from the object's own status.</summary>
    [Test]
    public async Task A_workload_pane_reads_each_conditions_transition_time()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" }));
        tab.SelectedRow = tab.Rows.First();
        tab.OpenSelectedCommand.Execute(null);
        var detail = (WorkloadDetailTabViewModel)tab.SelectedInspectorTab!;

        await Assert.That(detail.Conditions.Count).IsGreaterThan(0);
        foreach (var condition in detail.Conditions)
        {
            await Assert.That(condition.LastTransition).IsNotNull();
            await Assert.That(condition.Age).IsNotEqualTo("");
            await Assert.That(condition.AgeTooltip).StartsWith("Last transition ");
        }
    }
}
