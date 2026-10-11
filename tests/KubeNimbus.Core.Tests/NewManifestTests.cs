namespace KubeNimbus.Core.Tests;

/// <summary>
/// What a manifest typed or pasted into a blank editor says it is (FEAT-12), and the one
/// sentence the editor prints when it cannot say. Each refusal here would otherwise reach the
/// API server as a request built from a guess — the first document of several, an empty
/// name — and come back as an error that reads like a cluster fault.
/// </summary>
public class NewManifestTests
{
    [Test]
    public async Task A_core_object_reads_its_kind_name_and_namespace()
    {
        var identity = NewManifest.Read("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
              namespace: team-a
            """, out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(identity).IsEqualTo(new ManifestIdentity("", "v1", "ConfigMap", "settings", "team-a"));
        await Assert.That(identity!.ApiVersion).IsEqualTo("v1");
    }

    [Test]
    public async Task A_grouped_apiVersion_splits_into_group_and_version_and_the_namespace_is_optional()
    {
        var identity = NewManifest.Read("""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            spec: {}
            """, out _);

        await Assert.That(identity).IsEqualTo(new ManifestIdentity("apps", "v1", "Deployment", "web", null));
    }

    /// <summary>
    /// A pasted multi-document file. The parser reads the first document only, so without
    /// this check the first object would be applied and the rest dropped without a word.
    /// </summary>
    [Test]
    public async Task Several_documents_are_refused_rather_than_applying_the_first()
    {
        var identity = NewManifest.Read("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: a
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: b
            """, out var problem);

        await Assert.That(identity).IsNull();
        await Assert.That(problem).Contains("more than one document");
    }

    [Test]
    public async Task A_leading_separator_is_still_one_document()
    {
        var identity = NewManifest.Read("""
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: a
            """, out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(identity!.Name).IsEqualTo("a");
    }

    [Test]
    [Arguments("", "the editor is empty")]
    [Arguments("   \n", "the editor is empty")]
    [Arguments("kind: [", "isn't valid YAML")]
    [Arguments("- a\n- b", "a manifest is one object")]
    [Arguments("kind: ConfigMap\nmetadata:\n  name: a", "needs apiVersion: and kind:")]
    [Arguments("apiVersion: v1\nmetadata:\n  name: a", "needs apiVersion: and kind:")]
    [Arguments("apiVersion: v1\nkind: ConfigMap", "needs metadata.name")]
    [Arguments("apiVersion: v1\nkind: Pod\nmetadata:\n  generateName: web-", "cannot use generateName")]
    [Arguments("apiVersion: v1\nkind: List\nitems: []", "a List holds several objects")]
    [Arguments("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: ..", "cannot be an object's name")]
    [Arguments("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: a\n  namespace: a/b", "cannot be a namespace")]
    [Arguments("apiVersion: a/b/c\nkind: ConfigMap\nmetadata:\n  name: a", "is not an apiVersion")]
    public async Task Each_refusal_names_what_is_missing(string yaml, string expected)
    {
        var identity = NewManifest.Read(yaml, out var problem);

        await Assert.That(identity).IsNull();
        await Assert.That(problem).StartsWith("Nothing was applied:");
        await Assert.That(problem).Contains(expected);
    }
}
