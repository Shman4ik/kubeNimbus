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

    /// <summary>
    /// #362: a CRD printer column is <c>1*</c>, and the QA run's drag from 111 to 151 px moved
    /// it to about <c>1.36*</c>. The release compared both shapes against half a unit, so the
    /// ratio's change counted as no change and no CRD column width was ever stored.
    /// </summary>
    [Test]
    public async Task A_drag_is_measured_in_the_columns_own_unit()
    {
        var star = new GridColumnWidth(GridColumnWidth.Star, 1);
        var pixels = new GridColumnWidth(GridColumnWidth.Pixels, 111);

        await Assert.That(new GridColumnWidth(GridColumnWidth.Star, 1.3061).DiffersFrom(star)).IsTrue();
        await Assert.That(new GridColumnWidth(GridColumnWidth.Star, 1.0004).DiffersFrom(star)).IsFalse();
        await Assert.That(new GridColumnWidth(GridColumnWidth.Pixels, 151).DiffersFrom(pixels)).IsTrue();
        await Assert.That(new GridColumnWidth(GridColumnWidth.Pixels, 111.3).DiffersFrom(pixels)).IsFalse();
        await Assert.That(new GridColumnWidth(GridColumnWidth.Pixels, 1).DiffersFrom(star)).IsTrue();
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

    private static string WorkspaceFile => Path.Combine(WorkspaceStore.DirectoryOverride!, "workspace.json");

    /// <summary>
    /// #318: a layout is stored under one field for hidden columns and one for widths. The
    /// computed <c>Hidden</c> and <c>Widths</c> views were serialized beside the fields they
    /// read, so every layout held both. Mutation-checked by removing the JsonIgnore.
    /// </summary>
    [Test]
    public async Task A_layout_is_written_once_per_field()
    {
        TestObjects.RedirectStores();

        GridLayoutStore.Update(PodKey, l => l with
        {
            ColumnWidths = new Dictionary<string, GridColumnWidth> { [ResourceColumn.Name] = new(GridColumnWidth.Pixels, 240) },
            HiddenColumns = [ResourceColumn.Restarts],
        });

        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(WorkspaceFile));
        var layout = json.RootElement.GetProperty("GridLayouts").GetProperty(PodKey);
        var names = layout.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        await Assert.That(string.Join(",", names)).IsEqualTo("ColumnWidths,HiddenColumns,SortColumn,SortDescending");

        var back = GridLayoutStore.Load(PodKey);
        await Assert.That(back.Hidden.Single()).IsEqualTo(ResourceColumn.Restarts);
        await Assert.That(back.Widths[ResourceColumn.Name].Value).IsEqualTo(240);
    }

    /// <summary>#318: a file an older build wrote, with both spellings, still loads from the real fields.</summary>
    [Test]
    public async Task A_file_with_both_spellings_still_loads()
    {
        TestObjects.RedirectStores();
        await File.WriteAllTextAsync(WorkspaceFile, """
            {"Theme":null,"Tabs":[],"IsAdvancedView":false,"GridLayouts":{"POD_KEY":{
              "ColumnWidths":{"Name":{"Unit":"px","Value":240}},"SortColumn":null,"SortDescending":false,
              "HiddenColumns":["IP"],"Widths":{"Name":{"Unit":"px","Value":240}},"Hidden":["IP"],"IsDefault":false}}}
            """.Replace("POD_KEY", PodKey, StringComparison.Ordinal));

        var layout = GridLayoutStore.Load(PodKey);

        await Assert.That(layout.Hidden.Single()).IsEqualTo("IP");
        await Assert.That(layout.Widths["Name"].Value).IsEqualTo(240);
    }

    /// <summary>
    /// #318: the Advanced view lives in settings.json only. <c>Normalize</c> wrote the legacy
    /// workspace property as <c>false</c> on every save, so a fresh profile's workspace.json
    /// contradicted the sidebar's toggle. Mutation-checked by restoring the property.
    /// </summary>
    [Test]
    public async Task The_advanced_view_is_never_written_to_the_workspace_and_the_shell_shows_the_stored_preference()
    {
        TestObjects.RedirectStores();
        await File.WriteAllTextAsync(WorkspaceFile, """{"Theme":null,"Tabs":[],"IsAdvancedView":false}""");
        App.Update(s => s with { IsAdvancedView = true });

        WorkspaceStore.Save(WorkspaceStore.Load());
        var shell = new MainWindowViewModel();
        shell.IsAdvancedView = false;
        shell.IsAdvancedView = true;

        await Assert.That(await File.ReadAllTextAsync(WorkspaceFile)).DoesNotContain("IsAdvancedView");
        await Assert.That(new MainWindowViewModel().IsAdvancedView).IsTrue();
        App.Update(s => s with { IsAdvancedView = false });
        await Assert.That(new MainWindowViewModel().IsAdvancedView).IsFalse();
    }
}
