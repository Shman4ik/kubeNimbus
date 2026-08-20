using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The per-kind grid layout — dragged column widths and the sort — round-tripping
/// through <c>workspace.json</c>.
///
/// <para>
/// The thing being pinned is that the two halves of one record have two different
/// writers. The view owns the widths (pixels are not view-model state) and the view
/// model owns the sort, and each writes through <see cref="GridLayoutStore.Update"/>
/// without reading the other's half — so a read-modify-write that dropped what it did
/// not know about would make sorting a list silently reset the widths somebody had
/// dragged, and vice versa. That is the same failure the settings store's rule 1 is
/// about, one level down.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> because the stored layout is a real file behind a
/// process-global <c>WorkspaceStore.DirectoryOverride</c>: another test redirecting
/// that override mid-test would move the file out from under these assertions, which
/// is exactly what it did before this attribute was here.
/// </para>
/// </summary>
[NotInParallel]
public class GridLayoutStoreTests
{
    private static string PodKey => GridLayoutStore.KeyFor(TestObjects.PodDescriptor);

    [Test]
    public async Task A_kind_with_no_stored_layout_reads_as_empty()
    {
        TestObjects.RedirectStores();

        var layout = GridLayoutStore.Load(PodKey);

        await Assert.That(layout.SortColumn).IsNull();
        await Assert.That(layout.Widths.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Widths_and_sort_round_trip()
    {
        TestObjects.RedirectStores();

        GridLayoutStore.Update(PodKey, layout => layout with
        {
            ColumnWidths = new Dictionary<string, GridColumnWidth>
            {
                [ResourceColumn.Name] = new(GridColumnWidth.Star, 3.5),
                [ResourceColumn.Namespace] = new(GridColumnWidth.Pixels, 96),
            },
        });
        GridLayoutStore.Update(PodKey, layout => layout with { SortColumn = ResourceColumn.Age, SortDescending = true });

        var stored = GridLayoutStore.Load(PodKey);

        await Assert.That(stored.SortColumn).IsEqualTo(ResourceColumn.Age);
        await Assert.That(stored.SortDescending).IsTrue();
        await Assert.That(stored.Widths[ResourceColumn.Name].Unit).IsEqualTo(GridColumnWidth.Star);
        await Assert.That(stored.Widths[ResourceColumn.Name].Value).IsEqualTo(3.5);
        await Assert.That(stored.Widths[ResourceColumn.Namespace].Value).IsEqualTo(96);
    }

    /// <summary>
    /// The two-writer case, stated as a test: recording a sort must not lose the widths
    /// the view recorded, and recording a width must not lose the sort.
    /// </summary>
    [Test]
    public async Task One_writer_does_not_drop_the_other_writers_half()
    {
        TestObjects.RedirectStores();

        GridLayoutStore.Update(PodKey, l => l with { SortColumn = ResourceColumn.Status });
        GridLayoutStore.Update(PodKey, l => l with
        {
            ColumnWidths = new Dictionary<string, GridColumnWidth> { [ResourceColumn.Name] = new(GridColumnWidth.Pixels, 240) },
        });

        var stored = GridLayoutStore.Load(PodKey);

        await Assert.That(stored.SortColumn).IsEqualTo(ResourceColumn.Status);
        await Assert.That(stored.Widths[ResourceColumn.Name].Value).IsEqualTo(240);
    }

    /// <summary>A layout is per kind: two kinds keyed off the same file do not see each
    /// other's columns.</summary>
    [Test]
    public async Task Layouts_are_kept_per_kind()
    {
        TestObjects.RedirectStores();
        var configMaps = GridLayoutStore.KeyFor(TestObjects.ConfigMapDescriptor);

        GridLayoutStore.Update(PodKey, l => l with { SortColumn = ResourceColumn.Restarts });
        GridLayoutStore.Update(configMaps, l => l with { SortColumn = ResourceColumn.Name });

        await Assert.That(GridLayoutStore.Load(PodKey).SortColumn).IsEqualTo(ResourceColumn.Restarts);
        await Assert.That(GridLayoutStore.Load(configMaps).SortColumn).IsEqualTo(ResourceColumn.Name);
    }

    /// <summary>
    /// The key is the API group and Kind, never the version: a cluster promoting a CRD
    /// from v1beta1 to v1 is still the same list to whoever widened its Name column.
    /// </summary>
    [Test]
    public async Task The_key_ignores_the_served_version()
    {
        var beta = new ResourceDescriptor("shop.kubenimbus.io", "v1beta1", "Widget", "widgets", "widget",
            Namespaced: true, ShortNames: [], Categories: []);
        var stable = beta with { Version = "v1" };

        await Assert.That(GridLayoutStore.KeyFor(stable)).IsEqualTo(GridLayoutStore.KeyFor(beta));
    }

    /// <summary>
    /// A layout with nothing in it is removed rather than stored: the file is a record
    /// of the choices somebody made, and clearing a sort on a kind nobody has resized
    /// leaves none.
    /// </summary>
    [Test]
    public async Task A_layout_back_at_its_defaults_is_forgotten()
    {
        TestObjects.RedirectStores();

        GridLayoutStore.Update(PodKey, l => l with { SortColumn = ResourceColumn.Name });
        GridLayoutStore.Update(PodKey, l => l with { SortColumn = null });

        await Assert.That(WorkspaceStore.Load().GridLayouts!.ContainsKey(PodKey)).IsFalse();
    }

    /// <summary>
    /// Storing a layout must not disturb the rest of the workspace — it shares a file
    /// with the open tabs and the pinned contexts, and every writer there reads the file
    /// back before writing.
    /// </summary>
    [Test]
    public async Task Storing_a_layout_leaves_the_rest_of_the_workspace_alone()
    {
        TestObjects.RedirectStores();
        WorkspaceStore.Save(WorkspaceStore.Load() with
        {
            Tabs = [new TabSnapshot("prod-payments", "/home/someone/.kube/config")],
            PinnedContexts = ["prod-payments"],
        });

        GridLayoutStore.Update(PodKey, l => l with { SortColumn = ResourceColumn.Name });

        var settings = WorkspaceStore.Load();
        await Assert.That(settings.Tabs.Single().ContextName).IsEqualTo("prod-payments");
        await Assert.That(settings.PinnedContexts!.Single()).IsEqualTo("prod-payments");
    }
}
