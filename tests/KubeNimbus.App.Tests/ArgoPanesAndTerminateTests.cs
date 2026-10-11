using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Bundle K's App half: the ApplicationSet and AppProject panes (#194) and the Application
/// pane from the ordinary list, Terminate sync (#193), and the Argo dashboard's fleet list
/// (#195). Driven through the real demo tab, as <see cref="ArgoDashboardTests"/> is.
/// </summary>
public class ArgoPanesAndTerminateTests
{
    private static ClusterTabViewModel DemoTab(string? kind = null)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => kind is null ? k.IsArgoDashboard : k.Descriptor is { Group: ArgoCd.Group } d && d.Kind == kind));
        return tab;
    }

    private static InspectorTabViewModelBase Open(ClusterTabViewModel tab, string name)
    {
        tab.SelectedNamespace = "argocd";
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);
        return tab.SelectedInspectorTab!;
    }

    // ------------------------------------------------------------------ #194

    [Test]
    public async Task An_application_set_opens_its_pane_with_the_applications_it_generated_worst_first()
    {
        var pane = Open(DemoTab("ApplicationSet"), "payments-services");

        await Assert.That(pane).IsTypeOf<ArgoApplicationSetTabViewModel>();
        var set = (ArgoApplicationSetTabViewModel)pane;
        await Assert.That(set.Generated.Count).IsEqualTo(5);
        await Assert.That(set.Generated[0].Name).IsEqualTo("fraud-detector");
        await Assert.That(set.Generated[1].Name).IsEqualTo("settlement-batch");
        await Assert.That(set.GeneratedSummary).IsEqualTo("5 generated · 3 out of sync · 2 degraded or missing");
        await Assert.That(set.GeneratorsText).StartsWith("Git directories · apps/*/overlays/prod, not apps/monitoring/*");
        await Assert.That(set.Conditions.Any(c => c.IsProblem)).IsFalse();
    }

    [Test]
    public async Task An_app_project_opens_its_pane()
    {
        var pane = Open(DemoTab("AppProject"), "payments");

        await Assert.That(pane).IsTypeOf<ArgoProjectTabViewModel>();
        var project = ((ArgoProjectTabViewModel)pane).Project!;
        await Assert.That(project.SourceRepos.Count).IsEqualTo(2);
        await Assert.That(project.SyncWindows[0].Scope).IsEqualTo("Matches applications *. Manual syncs are still allowed.");
    }

    /// <summary>An Application on the ordinary list opens the dashboard's pane, not its YAML (UI rule 2).</summary>
    [Test]
    public async Task An_application_on_the_ordinary_list_opens_the_application_pane()
    {
        var pane = Open(DemoTab("Application"), "checkout");

        await Assert.That(pane).IsTypeOf<ArgoApplicationTabViewModel>();
        await Assert.That(pane.Key).IsEqualTo(ArgoApplicationTabViewModel.KeyFor("", "argocd", "checkout"));
    }

    // ------------------------------------------------------------------ #193

    [Test]
    public async Task Terminate_is_offered_only_while_a_sync_is_running()
    {
        var tab = DemoTab();

        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "checkout");
        await Assert.That(tab.CanTerminateSelectedArgoSync).IsFalse();
        await Assert.That(tab.TerminateArgoSyncCommand.CanExecute(null)).IsFalse();

        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "notification-dispatcher");
        await Assert.That(tab.CanTerminateSelectedArgoSync).IsTrue();
        await Assert.That(tab.TerminateArgoSyncCommand.CanExecute(null)).IsTrue();
    }

    [Test]
    public async Task Terminate_asks_first_and_says_what_stays_applied()
    {
        var tab = DemoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "notification-dispatcher");

        tab.TerminateArgoSyncCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.Kind).IsEqualTo(RowActionKind.ArgoTerminate);
        await Assert.That(action.FiredOnClick).IsFalse();
        await Assert.That(action.IsQuestionVisible).IsTrue();
        await Assert.That(action.Headline).StartsWith("Stop syncing Application notification-dispatcher");
        await Assert.That(action.Consequence).Contains("stays applied");
        await Assert.That(action.ConfirmLabel).IsEqualTo("Stop syncing");
        await Assert.That(action.IsDestructive).IsFalse();
    }

    // ------------------------------------------------------------------ #195

    [Test]
    public async Task The_fleet_toggle_is_offered_on_the_argo_dashboard()
    {
        var tab = DemoTab();
        tab.IsFleetViewAvailable = true;

        await Assert.That(tab.IsArgoView).IsTrue();
        await Assert.That(tab.IsFleetToggleVisible).IsTrue();
    }

    /// <summary>
    /// The same argocd/checkout is on every cluster of a fleet; with the key alone two rows
    /// compare equal and swap on every reload. The cluster breaks the tie.
    /// </summary>
    [Test]
    public async Task Fleet_rows_with_the_same_key_order_by_cluster()
    {
        var app = Demo.DemoData.ArgoApplications.First(a => a.Name == "checkout");
        var descriptor = new ResourceDescriptor("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []);
        var us = new ArgoApplicationRowViewModel(app, descriptor, "us");
        var eu = new ArgoApplicationRowViewModel(app, descriptor, "eu");

        await Assert.That(ArgoApplicationComparer.ByRank.Compare(eu, us)).IsLessThan(0);
        await Assert.That(new ArgoApplicationComparer(ArgoApplicationComparer.Name, descending: false).Compare(us, eu)).IsGreaterThan(0);
        await Assert.That(new ArgoApplicationComparer(ArgoApplicationComparer.Cluster, descending: false).Compare(eu, us)).IsLessThan(0);
    }
}
