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
    public async Task Cluster_and_CRDs_are_the_advanced_sections()
    {
        await Assert.That(SidebarGrouping.IsAdvancedSection(SidebarGrouping.ClusterSection)).IsTrue();
        await Assert.That(SidebarGrouping.IsAdvancedSection("CRDs")).IsTrue();
    }

    /// <summary>
    /// The sections the app is actually for are never hidden. Argo and Helm are on this
    /// list too: they only exist at all on a cluster that has them, which is already the
    /// evidence test UI rule 1 asks for.
    /// </summary>
    [Test]
    public async Task The_sections_people_browse_are_never_advanced()
    {
        foreach (var title in new[]
                 {
                     "Workloads", "Network", "Config", "Storage",
                     SidebarGrouping.ArgoSection, SidebarGrouping.HelmSection, SidebarGrouping.RecentSection,
                 })
        {
            await Assert.That(SidebarGrouping.IsAdvancedSection(title)).IsFalse();
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
    public async Task Turning_it_off_hides_the_advanced_sections_and_only_those()
    {
        var tab = TabWithSections("Workloads", "Network", SidebarGrouping.ClusterSection, "CRDs");

        tab.IsAdvancedView = false;

        await Assert.That(Section(tab, SidebarGrouping.ClusterSection).IsSectionVisible).IsFalse();
        await Assert.That(Section(tab, "CRDs").IsSectionVisible).IsFalse();
        await Assert.That(Section(tab, "Workloads").IsSectionVisible).IsTrue();
        await Assert.That(Section(tab, "Network").IsSectionVisible).IsTrue();
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
