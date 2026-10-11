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

    private static Task<ResourceDescriptor?> Resolve(string group, string kind) =>
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
