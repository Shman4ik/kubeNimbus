using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The App-layer half of the Argo CD integration: the sidebar section and its dashboard
/// row, the dashboard's ordering and counts, the detail pane, and the two actions arming
/// the shared confirm strip.
///
/// <para>
/// It drives the real demo tab — <c>ConnectCommand</c> on <see cref="ClusterContext.Demo"/>
/// and the real <c>SelectKindCommand</c> — for the same reason the node tests do: the thing
/// worth pinning is the wiring, and a test over hand-built view models pins a second copy
/// of it. No Avalonia application is started; a demo tab has no client, so no watch and no
/// poll ever reach the dispatcher.
/// </para>
/// </summary>
public class ArgoDashboardTests
{
    private static ClusterTabViewModel ArgoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.IsArgoDashboard);
        tab.SelectKindCommand.Execute(kind);
        return tab;
    }

    // ------------------------------------------------------------------ sidebar

    /// <summary>
    /// Argo's kinds get their own section instead of being eight more rows in a CRDs section
    /// that already runs past a hundred — and the dashboard row sits at the top of it, above
    /// the Applications kind it is a different question about.
    /// </summary>
    [Test]
    public async Task Argo_kinds_land_in_their_own_section_with_the_dashboard_on_top()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        var section = tab.SidebarSections.First(s => s.Title == SidebarGrouping.ArgoSection);

        await Assert.That(section.Kinds[0].IsArgoDashboard).IsTrue();
        await Assert.That(section.Kinds[0].DisplayName).IsEqualTo("Argo CD");
        await Assert.That(section.Kinds.Skip(1).Select(k => k.Descriptor.Kind))
            .Contains("Application");

        // And nothing from argoproj.io is left behind in CRDs.
        var crds = tab.SidebarSections.FirstOrDefault(s => s.Title == "CRDs");
        await Assert.That(crds?.Kinds.Any(k => k.Descriptor.Group == ArgoCd.Group) ?? false).IsFalse();
    }

    /// <summary>
    /// The dashboard row is gated on the Application <em>kind</em>, not on the group: a
    /// cluster running only Argo Rollouts or Argo Workflows has an Argo section with no Argo
    /// CD in it, and a GitOps dashboard there would open on nothing.
    /// </summary>
    [Test]
    public async Task The_dashboard_row_is_absent_without_the_application_kind()
    {
        TestObjects.RedirectStores();
        var section = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        ResourceDescriptor[] catalog =
        [
            new("argoproj.io", "v1alpha1", "Rollout", "rollouts", "rollout", true, [], []),
        ];

        SidebarGrouping.AddArgoDashboard([section], catalog);

        await Assert.That(section.Kinds).IsEmpty();
    }

    // ---------------------------------------------------------------- dashboard

    /// <summary>
    /// Selecting the dashboard swaps the content area for it and does not start a watch —
    /// the same arrangement the Helm browser has, and what <c>IsResourceListVisible</c>
    /// exists to keep from being two negated bindings on every element in the list's half of
    /// the view.
    /// </summary>
    [Test]
    public async Task Selecting_the_dashboard_replaces_the_resource_list()
    {
        var tab = ArgoTab();

        await Assert.That(tab.IsArgoView).IsTrue();
        await Assert.That(tab.IsHelmView).IsFalse();
        await Assert.That(tab.IsResourceListVisible).IsFalse();
        await Assert.That(tab.PrinterColumns).IsEmpty();
    }

    /// <summary>
    /// Worst first: degraded above missing above out-of-sync above everything healthy. A
    /// sixty-Application dashboard is unreadable in any other order, and it is the whole
    /// reason this is a dashboard rather than the ordinary Applications list.
    /// </summary>
    [Test]
    public async Task Applications_are_ordered_by_what_needs_attention()
    {
        var tab = ArgoTab();

        var names = tab.ArgoApplications.Select(a => a.Name).ToList();

        await Assert.That(names[0]).IsEqualTo("fraud-detector");   // Synced but Degraded
        await Assert.That(names[1]).IsEqualTo("settlement-batch"); // Missing
        await Assert.That(names.IndexOf("checkout")).IsGreaterThan(names.IndexOf("ledger-api"));
        await Assert.That(tab.ArgoApplications.Last().NeedsAttention).IsFalse();
    }

    /// <summary>
    /// The counts come from the same parse the rows do, and the attention line states the
    /// answer rather than leaving it to be counted off the pills.
    /// </summary>
    [Test]
    public async Task The_summary_counts_every_application_and_states_what_needs_attention()
    {
        var tab = ArgoTab();

        await Assert.That(tab.ArgoCounts!.Total).IsEqualTo(DemoData.ArgoApplications.Count);
        await Assert.That(tab.ArgoCounts!.Degraded).IsEqualTo(1);
        await Assert.That(tab.ArgoCounts!.Missing).IsEqualTo(1);
        await Assert.That(tab.ArgoAttentionSummary).Contains("need attention");
        await Assert.That(tab.IsArgoEmpty).IsFalse();
    }

    /// <summary>
    /// The namespace picker does not narrow the dashboard: Applications live in one
    /// namespace while what they manage is spread across the rest, so a dashboard that
    /// followed the picker would be empty everywhere except the one place nobody browses.
    /// </summary>
    [Test]
    public async Task The_dashboard_is_cluster_wide_and_ignores_the_namespace_picker()
    {
        var tab = ArgoTab();
        var before = tab.ArgoApplications.Count;

        tab.SelectedNamespace = "payments";

        await Assert.That(tab.ArgoApplications.Count).IsEqualTo(before);
        await Assert.That(tab.ArgoApplications.All(a => a.Namespace == "argocd")).IsTrue();
    }

    // ------------------------------------------------------------------ actions

    /// <summary>
    /// Sync and refresh arm the shared strip rather than firing on the click, and they name
    /// the Application they would act on — a confirm that does not name its object is not
    /// one.
    /// </summary>
    [Test]
    public async Task Sync_arms_the_confirm_strip_against_the_selected_application()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "checkout");

        await Assert.That(tab.CanSyncSelectedArgoApplication).IsTrue();
        await Assert.That(tab.ArgoActionLabel).IsEqualTo("argocd/checkout");

        tab.SyncArgoApplicationCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Kind).IsEqualTo(RowActionKind.ArgoSync);
        await Assert.That(tab.PendingRowAction!.IsArgoSync).IsTrue();
        await Assert.That(tab.PendingRowAction!.Target).Contains("checkout");
    }

    /// <summary>Prune deletes things, so it is off until somebody says otherwise.</summary>
    [Test]
    public async Task Prune_is_off_by_default()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications[0];
        tab.SyncArgoApplicationCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.ArgoPrune).IsFalse();
    }

    /// <summary>
    /// A refresh changes nothing on the cluster, and the confirm says so — otherwise the
    /// two actions read as the same thing with different names.
    /// </summary>
    [Test]
    public async Task Refresh_says_it_changes_nothing()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications[0];
        tab.RefreshArgoApplicationCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.Kind).IsEqualTo(RowActionKind.ArgoRefresh);
        await Assert.That(tab.PendingRowAction!.IsArgoSync).IsFalse();
        await Assert.That(tab.PendingRowAction!.Question).Contains("Nothing on the cluster changes");
    }

    /// <summary>
    /// The demo cluster arms the strip and refuses in place with a reason (demo rule 5) —
    /// never a hidden action, and never a silent no-op.
    /// </summary>
    [Test]
    public async Task The_demo_cluster_arms_the_strip_and_refuses_in_place()
    {
        var tab = ArgoTab();
        tab.SelectedArgoApplication = tab.ArgoApplications[0];
        tab.SyncArgoApplicationCommand.Execute(null);

        await Assert.That(tab.PendingRowAction!.IsDemo).IsTrue();
        await Assert.That(tab.PendingRowAction!.ConfirmCommand.CanExecute(null)).IsFalse();
    }

    /// <summary>
    /// The same two actions from the ordinary Applications list, which is still there one
    /// row below the dashboard. A menu item that worked on one surface and not the other
    /// would be the harder thing to explain.
    /// </summary>
    [Test]
    public async Task The_actions_are_offered_from_an_ordinary_applications_list_too()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        var kind = tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: ArgoCd.Group, Kind: "Application" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedNamespace = "argocd";

        await Assert.That(tab.Rows).IsNotEmpty();
        tab.SelectedRow = tab.Rows[0];

        await Assert.That(tab.CanSyncSelectedArgoApplication).IsTrue();
        await Assert.That(tab.ArgoActionLabel).StartsWith("argocd/");
    }

    /// <summary>Nothing else on the cluster offers a sync — "sync this ConfigMap" is not a question.</summary>
    [Test]
    public async Task No_other_kind_offers_a_sync()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedRow = tab.Rows.FirstOrDefault();

        await Assert.That(tab.CanSyncSelectedArgoApplication).IsFalse();
        await Assert.That(tab.ArgoActionLabel).IsNull();
    }

    // ------------------------------------------------------------- detail pane

    /// <summary>
    /// The detail pane opens on the worst managed resource, not on whatever Argo happened to
    /// list first: a 200-resource Application is scrolled to find the broken one otherwise.
    /// </summary>
    [Test]
    public async Task The_detail_pane_orders_managed_resources_worst_first()
    {
        var application = DemoData.ArgoApplications.First(a => a.Name == "fraud-detector");
        var descriptor = ArgoCd.ApplicationDescriptor(DemoData.BuildCatalog())!;

        var tab = new ArgoApplicationTabViewModel(client: null, descriptor, application);

        await Assert.That(tab.Resources[0].Kind).IsEqualTo("Deployment");
        await Assert.That(tab.Resources[0].HealthHealth).IsEqualTo("error");
        await Assert.That(tab.ResourceSummary).Contains("degraded or missing");
        await Assert.That(tab.Title).IsEqualTo("Argo/fraud-detector");
        await Assert.That(tab.IsDemo).IsTrue();
    }

    /// <summary>
    /// A resource Argo has no health check for (a ConfigMap has no notion of healthy) shows
    /// no health pill at all, rather than the word "Unknown" beside every configuration
    /// object in the list.
    /// </summary>
    [Test]
    public async Task A_resource_with_no_health_check_shows_no_health_pill()
    {
        var application = DemoData.ArgoApplications.First(a => a.Name == "checkout");
        var descriptor = ArgoCd.ApplicationDescriptor(DemoData.BuildCatalog())!;

        var tab = new ArgoApplicationTabViewModel(client: null, descriptor, application);
        var configMap = tab.Resources.First(r => r.Kind == "ConfigMap");

        await Assert.That(configMap.HasHealth).IsFalse();
        await Assert.That(configMap.HealthText).IsEqualTo("");
    }

    /// <summary>
    /// An Application Argo cannot compare has no resources, and that is its own state rather
    /// than an empty list (UI rule 9) — the conditions below it are the only thing explaining
    /// the blank.
    /// </summary>
    [Test]
    public async Task An_uncompared_application_says_why_its_resource_list_is_empty()
    {
        var application = DemoData.ArgoApplications.First(a => a.Name == "risk-scoring");
        var descriptor = ArgoCd.ApplicationDescriptor(DemoData.BuildCatalog())!;

        var tab = new ArgoApplicationTabViewModel(client: null, descriptor, application);

        await Assert.That(tab.HasResources).IsFalse();
        await Assert.That(tab.EmptyResourcesNotice).Contains("has not reconciled");
        await Assert.That(tab.HasConditions).IsTrue();
        await Assert.That(tab.Conditions[0].IsProblem).IsTrue();
    }

    /// <summary>
    /// The tab key is cluster-qualified like every other inspector key: the same Application
    /// name exists in the <c>argocd</c> namespace of every cluster in a fleet.
    /// </summary>
    [Test]
    public async Task The_tab_key_is_cluster_qualified()
    {
        await Assert.That(ArgoApplicationTabViewModel.KeyFor("prod", "argocd", "checkout"))
            .IsNotEqualTo(ArgoApplicationTabViewModel.KeyFor("staging", "argocd", "checkout"));
    }
}
