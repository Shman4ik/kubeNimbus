using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Security block 3, B3-1: a delete on a production cluster always confirms, whatever
/// "Confirm before deleting" says, in both delete paths (the list's strip and the YAML
/// editor); a fleet row is judged by its own cluster's environment; and every armed strip
/// names the cluster it lands on.
///
/// <para>
/// The tabs here are given an offline client (<see cref="TestObjects.OfflineClient"/>), not
/// the demo cluster: a demo strip refuses every confirm before the decision under test is
/// reached, so "deleted at once" and "armed" would look the same. With a client, a delete that
/// runs is a request to 127.0.0.1:1 that fails — a delete that went out — and an armed one
/// sends nothing.
/// </para>
/// <para>
/// <c>[NotInParallel]</c> because the preference is written to <c>settings.json</c> and read
/// back at the press, and the store override is process-wide (ENG-37).
/// </para>
/// </summary>
[NotInParallel]
public class MutatingActionSafetyTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static void SetConfirmDeletes(bool value) => App.Update(s => s with { ConfirmDeletes = value });

    /// <summary>A pod tab on <c>test-cluster</c> with one row selected and a client to act through.</summary>
    private static ClusterTabViewModel PodTab(ClusterEnvironment environment, out ResourceRowViewModel row)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
        };
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "checkout-7f9c")));
        tab.Client = TestObjects.OfflineClient();
        tab.Environment = environment;
        row = tab.Rows.Single();
        tab.SelectedRow = row;
        return tab;
    }

    /// <summary>Waits for a delete that was sent to come back (refused: nothing listens).</summary>
    private static async Task<RowActionViewModel> SettledAsync(RowActionViewModel action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (action.IsBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return action;
    }

    // ---------------------------------------------------------------- the list's strip

    [Test]
    public async Task A_production_delete_arms_the_strip_with_the_preference_off()
    {
        SetConfirmDeletes(false);
        var tab = PodTab(ClusterEnvironment.Production, out _);

        tab.DeleteSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.Kind).IsEqualTo(RowActionKind.Delete);
        await Assert.That(action.IsProduction).IsTrue();
        await Assert.That(action.IsBusy).IsFalse();
        await Assert.That(action.Message).IsNull();
        await Assert.That(action.IsPromptVisible).IsTrue();
    }

    /// <summary>The preference keeps working where it is allowed to: off a production cluster, off means off.</summary>
    [Test]
    [Arguments(ClusterEnvironment.Unknown)]
    [Arguments(ClusterEnvironment.Development)]
    [Arguments(ClusterEnvironment.Staging)]
    public async Task A_non_production_delete_runs_at_once_with_the_preference_off(ClusterEnvironment environment)
    {
        SetConfirmDeletes(false);
        var tab = PodTab(environment, out _);

        tab.DeleteSelectedCommand.Execute(null);

        var action = await SettledAsync(tab.PendingRowAction!);
        await Assert.That(action.IsError).IsTrue();
        await Assert.That(action.Message).StartsWith("Delete failed:");
    }

    [Test]
    public async Task With_the_preference_on_every_delete_asks()
    {
        SetConfirmDeletes(true);
        var tab = PodTab(ClusterEnvironment.Development, out _);

        tab.DeleteSelectedCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Message).IsNull();
        await Assert.That(tab.PendingRowAction.IsBusy).IsFalse();
    }

    /// <summary>
    /// In an aggregated list the row's own cluster decides, both ways round: a production
    /// member's row asks from a development tab, and a development member's row does not ask
    /// from a production tab.
    /// </summary>
    [Test]
    [Arguments(ClusterEnvironment.Development, "prod-eu", true)]
    [Arguments(ClusterEnvironment.Production, "dev-eu", false)]
    public async Task A_fleet_row_is_judged_by_its_own_clusters_environment(
        ClusterEnvironment tabEnvironment, string rowCluster, bool expectArmed)
    {
        SetConfirmDeletes(false);
        var offline = TestObjects.OfflineClient();
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"),
            FleetMembersProvider = () =>
            [
                new FleetMember("prod-eu", offline, ClusterEnvironment.Production),
                new FleetMember("dev-eu", offline, ClusterEnvironment.Development),
            ],
        };
        tab.ApplyFleet(new FleetResourceEvent("prod-eu", TestObjects.Added(TestObjects.Pod("payments", "api"))));
        tab.ApplyFleet(new FleetResourceEvent("dev-eu", TestObjects.Added(TestObjects.Pod("payments", "api"))));
        tab.Client = offline;
        tab.Environment = tabEnvironment;
        tab.SelectedRow = tab.Rows.Single(r => r.ClusterName == rowCluster);

        tab.DeleteSelectedCommand.Execute(null);

        var action = await SettledAsync(tab.PendingRowAction!);
        await Assert.That(action.Target).Contains($" on {rowCluster}");
        await Assert.That(action.IsProduction).IsEqualTo(expectArmed);
        await Assert.That(action.Message is null).IsEqualTo(expectArmed);
    }

    // ---------------------------------------------------------------- naming the cluster

    [Test]
    public async Task The_strip_names_the_tabs_context()
    {
        SetConfirmDeletes(true);
        var tab = PodTab(ClusterEnvironment.Development, out _);

        tab.DeleteSelectedCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Target).IsEqualTo("Pod/checkout-7f9c in payments on test-cluster");
        await Assert.That(tab.PendingRowAction.Question).Contains("on test-cluster");
    }

    /// <summary>
    /// A cluster assigned production by hand need not say "prod" anywhere in its name, so the
    /// strip says it in words beside the colour (UI rule 11).
    /// </summary>
    [Test]
    public async Task A_production_strip_says_production_in_words()
    {
        SetConfirmDeletes(true);
        var tab = PodTab(ClusterEnvironment.Production, out _);

        tab.DeleteSelectedCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Target).IsEqualTo("Pod/checkout-7f9c in payments on test-cluster (production)");
    }

    [Test]
    public async Task The_demo_strip_names_the_demo_cluster()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == "apps" && k.Descriptor.Kind == "Deployment"));
        tab.SelectedRow = tab.Rows.First();

        tab.RestartSelectedCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Target).EndsWith($" on {ClusterContext.Demo.Name}");
    }

    // ---------------------------------------------------------------- the YAML editor

    private static YamlEditorTabViewModel EditorFrom(ClusterTabViewModel tab)
    {
        tab.EditSelectedYamlCommand.Execute(null);
        return tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Single();
    }

    [Test]
    public async Task The_yaml_editors_delete_asks_on_production_with_the_preference_off()
    {
        SetConfirmDeletes(false);
        var editor = EditorFrom(PodTab(ClusterEnvironment.Production, out _));

        await editor.RequestDeleteCommand.ExecuteAsync(null);

        await Assert.That(editor.IsConfirmingDelete).IsTrue();
        await Assert.That(editor.IsDeleted).IsFalse();
        await Assert.That(editor.DeleteTargetDescription)
            .IsEqualTo("Pod/checkout-7f9c in namespace payments on test-cluster (production)");
    }

    [Test]
    public async Task The_yaml_editors_delete_runs_at_once_off_production_with_the_preference_off()
    {
        SetConfirmDeletes(false);
        var editor = EditorFrom(PodTab(ClusterEnvironment.Staging, out _));

        await editor.RequestDeleteCommand.ExecuteAsync(null);

        await Assert.That(editor.IsConfirmingDelete).IsFalse();
        await Assert.That(editor.StatusMessage).StartsWith("Delete failed:");
        await Assert.That(editor.DeleteTargetDescription).IsEqualTo("Pod/checkout-7f9c in namespace payments on test-cluster");
    }

    /// <summary>The one rule both paths share.</summary>
    [Test]
    public async Task The_rule_is_the_preference_or_production()
    {
        await Assert.That(RowActionViewModel.DeleteNeedsConfirm(false, ClusterEnvironment.Production)).IsTrue();
        await Assert.That(RowActionViewModel.DeleteNeedsConfirm(true, ClusterEnvironment.Development)).IsTrue();
        await Assert.That(RowActionViewModel.DeleteNeedsConfirm(false, ClusterEnvironment.Staging)).IsFalse();
        await Assert.That(RowActionViewModel.DeleteNeedsConfirm(false, ClusterEnvironment.Unknown)).IsFalse();

        // Only a delete can skip the strip at all.
        var restart = new RowActionViewModel(
            RowActionKind.Restart, client: null, TestObjects.PodDescriptor, "payments", "x");
        await Assert.That(restart.NeedsConfirm(false)).IsTrue();
    }
}
