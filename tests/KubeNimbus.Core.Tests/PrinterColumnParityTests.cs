using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Two places a real API server's printer-column rendering disagreed with the app's,
/// found by <c>Live/PrinterColumnsLiveTests</c> (VER-23) and pinned here so they hold on a
/// machine with no cluster: a non-scalar in a <c>string</c> column is printed as JSON, and
/// a backslash-escaped dot is how kubectl's JSONPath reaches a key containing one.
/// </summary>
public class PrinterColumnParityTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Route = Json("""
        {
          "metadata": {
            "name": "shop",
            "labels": { "app.kubernetes.io/name": "checkout" },
            "annotations": { "crossplane.io/external-name": "db-prod-7" }
          },
          "spec": {
            "hostnames": [ "shop.example.com", "www.shop.example.com" ],
            "parentRefs": [ { "name": "gateway" } ]
          }
        }
        """);

    /// <summary>
    /// kubectl prints Gateway API's HTTPRoute Hostnames column (<c>type: string</c>,
    /// <c>.spec.hostnames</c>) as <c>["shop.example.com","www.shop.example.com"]</c>. The
    /// app used to print an empty cell for any array or object.
    /// </summary>
    [Test]
    [Arguments(".spec.hostnames", """["shop.example.com","www.shop.example.com"]""")]
    [Arguments(".spec.parentRefs[0]", """{"name":"gateway"}""")]
    public async Task Evaluate_prints_a_non_scalar_as_kubectl_does(string path, string expected)
    {
        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", "string", path), Route)).IsEqualTo(expected);
    }

    /// <summary>For every other type the server emits a null cell for a non-scalar, and so does the app.</summary>
    [Test]
    [Arguments("integer")]
    [Arguments("number")]
    [Arguments("boolean")]
    [Arguments("date")]
    public async Task A_non_scalar_in_a_non_string_column_is_still_empty(string type)
    {
        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", type, ".spec.hostnames"), Route)).IsEqualTo("");
    }

    /// <summary>The JSON is one line even when the object came from an indented document.</summary>
    [Test]
    public async Task A_non_scalar_is_rendered_on_one_line()
    {
        var cell = PrinterColumns.Evaluate(new PrinterColumn("C", "string", ".spec"), Route);
        await Assert.That(cell).DoesNotContain("\n");
        await Assert.That(cell).StartsWith("{\"hostnames\":[");
    }

    /// <summary>
    /// <c>.metadata.annotations.crossplane\.io/external-name</c> is Crossplane's own
    /// EXTERNAL-NAME column; kubectl resolves the escaped form, and the app now does too.
    /// </summary>
    [Test]
    [Arguments(@".metadata.labels.app\.kubernetes\.io/name", "checkout")]
    [Arguments(@".metadata.annotations.crossplane\.io/external-name", "db-prod-7")]
    [Arguments(@"metadata.annotations.crossplane\.io/external-name", "db-prod-7")]
    public async Task An_escaped_dot_is_part_of_the_key(string path, string expected)
    {
        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", "string", path), Route)).IsEqualTo(expected);
    }
}
