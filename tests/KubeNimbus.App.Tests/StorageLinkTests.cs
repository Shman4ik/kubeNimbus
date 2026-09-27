using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-47 in the list: a bound claim opens its volume and a bound volume its claim, from
/// the row's menu, through the navigation owner chips use — and the Details column names
/// the other end, as kubectl's VOLUME and CLAIM columns do. On the demo cluster, which ships
/// a bound pair and a volume nothing has claimed.
/// </summary>
public class StorageLinkTests
{
    private static ClusterTabViewModel DemoKind(string kind)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "" } d && d.Kind == kind));
        return tab;
    }

    [Test]
    public async Task A_bound_claim_names_and_opens_its_volume()
    {
        var tab = DemoKind("PersistentVolumeClaim");
        var claim = tab.Rows.Single(r => r.Name == "data-redis-cache-0");
        tab.SelectedRow = claim;

        await Assert.That(claim.Details).Contains("volume pvc-f1111111-1111-1111-1111-111111111111");
        await Assert.That(tab.BoundObjectLabel).IsEqualTo("Open volume pvc-f1111111-1111-1111-1111-111111111111");

        await tab.OpenBoundObjectCommand.ExecuteAsync(null);

        var opened = tab.SelectedInspectorTab;
        await Assert.That(opened).IsTypeOf<YamlEditorTabViewModel>();
        await Assert.That(opened!.Title).Contains("pvc-f1111111-1111-1111-1111-111111111111");
    }

    [Test]
    public async Task A_bound_volume_names_and_opens_its_claim()
    {
        var tab = DemoKind("PersistentVolume");
        var volume = tab.Rows.Single(r => r.Name == "pvc-f1111111-1111-1111-1111-111111111111");
        tab.SelectedRow = volume;

        await Assert.That(volume.Details).Contains("claim payments/data-redis-cache-0");
        await Assert.That(tab.BoundObjectLabel).IsEqualTo("Open claim payments/data-redis-cache-0");

        await tab.OpenBoundObjectCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab!.Title).Contains("data-redis-cache-0");
    }

    [Test]
    public async Task A_volume_nothing_has_claimed_offers_no_link()
    {
        var tab = DemoKind("PersistentVolume");
        tab.SelectedRow = tab.Rows.Single(r => r.Name == "nfs-archive-01");

        await Assert.That(tab.CanOpenBoundObject).IsFalse();
        await Assert.That(tab.BoundObjectLabel).IsNull();
        await Assert.That(tab.OpenBoundObjectCommand.CanExecute(null)).IsFalse();
    }
}
