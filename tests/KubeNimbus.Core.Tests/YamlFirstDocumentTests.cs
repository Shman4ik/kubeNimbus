using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="YamlJson.FirstDocument"/>: what an editor opened on a local file shows (#301,
/// FEAT-12). An editor applies one object, so a multi-document file opens on its first one and
/// says how many there were; the cut is the parser's, never a split on <c>---</c> lines.
/// </summary>
public class YamlFirstDocumentTests
{
    [Test]
    public async Task A_single_document_comes_back_as_written_with_its_comments()
    {
        const string yaml = "# the web tier\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: web # inline\n";

        var (text, count) = YamlJson.FirstDocument(yaml);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(text).IsEqualTo(yaml);
    }

    [Test]
    public async Task A_multi_document_file_gives_its_first_document_and_the_count()
    {
        const string yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: one\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: two\n---\napiVersion: v1\nkind: Secret\nmetadata:\n  name: three\n";

        var (text, count) = YamlJson.FirstDocument(yaml);

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(text).IsEqualTo("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: one\n");
        await Assert.That(YamlJson.CountDocuments(text)).IsEqualTo(1);
    }

    [Test]
    public async Task Leading_separators_and_empty_documents_are_skipped_and_not_counted()
    {
        const string yaml = "---\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: one\n---\n";

        var (text, count) = YamlJson.FirstDocument(yaml);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(YamlJson.ParseYamlToJson(text)!["metadata"]!["name"]!.GetValue<string>()).IsEqualTo("one");
        await Assert.That(YamlJson.CountDocuments(text)).IsEqualTo(1);
    }

    /// <summary>A <c>---</c> line inside a block scalar is data, not a separator.</summary>
    [Test]
    public async Task A_separator_inside_a_block_scalar_does_not_split_the_document()
    {
        const string yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: embedded\ndata:\n  file.yaml: |\n    a: 1\n    ---\n    b: 2\n";

        var (text, count) = YamlJson.FirstDocument(yaml);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(text).IsEqualTo(yaml);
    }

    [Test]
    public async Task Text_that_does_not_parse_comes_back_whole()
    {
        const string yaml = "apiVersion: v1\nkind: [unclosed\n";

        var (text, count) = YamlJson.FirstDocument(yaml);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(text).IsEqualTo(yaml);
    }
}
