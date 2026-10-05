using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The header sort of the Helm release browser and the Argo CD dashboard: the default order
/// each opened in, columns compared by meaning, and a sort that survives a reload. Driven
/// through a demo tab, so the lists are loaded by the same code a cluster's are.
/// </summary>
public class HelmArgoSortTests
{
    private static ClusterTabViewModel Tab(Func<SidebarKindViewModel, bool> kind)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(kind));
        return tab;
    }

    private static void Reload(ClusterTabViewModel tab, Func<SidebarKindViewModel, bool> kind)
    {
        var kinds = tab.SidebarSections.SelectMany(s => s.Kinds).ToList();
        tab.SelectKindCommand.Execute(kinds.First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SelectKindCommand.Execute(kinds.First(kind));
    }

    [Test]
    public async Task The_Argo_dashboard_opens_most_urgent_first_and_keeps_a_header_sort_through_a_reload()
    {
        static bool Argo(SidebarKindViewModel k) => k.IsArgoDashboard;
        var tab = Tab(Argo);
        var apps = tab.ArgoApplications;
        await Assert.That(apps.Count).IsGreaterThan(2);
        await Assert.That(apps.Select(ArgoApplicationRowViewModel.Rank)).IsOrderedBy(r => r);

        tab.ArgoSort.Toggle(ArgoApplicationComparer.Name);
        await Assert.That(apps.Select(a => a.Name)).IsOrderedBy(n => n, StringComparer.OrdinalIgnoreCase);

        Reload(tab, Argo);
        await Assert.That(tab.ArgoSort.Column).IsEqualTo(ArgoApplicationComparer.Name);
        await Assert.That(tab.ArgoApplications.Select(a => a.Name)).IsOrderedBy(n => n, StringComparer.OrdinalIgnoreCase);

        tab.ArgoSort.Toggle(ArgoApplicationComparer.Sync);
        await Assert.That(tab.ArgoApplications[0].Application.Sync).IsEqualTo(ArgoSyncState.OutOfSync);
    }

    [Test]
    public async Task Helm_releases_sort_by_revision_as_a_number_and_by_update_time()
    {
        static bool Helm(SidebarKindViewModel k) => k.IsHelmReleases;
        var tab = Tab(Helm);
        var releases = tab.HelmReleases;
        await Assert.That(releases.Count).IsGreaterThan(1);
        await Assert.That(releases.Select(r => $"{r.Namespace}/{r.Name}")).IsOrderedBy(k => k, StringComparer.Ordinal);

        tab.HelmSort.Toggle(HelmReleaseComparer.Revision);
        tab.HelmSort.Toggle(HelmReleaseComparer.Revision);
        await Assert.That(releases.Select(r => r.Revision)).IsOrderedByDescending(r => r);

        tab.HelmSort.Toggle(HelmReleaseComparer.Updated);
        var dated = releases.Where(r => r.Updated is not null).Select(r => r.Updated!.Value).ToList();
        await Assert.That(dated).IsOrderedBy(d => d);

        tab.HelmSort.Toggle(HelmReleaseComparer.Updated);
        tab.HelmSort.Toggle(HelmReleaseComparer.Updated);
        await Assert.That(tab.HelmSort.Column).IsNull();
        await Assert.That(releases.Select(r => $"{r.Namespace}/{r.Name}")).IsOrderedBy(k => k, StringComparer.Ordinal);
    }
}
