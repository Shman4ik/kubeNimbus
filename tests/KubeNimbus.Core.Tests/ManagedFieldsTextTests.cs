using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #270: the YAML editor hides <c>metadata.managedFields</c> by default and puts it back on
/// request. These pin the text operations behind that: only the top-level metadata key is
/// touched, hiding then showing gives back the server's text exactly, and text the helper
/// cannot read is left as it was.
/// </summary>
public class ManagedFieldsTextTests
{
    private const string Deployment = """
        {"apiVersion":"apps/v1","kind":"Deployment",
         "metadata":{"name":"web","namespace":"shop","uid":"1","resourceVersion":"42",
           "labels":{"app":"web"},
           "managedFields":[{"manager":"kubectl","operation":"Apply","apiVersion":"apps/v1",
             "fieldsType":"FieldsV1","fieldsV1":{"f:spec":{"f:replicas":{}}}},
             {"manager":"k3s","operation":"Update","subresource":"status",
             "fieldsV1":{"f:status":{"f:conditions":{"k:{\"type\":\"Available\"}":{".":{}}}}}}],
           "annotations":{"deployment.kubernetes.io/revision":"1"}},
         "spec":{"replicas":2}}
        """;

    private static string Yaml(string json)
    {
        using var document = JsonDocument.Parse(json);
        return YamlJson.ToYamlString(document.RootElement);
    }

    [Test]
    public async Task Hiding_removes_only_the_metadata_key_and_showing_gives_back_the_same_text()
    {
        var server = Yaml(Deployment);

        var hid = ManagedFieldsText.TryHide(server, out var hidden, out var block);

        await Assert.That(hid).IsTrue();
        await Assert.That(hidden).DoesNotContain("managedFields");
        await Assert.That(hidden).DoesNotContain("f:replicas");
        await Assert.That(hidden).Contains("replicas: 2");
        await Assert.That(hidden).Contains("deployment.kubernetes.io/revision: \"1\"");

        // The field sat between labels and annotations, and goes back there.
        var shown = ManagedFieldsText.TryShow(hidden, block!, out var restored);
        await Assert.That(shown).IsTrue();
        await Assert.That(restored).IsEqualTo(server);
    }

    [Test]
    public async Task Showing_keeps_an_edit_made_while_the_field_was_hidden()
    {
        ManagedFieldsText.TryHide(Yaml(Deployment), out var hidden, out var block);
        var edited = hidden.Replace("replicas: 2", "replicas: 5", StringComparison.Ordinal);

        await Assert.That(ManagedFieldsText.TryShow(edited, block!, out var restored)).IsTrue();

        var json = YamlJson.ParseYamlToJson(restored)!;
        await Assert.That(json["spec"]!["replicas"]!.GetValue<long>()).IsEqualTo(5L);
        await Assert.That(json["metadata"]!["managedFields"]!.AsArray().Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_managed_fields_key_inside_a_value_is_never_touched()
    {
        var configMap = Yaml("""
            {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"manifests"},
             "data":{"deployment.yaml":"metadata:\n  managedFields: []\n"},
             "spec":{"metadata":{"managedFields":"not the object's"}}}
            """);

        await Assert.That(ManagedFieldsText.TryHide(configMap, out var hidden, out var block)).IsFalse();
        await Assert.That(hidden).IsEqualTo(configMap);
        await Assert.That(block).IsNull();
    }

    [Test]
    [Arguments("metadata: {name: web\nspec: [")]
    [Arguments("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  managedFields: []\n---\napiVersion: v1\nkind: ConfigMap\n")]
    public async Task Text_that_is_not_one_readable_document_is_left_as_it_is(string yaml)
    {
        await Assert.That(ManagedFieldsText.TryHide(yaml, out var hidden, out _)).IsFalse();
        await Assert.That(hidden).IsEqualTo(yaml);

        var block = new ManagedFieldsBlock("- manager: kubectl\n", 0);
        await Assert.That(ManagedFieldsText.TryShow(yaml, block, out var shown)).IsFalse();
        await Assert.That(shown).IsEqualTo(yaml);
    }

    [Test]
    public async Task Showing_twice_does_not_add_a_second_copy()
    {
        var server = Yaml(Deployment);
        ManagedFieldsText.TryHide(server, out _, out var block);

        await Assert.That(ManagedFieldsText.TryShow(server, block!, out var shown)).IsFalse();
        await Assert.That(shown).IsEqualTo(server);
    }
}
