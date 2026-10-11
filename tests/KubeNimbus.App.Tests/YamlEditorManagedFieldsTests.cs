using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #270 at the editor: <c>metadata.managedFields</c> is out of the text when a tab opens, the
/// chip that shows it is offered only when there is something to show, and moving the field in
/// or out is not an edit and never loses one.
/// </summary>
public class YamlEditorManagedFieldsTests
{
    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: [], Categories: []);

    private const string ServerYaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: web
          namespace: shop
          managedFields:
          - manager: kubectl
            operation: Apply
            fieldsV1:
              f:spec:
                f:replicas: {}
        spec:
          replicas: 2

        """;

    private static YamlEditorTabViewModel Editor(string yaml = ServerYaml)
    {
        TestObjects.RedirectStores();
        return new YamlEditorTabViewModel(client: null, Deployments, "shop", "web", yaml);
    }

    [Test]
    public async Task A_tab_opens_without_the_managed_fields_and_offers_to_show_them()
    {
        var editor = Editor();

        await Assert.That(editor.YamlText).DoesNotContain("managedFields");
        await Assert.That(editor.YamlText).DoesNotContain("f:replicas");
        await Assert.That(editor.YamlText).Contains("replicas: 2");
        await Assert.That(editor.HasManagedFields).IsTrue();
        await Assert.That(editor.ShowManagedFields).IsFalse();
        await Assert.That(editor.IsDirty).IsFalse();
    }

    [Test]
    public async Task Showing_and_hiding_is_not_an_edit_and_keeps_one()
    {
        var editor = Editor();

        editor.ShowManagedFields = true;
        await Assert.That(editor.YamlText).Contains("managedFields");
        await Assert.That(editor.YamlText).Contains("f:replicas");
        await Assert.That(editor.IsDirty).IsFalse();

        editor.YamlText = editor.YamlText.Replace("replicas: 2", "replicas: 5", StringComparison.Ordinal);
        editor.ShowManagedFields = false;

        await Assert.That(editor.YamlText).DoesNotContain("managedFields");
        await Assert.That(editor.YamlText).Contains("replicas: 5");
        await Assert.That(editor.IsDirty).IsTrue();

        editor.ShowManagedFields = true;
        await Assert.That(editor.YamlText).Contains("f:replicas");
        await Assert.That(editor.YamlText).Contains("replicas: 5");
    }

    [Test]
    public async Task A_toggle_over_text_that_does_not_parse_says_why_and_stays_put()
    {
        var editor = Editor();
        editor.YamlText = "metadata: {name: web\nspec: [";

        editor.ShowManagedFields = true;

        await Assert.That(editor.ShowManagedFields).IsFalse();
        await Assert.That(editor.YamlText).IsEqualTo("metadata: {name: web\nspec: [");
        await Assert.That(editor.StatusMessage).Contains("one YAML document");
    }

    [Test]
    public async Task An_object_without_managed_fields_has_no_chip()
    {
        var editor = Editor("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\n");

        await Assert.That(editor.HasManagedFields).IsFalse();
        await Assert.That(editor.YamlText).IsEqualTo("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\n");
    }
}
