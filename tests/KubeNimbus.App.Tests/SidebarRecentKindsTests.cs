using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The sidebar's selected row and its Recent section, on the demo cluster, which builds its
/// sidebar through the same <c>SidebarGrouping</c> calls discovery does.
///
/// <para>
/// ENG-26: exactly one kind is drawn selected — its own row and its copy in Recent, and no
/// other — however it came to be selected. ENG-5: Recent survives the tab, per cluster, as
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
    public async Task The_kind_on_screen_is_lit_in_its_section_and_in_Recent_and_nowhere_else()
    {
        var tab = DemoTab();
        tab.SelectKindCommand.Execute(Row(tab, "", "Service"));
        tab.SelectKindCommand.Execute(Row(tab, "", "ConfigMap"));
        tab.SelectKindCommand.Execute(Row(tab, "", "Pod"));

        // Before: only the Workloads copy lit up, and the Recent entry for the kind being
        // looked at read as inactive.
        await Assert.That(Lit(tab)).IsEqualTo("Recent/Pods, Workloads/Pods");
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
        await Assert.That(Lit(tab)).IsEqualTo("Recent/Pods, Workloads/Pods");

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
        await Assert.That(Lit(tab)).IsEqualTo("Recent/Services, Network/Services");
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
}
