using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The advanced view's whole remit: which sidebar sections the reader sees.
///
/// <para>
/// It used to hide content-area controls too — the list's usage columns, pod detail's
/// Usage tab, the fleet toggle, both log toolbars, YAML force-apply, the Helm and RBAC
/// palette entries and a CRD's own priority-1 columns. That answered a complaint about
/// a crowded sidebar by hiding things everywhere except the sidebar, and what it hid
/// was mostly what somebody had gone looking for. These tests pin the new contract in
/// both directions: the two sections it governs, and the fact that it governs nothing
/// else.
/// </para>
/// </summary>
public class SidebarAdvancedSectionTests
{
    private static ResourceDescriptor Kind(string group, string kind, string plural) =>
        new(group, "v1", kind, plural, kind.ToLowerInvariant(), Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>
    /// A tab carrying one section per title, each with a kind in it — the shape
    /// <c>RebuildSidebar</c> produces, without needing a cluster to discover anything.
    /// </summary>
    private static ClusterTabViewModel TabWithSections(params string[] titles)
    {
        var tab = TestObjects.Tab();
        foreach (var title in titles)
        {
            var section = new SidebarSectionViewModel(title);
            section.Kinds.Add(new SidebarKindViewModel(
                Kind("example.io", $"{title}Thing", $"{title.ToLowerInvariant()}things"), "CogIconGeometry"));
            tab.SidebarSections.Add(section);
        }

        // The gate is derived wherever the inputs change; flipping the switch through
        // its own setter is what a click on the chip does.
        tab.IsAdvancedView = tab.IsAdvancedView;
        return tab;
    }

    private static SidebarSectionViewModel Section(ClusterTabViewModel tab, string title) =>
        tab.SidebarSections.First(s => s.Title == title);

    // ------------------------------------------------------- what counts as advanced

    [Test]
    public async Task The_discovery_driven_sections_are_curated()
    {
        foreach (var title in new[] { "Workloads", "Network", "Config", "Storage", SidebarGrouping.ClusterSection, "CRDs" })
        {
            await Assert.That(SidebarGrouping.IsCuratedSection(title)).IsTrue();
        }
    }

    /// <summary>
    /// Argo and Helm only exist at all on a cluster that has them, which is already the
    /// evidence UI rule 1 asks for; Recent holds what the reader just chose.
    /// </summary>
    [Test]
    public async Task Argo_Helm_and_Recent_are_never_curated()
    {
        foreach (var title in new[] { SidebarGrouping.ArgoSection, SidebarGrouping.HelmSection, SidebarGrouping.RecentSection })
        {
            await Assert.That(SidebarGrouping.IsCuratedSection(title)).IsFalse();
        }
    }

    /// <summary>
    /// The basic view's list, by group and Kind. The machinery that sits in the ordinary
    /// sections is the half the old section-only rule missed.
    /// </summary>
    [Test]
    public async Task The_basic_view_keeps_the_everyday_kinds_and_drops_the_machinery()
    {
        foreach (var (group, kind) in new[]
                 {
                     ("", "Pod"), ("apps", "Deployment"), ("apps", "ReplicaSet"), ("batch", "CronJob"),
                     ("", "Service"), ("networking.k8s.io", "Ingress"), ("", "ConfigMap"), ("", "Event"),
                     ("", "PersistentVolumeClaim"), ("", "PersistentVolume"), ("", "Node"), ("", "Namespace"),
                 })
        {
            await Assert.That(SidebarGrouping.IsShownInBasicView(Kind(group, kind, ""))).IsTrue();
        }

        foreach (var (group, kind) in new[]
                 {
                     ("apps", "ControllerRevision"), ("", "ReplicationController"), ("", "PodTemplate"),
                     ("", "Endpoints"), ("discovery.k8s.io", "EndpointSlice"), ("networking.k8s.io", "IngressClass"),
                     ("events.k8s.io", "Event"), ("", "LimitRange"), ("storage.k8s.io", "CSIDriver"),
                     ("storage.k8s.io", "VolumeAttachment"), ("rbac.authorization.k8s.io", "ClusterRole"),
                     // A CRD that shares a built-in's Kind is not the built-in.
                     ("example.io", "Deployment"),
                 })
        {
            await Assert.That(SidebarGrouping.IsShownInBasicView(Kind(group, kind, ""))).IsFalse();
        }
    }

    // --------------------------------------------------------------- the sidebar gate

    /// <summary>On by default, so a fresh install is missing nothing.</summary>
    [Test]
    public async Task A_new_tab_shows_every_section()
    {
        var tab = TabWithSections("Workloads", SidebarGrouping.ClusterSection, "CRDs");

        await Assert.That(tab.IsAdvancedView).IsTrue();
        await Assert.That(Section(tab, SidebarGrouping.ClusterSection).IsSectionVisible).IsTrue();
        await Assert.That(Section(tab, "CRDs").IsSectionVisible).IsTrue();
    }

    [Test]
    public async Task Turning_it_off_hides_the_machinery_and_the_sections_left_empty()
    {
        var tab = TestObjects.Tab();
        var workloads = new SidebarSectionViewModel("Workloads");
        var pods = new SidebarKindViewModel(Kind("", "Pod", "pods"), "CubeOutlineIconGeometry");
        var revisions = new SidebarKindViewModel(Kind("apps", "ControllerRevision", "controllerrevisions"), "CubeOutlineIconGeometry");
        workloads.Kinds.Add(pods);
        workloads.Kinds.Add(revisions);
        tab.SidebarSections.Add(workloads);
        var crds = new SidebarSectionViewModel("CRDs");
        crds.Kinds.Add(new SidebarKindViewModel(Kind("cert-manager.io", "Certificate", "certificates"), "PuzzleIconGeometry"));
        tab.SidebarSections.Add(crds);
        var argo = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        argo.Kinds.Add(new SidebarKindViewModel(Kind("argoproj.io", "Application", "applications"), "SourceBranchIconGeometry"));
        tab.SidebarSections.Add(argo);

        tab.IsAdvancedView = false;

        await Assert.That(workloads.IsSectionVisible).IsTrue();
        await Assert.That(pods.IsVisible).IsTrue();
        await Assert.That(revisions.IsVisible).IsFalse();
        await Assert.That(crds.IsSectionVisible).IsFalse();
        await Assert.That(argo.IsSectionVisible).IsTrue();
    }

    [Test]
    public async Task Turning_it_back_on_restores_them()
    {
        var tab = TabWithSections("Workloads", SidebarGrouping.ClusterSection);

        tab.IsAdvancedView = false;
        tab.IsAdvancedView = true;

        await Assert.That(Section(tab, SidebarGrouping.ClusterSection).IsSectionVisible).IsTrue();
    }

    /// <summary>
    /// A filter is a deliberate search for one thing, so it reaches into the sections
    /// the switch hides. A query that matches a kind and then renders nothing is the
    /// "worse than no match" failure this app's own palette rules name — and it is the
    /// reason hiding a section is safe at all.
    /// </summary>
    [Test]
    public async Task A_filter_reaches_into_a_hidden_section()
    {
        var tab = TabWithSections("Workloads", SidebarGrouping.ClusterSection);
        tab.IsAdvancedView = false;

        tab.SidebarFilter = "ClusterThing";

        var cluster = Section(tab, SidebarGrouping.ClusterSection);
        await Assert.That(cluster.IsSectionVisible).IsTrue();
        await Assert.That(cluster.HasVisibleKinds).IsTrue();

        // …and hides again once the search is over, rather than latching open.
        tab.SidebarFilter = "";
        await Assert.That(cluster.IsSectionVisible).IsFalse();
    }

    /// <summary>
    /// The two reasons a section can be hidden are independent: a filter that nothing in
    /// a *visible* section matches still hides it.
    /// </summary>
    [Test]
    public async Task A_filter_matching_nothing_hides_an_ordinary_section()
    {
        var tab = TabWithSections("Workloads");

        tab.SidebarFilter = "nothing-matches-this";

        await Assert.That(Section(tab, "Workloads").IsSectionVisible).IsFalse();
    }

    /// <summary>
    /// Nodes and Namespaces live in Cluster but are not API machinery, and hiding the
    /// whole section took the only route to node detail, cordon and drain with it. The
    /// basic view keeps the section with those two in it and nothing else.
    /// </summary>
    [Test]
    public async Task The_basic_view_keeps_Nodes_and_Namespaces_in_Cluster()
    {
        var tab = TestObjects.Tab();
        var cluster = new SidebarSectionViewModel(SidebarGrouping.ClusterSection);
        var nodes = new SidebarKindViewModel(Kind("", "Node", "nodes"), "CogIconGeometry");
        var namespaces = new SidebarKindViewModel(Kind("", "Namespace", "namespaces"), "CogIconGeometry");
        var roles = new SidebarKindViewModel(Kind("rbac.authorization.k8s.io", "ClusterRole", "clusterroles"), "CogIconGeometry");
        // Kind "NodeMetrics", resource "nodes": the kind that used to render as a second "Nodes".
        var nodeMetrics = new SidebarKindViewModel(Kind("metrics.k8s.io", "NodeMetrics", "nodes"), "CogIconGeometry");
        foreach (var kind in new[] { nodes, namespaces, roles, nodeMetrics })
        {
            cluster.Kinds.Add(kind);
        }

        tab.SidebarSections.Add(cluster);

        tab.IsAdvancedView = false;

        await Assert.That(cluster.IsSectionVisible).IsTrue();
        await Assert.That(nodes.IsVisible).IsTrue();
        await Assert.That(namespaces.IsVisible).IsTrue();
        await Assert.That(roles.IsVisible).IsFalse();
        await Assert.That(nodeMetrics.IsVisible).IsFalse();

        tab.IsAdvancedView = true;

        await Assert.That(roles.IsVisible).IsTrue();
        await Assert.That(nodeMetrics.IsVisible).IsTrue();
    }

    /// <summary>
    /// <c>metrics.k8s.io</c> serves Kind <c>NodeMetrics</c> as resource <c>nodes</c>; re-casing
    /// the plural against the Kind gave "Nodes", a second row indistinguishable from the real one.
    /// </summary>
    [Test]
    public async Task A_plural_that_is_not_a_plural_of_the_Kind_shows_the_Kind()
    {
        await Assert.That(new SidebarKindViewModel(Kind("metrics.k8s.io", "NodeMetrics", "nodes"), "x").DisplayName)
            .IsEqualTo("NodeMetrics");
        await Assert.That(new SidebarKindViewModel(Kind("metrics.k8s.io", "PodMetrics", "pods"), "x").DisplayName)
            .IsEqualTo("PodMetrics");

        // …without losing what the re-casing is for.
        await Assert.That(new SidebarKindViewModel(Kind("", "Node", "nodes"), "x").DisplayName).IsEqualTo("Nodes");
        await Assert.That(new SidebarKindViewModel(Kind("networking.k8s.io", "NetworkPolicy", "networkpolicies"), "x").DisplayName)
            .IsEqualTo("NetworkPolicies");
        await Assert.That(new SidebarKindViewModel(Kind("", "Endpoints", "endpoints"), "x").DisplayName).IsEqualTo("Endpoints");
    }

    // ------------------------------------------------- and nothing outside the sidebar

    /// <summary>
    /// The negative half, and the one that fails if anything re-gates a content-area
    /// control on this switch. The usage columns are the case with the loudest history:
    /// they are what a reader opens a pod list to see.
    /// </summary>
    [Test]
    public async Task The_switch_does_not_touch_the_list()
    {
        var tab = TestObjects.Tab();
        tab.AreMetricsVisible = true;

        await Assert.That(tab.AreUsageColumnsVisible).IsTrue();

        tab.IsAdvancedView = false;

        await Assert.That(tab.AreUsageColumnsVisible).IsTrue();
    }

    [Test]
    public async Task The_switch_does_not_hide_the_fleet_toggle()
    {
        var tab = TestObjects.Tab();
        tab.IsFleetViewAvailable = true;

        tab.IsAdvancedView = false;

        await Assert.That(tab.IsFleetToggleVisible).IsTrue();
    }
}
