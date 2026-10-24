using System.Net;
using System.Text;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #301, FEAT-12's local-file slice: Open a YAML file… fills the New YAML editor from a picked
/// file and applies it through the same Review and server-side apply. A multi-document file
/// opens on its first document, and the InfoBar says how many were left out before anything
/// is sent. <c>[NotInParallel]</c>: the editor reads "Preview before applying" from
/// <c>settings.json</c>.
/// </summary>
[NotInParallel]
public class OpenYamlFileTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private const string ConfigMap = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\ndata:\n  mode: fast\n";

    private const string ThreeDocuments = ConfigMap + "---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: two\n"
        + "---\napiVersion: v1\nkind: Secret\nmetadata:\n  name: three\n";

    private static ClusterTabViewModel ConnectedTab(Func<Task<PickedYamlFile?>> pick)
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config"),
        };
        tab.Client = TestObjects.OfflineClient();
        tab.SetNamespaces(["team-a"]);
        tab.PickYamlFile = pick;
        return tab;
    }

    /// <summary>
    /// The central check, mutation-checked: with <c>LoadFromFile</c> setting the whole file's
    /// text, the editor held three documents and the notice did not say so.
    /// </summary>
    [Test]
    public async Task A_multi_document_file_opens_on_its_first_document_and_says_so()
    {
        var tab = ConnectedTab(() => Task.FromResult<PickedYamlFile?>(
            new("stack.yaml", ThreeDocuments.Replace("\n", "\r\n", StringComparison.Ordinal))));

        await tab.OpenYamlFileCommand.ExecuteAsync(null);

        var editor = tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Single();
        await Assert.That(editor.IsNewDocument).IsTrue();
        await Assert.That(editor.Title).IsEqualTo("stack.yaml");
        await Assert.That(editor.YamlText).IsEqualTo(ConfigMap);
        await Assert.That(editor.NewDocumentNotice).StartsWith(
            "stack.yaml holds 3 documents. Only the first is open here, because an editor applies one object; "
            + "the other 2 were not opened and nothing was applied.");
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(editor);
    }

    [Test]
    public async Task A_single_document_file_names_the_file_and_where_it_applies()
    {
        var tab = ConnectedTab(() => Task.FromResult<PickedYamlFile?>(new("settings.yaml", ConfigMap)));

        await tab.OpenYamlFileCommand.ExecuteAsync(null);

        var editor = tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Single();
        await Assert.That(editor.YamlText).IsEqualTo(ConfigMap);
        await Assert.That(editor.NewDocumentNotice).StartsWith("Opened from settings.yaml. ");
        await Assert.That(editor.NewDocumentNotice).Contains("goes to team-a");
    }

    [Test]
    public async Task A_dismissed_picker_opens_nothing()
    {
        var tab = ConnectedTab(() => Task.FromResult<PickedYamlFile?>(null));

        await tab.OpenYamlFileCommand.ExecuteAsync(null);

        await Assert.That(tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Any()).IsFalse();
    }

    [Test]
    public async Task A_file_too_large_or_not_utf8_is_refused_in_the_status_line()
    {
        var big = new MemoryStream(new byte[ClusterTabViewModel.MaxYamlFileBytes + 1]);
        var tab = ConnectedTab(async () => new("huge.yaml", await ClusterTabViewModel.ReadYamlTextAsync("huge.yaml", big)));

        await tab.OpenYamlFileCommand.ExecuteAsync(null);

        await Assert.That(tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Any()).IsFalse();
        await Assert.That(tab.Status).IsEqualTo("huge.yaml was not opened: it is over 4 MiB, larger than any one object a cluster stores.");

        var latin1 = new MemoryStream([0x6B, 0x69, 0x6E, 0x64, 0x3A, 0x20, 0xE9, 0x0A]);
        tab.PickYamlFile = async () => new("latin1.yaml", await ClusterTabViewModel.ReadYamlTextAsync("latin1.yaml", latin1));
        await tab.OpenYamlFileCommand.ExecuteAsync(null);
        await Assert.That(tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Any()).IsFalse();
        await Assert.That(tab.Status).StartsWith("The file was not opened: ");
    }

    [Test]
    public async Task A_byte_order_mark_is_not_part_of_the_text()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(ConfigMap)).ToArray();

        var text = await ClusterTabViewModel.ReadYamlTextAsync("bom.yaml", new MemoryStream(bytes));

        await Assert.That(text).IsEqualTo(ConfigMap);
    }

    /// <summary>The file's document goes through the same Review as a pasted one: a dry run first, nothing created.</summary>
    [Test]
    public async Task An_opened_file_is_reviewed_before_it_is_applied()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", "/api/v1/namespaces/team-a/configmaps/settings", HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"settings","namespace":"team-a","resourceVersion":"7"},"data":{"mode":"fast"}}""");
        var editor = new YamlEditorTabViewModel(
            server.Connect(),
            (group, kind, _) => Task.FromResult(group == "" && kind == "ConfigMap" ? TestObjects.ConfigMapDescriptor : null),
            "team-a",
            1);
        editor.LoadFromFile("stack.yaml", ThreeDocuments);

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(editor.PendingPreview).IsNotNull();
        var dryRun = server.Requests.Single(r => r.Method == "PATCH");
        await Assert.That(dryRun.Path).IsEqualTo("/api/v1/namespaces/team-a/configmaps/settings");
        await Assert.That(dryRun.Query).Contains("dryRun=All");
    }

    [Test]
    public async Task Not_on_the_demo_cluster()
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);

        await Assert.That(tab.OpenYamlFileCommand.CanExecute(null)).IsFalse();
    }
}
