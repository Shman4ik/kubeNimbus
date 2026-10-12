using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The sidebar's selected row and its Recent section, on the demo cluster, which builds its
/// sidebar through the same <c>SidebarGrouping</c> calls discovery does.
///
/// <para>
/// ENG-26 and #317: exactly one row is drawn selected — the kind's own row while it is on
/// screen, its copy in Recent while it is not — however the kind came to be selected. ENG-5: Recent survives the tab, per cluster, as
/// kind keys resolved against the next catalog.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c>: Recent is written to <c>workspace.json</c> behind the
/// process-wide <c>WorkspaceStore.DirectoryOverride</c>, and every demo tab is the same
/// cluster, so a parallel test selecting a kind on the demo cluster would change the
/// Recent section these read back.
/// </para>
/// </summary>
[NotInParallel]
public class SidebarRecentKindsTests
{
    [Before(Test)]
    public void RedirectStores() => TestObjects.RedirectStores();

    private static ClusterTabViewModel DemoTab()
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    private static string DemoKey => $"{ClusterContext.Demo.KubeconfigPath}\n{ClusterContext.Demo.Name}";

    /// <summary>The sidebar's own row for a kind — never its Recent copy.</summary>
    private static SidebarKindViewModel Row(ClusterTabViewModel tab, string group, string kind) =>
        tab.SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind);

    private static SidebarSectionViewModel? Recent(ClusterTabViewModel tab) =>
        tab.SidebarSections.FirstOrDefault(s => s.Title == SidebarGrouping.RecentSection);

    private static string RecentNames(ClusterTabViewModel tab) =>
        string.Join(", ", Recent(tab)?.Kinds.Select(k => k.DisplayName) ?? []);

    /// <summary>Every row drawn selected, as "Section/Kind", in sidebar order.</summary>
    private static string Lit(ClusterTabViewModel tab) =>
        string.Join(", ", tab.SidebarSections
            .SelectMany(s => s.Kinds.Where(k => k.IsSelected).Select(k => $"{s.Title}/{k.DisplayName}")));

    // ------------------------------------------------------------------ ENG-26

    [Test]
    public async Task The_kind_on_screen_is_lit_in_its_own_section_only()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "", "Service"));
        tab.SelectKindCommand.Execute(Row(tab, "", "ConfigMap"));
        tab.SelectKindCommand.Execute(Row(tab, "", "Pod"));

        // Pods is in Recent too. Both copies used to be lit, which read as two selections
        // and gave UI Automation two selected items (#317).
        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Pods");
    }

    /// <summary>
    /// The Recent copy takes the highlight only while the kind's own row is off screen, so
    /// the kind on screen is always lit somewhere a reader can see (ENG-26's half), and
    /// never in two places (#317's).
    /// </summary>
    [Test]
    public async Task Folding_the_kinds_section_hands_the_highlight_to_its_Recent_copy_and_back()
    {
        var tab = DemoTab();
        var workloads = tab.SidebarSections.Single(s => s.Title == "Workloads");
        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Pods");

        workloads.IsExpanded = false;
        await Assert.That(Lit(tab)).IsEqualTo("Recent/Pods");

        workloads.IsExpanded = true;
        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Pods");
    }

    [Test]
    public async Task A_filter_that_hides_the_kinds_own_row_lights_one_row_at_most()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "", "Service"));

        // "serv" matches Services in Network and in Recent; the own row stays lit.
        tab.SidebarFilter = "serv";
        await Assert.That(Lit(tab)).IsEqualTo("Network/Services");

        // A filter that hides Services everywhere still lights one row, its own.
        tab.SidebarFilter = "configm";
        await Assert.That(Lit(tab)).IsEqualTo("Network/Services");

        tab.SidebarFilter = "";
        await Assert.That(Lit(tab)).IsEqualTo("Network/Services");
    }

    /// <summary>Every route that selects a kind ends with exactly one row lit.</summary>
    [Test]
    public async Task Every_way_of_selecting_a_kind_leaves_exactly_one_row_lit()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "", "Service"));
        tab.SelectKindCommand.Execute(Row(tab, "", "ConfigMap"));
        // Config starts folded, so the Recent copy is the one on screen.
        await Assert.That(Lit(tab)).IsEqualTo("Recent/ConfigMaps");

        tab.SelectKindCommand.Execute(Recent(tab)!.Kinds.Single(k => k.DisplayName == "Services"));
        await Assert.That(Lit(tab)).IsEqualTo("Network/Services");

        tab.SelectedKind = Row(tab, "apps", "Deployment");
        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Deployments");

        tab.SelectedKind = Recent(tab)!.Kinds.First(k => k.DisplayName == "ConfigMaps");
        await Assert.That(Lit(tab).Split(", ").Length).IsEqualTo(1);
    }

    /// <summary>
    /// The palette, a restore and the screenshot harness assign the kind rather than going
    /// through the sidebar command, and a highlight that only the command maintained is how
    /// two rows came to be drawn selected at once.
    /// </summary>
    [Test]
    public async Task Assigning_the_kind_directly_moves_the_highlight_too()
    {
        var tab = DemoTab();
        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Pods");

        tab.SelectedKind = Row(tab, "apps", "Deployment");

        await Assert.That(Lit(tab)).IsEqualTo("Workloads/Deployments");
    }

    /// <summary>
    /// Clicking the Recent copy of the kind already on screen is what clicking its own row
    /// is: nothing. It used to be a second selection of the same kind, which restarted the
    /// list and dropped the search the reader had typed.
    /// </summary>
    [Test]
    public async Task Clicking_the_Recent_copy_of_the_kind_on_screen_changes_nothing()
    {
        var tab = DemoTab();
        var pods = Row(tab, "", "Pod");
        tab.RowFilter = "checkout";

        tab.SelectKindCommand.Execute(Recent(tab)!.Kinds.Single(k => ReferenceEquals(k.Descriptor, pods.Descriptor)));

        await Assert.That(tab.RowFilter).IsEqualTo("checkout");
        await Assert.That(ReferenceEquals(tab.SelectedKind, pods)).IsTrue();
    }

    [Test]
    public async Task A_Recent_entry_selects_its_own_row_and_leaves_Recent_in_place()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "", "Service"));
        tab.SelectKindCommand.Execute(Row(tab, "", "ConfigMap"));
        await Assert.That(RecentNames(tab)).IsEqualTo("ConfigMaps, Services, Pods");

        tab.SelectKindCommand.Execute(Recent(tab)!.Kinds.Single(k => k.DisplayName == "Services"));

        await Assert.That(ReferenceEquals(tab.SelectedKind, Row(tab, "", "Service"))).IsTrue();
        await Assert.That(RecentNames(tab)).IsEqualTo("ConfigMaps, Services, Pods");
        await Assert.That(Lit(tab)).IsEqualTo("Network/Services");
    }

    // ------------------------------------------------------------------- ENG-5

    [Test]
    public async Task Recent_kinds_survive_into_the_next_tab_on_the_same_cluster()
    {
        var first = DemoTab();
        first.SelectKindCommand.Execute(Row(first, "", "Service"));
        first.SelectKindCommand.Execute(Row(first, "", "ConfigMap"));

        var second = DemoTab();

        // Restored, then the tab's own opening kind (Pods) recorded on top of them.
        await Assert.That(RecentNames(second)).IsEqualTo("Pods, ConfigMaps, Services");

        // The restored entries are the new catalog's own rows, not the old tab's.
        var services = Recent(second)!.Kinds.Single(k => k.DisplayName == "Services");
        await Assert.That(ReferenceEquals(services.Descriptor, Row(second, "", "Service").Descriptor)).IsTrue();
    }

    /// <summary>Kind keys only, under the cluster's own key — never anything read from it.</summary>
    [Test]
    public async Task Recent_kinds_are_saved_as_kind_keys_per_cluster()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "apps", "Deployment"));

        var saved = WorkspaceStore.Load().RecentKinds!;
        await Assert.That(saved.Keys.Single()).IsEqualTo(DemoKey);
        await Assert.That(string.Join(", ", saved[DemoKey])).IsEqualTo("apps/Deployment, /Pod");
    }

    /// <summary>A kind the cluster no longer serves — a CRD uninstalled since — is dropped.</summary>
    [Test]
    public async Task A_saved_kind_the_catalog_no_longer_has_is_not_restored()
    {
        WorkspaceStore.Save(WorkspaceStore.Load() with
        {
            RecentKinds = new() { [DemoKey] = ["gone.example.io/Widget", "/Service"] },
        });

        var tab = DemoTab();

        await Assert.That(RecentNames(tab)).IsEqualTo("Pods, Services");
    }

    /// <summary>
    /// #213 / #244: a section's rows draw no glyph — it was the same on every row under a
    /// header that names the section — and only Recent's rows, which mix sections, keep one.
    /// </summary>
    [Test]
    public async Task Only_Recent_rows_draw_the_kind_glyph()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "apps", "Deployment"));

        var withGlyph = tab.SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .Where(k => k.ShowIcon)
            .Select(k => k.DisplayName)
            .ToList();
        await Assert.That(string.Join(", ", withGlyph)).IsEqualTo("");
        await Assert.That(Recent(tab)!.Kinds.Count).IsGreaterThan(0);
        await Assert.That(Recent(tab)!.Kinds.All(k => k.ShowIcon)).IsTrue();
    }
}
