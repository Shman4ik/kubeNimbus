using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-12, the paste-or-type slice: New YAML… opens an empty editor that applies through the
/// same preview and server-side apply as any edit, aimed at whatever its text names, with a
/// namespaced object that names none sent to the tab's namespace. It never replaces an editor
/// that is open (UI rule 5). <c>[NotInParallel]</c>: the editor reads "Preview before
/// applying" from <c>settings.json</c>.
/// </summary>
[NotInParallel]
public class NewYamlTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private const string ConfigMapYaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: settings
        data:
          mode: fast
        """;

    private const string AppliedConfigMap = """
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"settings","namespace":"team-a","resourceVersion":"7"},"data":{"mode":"fast"}}
        """;

    private static Task<ResourceDescriptor?> Resolve(string group, string kind, CancellationToken cancellationToken) =>
        Task.FromResult(group == "" && kind == "ConfigMap" ? TestObjects.ConfigMapDescriptor : null);

    private static YamlEditorTabViewModel NewEditor(ClusterClient client, string yaml) =>
        new(client, Resolve, "team-a", 1) { YamlText = yaml };

    [Test]
    public async Task A_manifest_without_a_namespace_is_previewed_then_created_in_the_default_one()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", "/api/v1/namespaces/team-a/configmaps/settings", HttpStatusCode.OK, AppliedConfigMap);
        var editor = NewEditor(server.Connect(), ConfigMapYaml);

        await editor.ApplyCommand.ExecuteAsync(null);

        // Review first: a dry run against the resolved path, and nothing created yet.
        await Assert.That(editor.PendingPreview).IsNotNull();
        await Assert.That(editor.PendingPreview!.IsCreate).IsTrue();
        var dryRun = server.Requests.Single(r => r.Method == "PATCH");
        await Assert.That(dryRun.Path).IsEqualTo("/api/v1/namespaces/team-a/configmaps/settings");
        await Assert.That(dryRun.Query).Contains("dryRun=All");

        await editor.ConfirmPreviewCommand.ExecuteAsync(null);

        var applied = server.Requests.Where(r => r.Method == "PATCH").Last();
        await Assert.That(applied.Query).DoesNotContain("dryRun");
        await Assert.That(editor.Title).IsEqualTo("ConfigMap/settings");
        await Assert.That(editor.NewDocumentNotice).IsNull();
        await Assert.That(editor.ReloadCommand.CanExecute(null)).IsTrue();
        await Assert.That(editor.StatusMessage).StartsWith("Applied at");
    }

    [Test]
    public async Task The_manifests_own_namespace_wins_over_the_default()
    {
        using var server = new StubApiServer();
        var editor = NewEditor(server.Connect(), ConfigMapYaml.Replace("name: settings", "name: settings\n  namespace: other", StringComparison.Ordinal));

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(server.Requests.Single(r => r.Method == "PATCH").Path)
            .IsEqualTo("/api/v1/namespaces/other/configmaps/settings");
    }

    [Test]
    public async Task A_kind_this_cluster_does_not_serve_is_refused_before_anything_is_sent()
    {
        using var server = new StubApiServer();
        var editor = NewEditor(server.Connect(), """
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: w
            """);

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(server.Requests.Count).IsEqualTo(0);
        await Assert.That(editor.StatusMessage).IsEqualTo(
            "Nothing was applied: this cluster serves no Widget in API group example.com. Check apiVersion: and kind:, or install the CRD that defines it.");
    }

    [Test]
    public async Task An_empty_editor_says_so_and_cannot_reload_or_delete()
    {
        using var server = new StubApiServer();
        var editor = NewEditor(server.Connect(), "");

        await Assert.That(editor.NewDocumentNotice).IsNotNull();
        await Assert.That(editor.ReloadCommand.CanExecute(null)).IsFalse();
        await Assert.That(editor.RequestDeleteCommand.CanExecute(null)).IsFalse();

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(server.Requests.Count).IsEqualTo(0);
        await Assert.That(editor.StatusMessage).StartsWith("Nothing was applied: the editor is empty.");
    }

    /// <summary>UI rule 5: each New YAML is its own tab, and an editor already open stays open.</summary>
    [Test]
    public async Task Each_new_document_is_its_own_tab_and_never_replaces_an_open_editor()
    {
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config"),
        };
        tab.Client = TestObjects.OfflineClient();
        tab.SetNamespaces(["team-a"]);
        var existing = new YamlEditorTabViewModel(tab.Client, TestObjects.ConfigMapDescriptor, "team-a", "x", "kind: ConfigMap");
        tab.InspectorTabs.Add(existing);

        await Assert.That(tab.CanCreateFromYaml).IsTrue();
        await Assert.That(tab.NewObjectNamespace).IsEqualTo("team-a");
        tab.NewYamlCommand.Execute(null);
        tab.NewYamlCommand.Execute(null);

        var editors = tab.InspectorTabs.OfType<YamlEditorTabViewModel>().ToList();
        await Assert.That(editors.Count).IsEqualTo(3);
        await Assert.That(editors[0]).IsSameReferenceAs(existing);
        await Assert.That(editors.Select(e => e.Key).Distinct().Count()).IsEqualTo(3);
        await Assert.That(editors[1].Title).IsEqualTo("New YAML");
        await Assert.That(editors[2].Title).IsEqualTo("New YAML 2");
        await Assert.That(editors[2].IsPreview).IsFalse();
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(editors[2]);
        await Assert.That(editors[2].DefaultNamespace).IsEqualTo("team-a");
    }

    /// <summary>
    /// #342 (hard rule 2): the press reads the cluster's kinds through the tab's real resolver,
    /// and closing the editor while that read is held in flight stops it — the apply returns,
    /// nothing is sent and no "could not be read" is said to a closed tab. Mutation-checked by
    /// dropping the token from the catalog read: the held discovery request then keeps the
    /// apply waiting past the timeout.
    /// </summary>
    [Test]
    public async Task Closing_the_editor_cancels_the_catalog_read_its_apply_started()
    {
        using var server = new StubApiServer { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config"),
        };
        tab.Client = server.Connect();
        tab.NewYamlCommand.Execute(null);
        var editor = tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Single();
        editor.YamlText = ConfigMapYaml;

        var apply = editor.ApplyCommand.ExecuteAsync(null);
        await server.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await tab.CloseInspectorTabCommand.ExecuteAsync(editor);

        await apply.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.Requests.Any(r => r.Method == "PATCH")).IsFalse();
        await Assert.That(editor.StatusMessage).IsNull();
        await Assert.That(tab.InspectorTabs.Contains(editor)).IsFalse();
    }

    /// <summary>
    /// #300: after its apply a New YAML editor is that object's editor, so E on the object's row
    /// focuses it rather than opening a second one (UI rule 5). Mutation-checked by leaving the
    /// key at <c>yaml:new:1</c>: E then adds a second editor.
    /// </summary>
    [Test]
    public async Task After_the_apply_E_on_the_created_object_focuses_the_new_document()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", "/api/v1/namespaces/team-a/configmaps/settings", HttpStatusCode.OK, AppliedConfigMap);
        var client = server.Connect();
        var editor = NewEditor(client, ConfigMapYaml);
        await editor.ApplyCommand.ExecuteAsync(null);
        await editor.ConfirmPreviewCommand.ExecuteAsync(null);

        await Assert.That(editor.Key)
            .IsEqualTo(YamlEditorTabViewModel.KeyFor("", TestObjects.ConfigMapDescriptor, "team-a", "settings"));

        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            SelectedKind = new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config"),
        };
        tab.Apply(TestObjects.Added(TestObjects.ConfigMap("team-a", "settings")));
        tab.Client = client;
        tab.InspectorTabs.Add(editor);
        tab.SelectedRow = tab.Rows.Single();

        tab.EditSelectedYamlCommand.Execute(null);

        await Assert.That(tab.InspectorTabs.OfType<YamlEditorTabViewModel>().Count()).IsEqualTo(1);
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(editor);
    }

    /// <summary>
    /// #300: the editor E opens applies its first document only, so a second one pasted below
    /// is refused with the reason before anything is sent — with the preview on and with it off.
    /// Mutation-checked by removing the check from the apply path.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_opened_object_refuses_a_second_pasted_document(bool previewApplies)
    {
        App.Update(s => s with { PreviewApplies = previewApplies });
        using var server = new StubApiServer();
        var editor = new YamlEditorTabViewModel(server.Connect(), TestObjects.ConfigMapDescriptor, "team-a", "settings", ConfigMapYaml)
        {
            YamlText = ConfigMapYaml + "\n---\n" + ConfigMapYaml.Replace("name: settings", "name: other", StringComparison.Ordinal),
        };

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(server.Requests.Count).IsEqualTo(0);
        await Assert.That(editor.StatusMessage).IsEqualTo(YamlEditorTabViewModel.MultipleDocumentsMessage);
        await Assert.That(editor.PendingPreview).IsNull();
    }

    [Test]
    public async Task There_is_no_new_document_without_an_api_server()
    {
        var tab = new ClusterTabViewModel(TestObjects.Context);
        await Assert.That(tab.CanCreateFromYaml).IsFalse();
        await Assert.That(tab.NewYamlCommand.CanExecute(null)).IsFalse();

        // All namespaces: the context's own namespace is the default, as kubectl's is.
        tab.Client = TestObjects.OfflineClient();
        await Assert.That(tab.NewYamlCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.NewObjectNamespace).IsEqualTo("default");
    }
}
