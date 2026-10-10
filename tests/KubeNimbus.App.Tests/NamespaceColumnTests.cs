using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-67's last part: the resource list's Namespace column shows only where it tells rows
/// apart. With exactly one namespace chosen it printed the picker's own value down every row;
/// with several, or all of them, it is what tells the rows apart. A cluster-scoped kind never
/// has it. The view reads <see cref="ClusterTabViewModel.IsNamespaceColumnShown"/> whenever the
/// kind or the namespaces change (ClusterTabView.ApplySummaryColumns), which the screenshot
/// scenarios with one namespace chosen show.
/// </summary>
public class NamespaceColumnTests
{
    private static ClusterTabViewModel Tab()
    {
        var tab = TestObjects.Tab();
        foreach (var ns in new[] { "payments", "checkout", "ledger" })
        {
            tab.NamespaceOptions.Add(ns);
        }

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        return tab;
    }

    [Test]
    public async Task One_namespace_hides_the_column_and_several_or_all_show_it()
    {
        var tab = Tab();

        tab.SetNamespaces([]);
        await Assert.That(tab.IsNamespaceColumnShown).IsTrue();

        tab.SetNamespaces(["payments"]);
        await Assert.That(tab.IsNamespaceColumnShown).IsFalse();

        tab.SetNamespaces(["payments", "checkout"]);
        await Assert.That(tab.IsNamespaceColumnShown).IsTrue();

        // A namespace set on its own (the palette, a reveal, a restore) is one namespace.
        tab.SelectedNamespace = "ledger";
        await Assert.That(tab.IsNamespaceColumnShown).IsFalse();
    }

    [Test]
    public async Task A_fleet_list_in_one_namespace_hides_it_too()
    {
        // The chosen namespace is read on every cluster, so every row is still in it; the
        // Cluster column is what tells a fleet list's rows apart.
        var tab = Tab();
        tab.SetNamespaces(["payments"]);
        tab.IsFleetView = true;

        await Assert.That(tab.IsNamespaceColumnShown).IsFalse();
    }

    [Test]
    public async Task A_cluster_scoped_kind_never_shows_it_and_no_kind_yet_does()
    {
        var tab = Tab();
        tab.SetNamespaces([]);
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.NodeDescriptor, "cluster");
        await Assert.That(tab.IsNamespaceColumnShown).IsFalse();

        tab.SelectedKind = null;
        await Assert.That(tab.IsNamespaceColumnShown).IsTrue();
    }
}
