using System.Text.Json;
using System.Text.Json.Nodes;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Round-trip tests (no cluster needed) for the converter behind the YAML editor
/// and server-side apply.
/// </summary>
/// <remarks>
/// The failure these pin is silent and expensive: a plain YAML scalar is
/// re-typed by the resolver on the way back, so a string of <c>"1"</c> came home
/// as the number 1. Kubernetes types annotations and labels as
/// <c>map[string]string</c> and every Deployment carries
/// <c>deployment.kubernetes.io/revision: "1"</c> — which made "open a Deployment,
/// press Apply, change nothing" produce a body the API server must reject.
/// </remarks>
public class YamlJsonTests
{
    private static JsonNode RoundTrip(string json)
    {
        using var document = JsonDocument.Parse(json);
        var yaml = YamlJson.ToYamlString(document.RootElement);
        return YamlJson.ParseYamlToJson(yaml) ?? throw new InvalidOperationException($"Empty YAML document:\n{yaml}");
    }

    [Test]
    [Arguments("1")]          // the annotation every Deployment carries
    [Arguments("false")]
    [Arguments("true")]
    [Arguments("null")]
    [Arguments("~")]
    [Arguments("8080")]
    [Arguments("1.5")]
    [Arguments("1E50")]       // parsed as a double and re-emitted as 1E+50
    [Arguments("0755")]       // a file mode, not the octal 493
    [Arguments("2024-01-01")] // YAML 1.1 resolves this to a timestamp
    [Arguments("")]           // a plain empty scalar is null, not ""
    [Arguments("nginx")]      // and an unambiguous string must stay unquoted-clean
    public async Task String_values_come_back_as_strings(string value)
    {
        var result = RoundTrip(new JsonObject { ["value"] = value }.ToJsonString())["value"]!;

        await Assert.That(result.GetValueKind()).IsEqualTo(JsonValueKind.String);
        await Assert.That(result.GetValue<string>()).IsEqualTo(value);
    }

    [Test]
    [Arguments("8080")]
    [Arguments("true")]
    [Arguments("null")]
    public async Task Map_keys_come_back_as_strings(string key)
    {
        // ConfigMap/Secret data keys are arbitrary strings; a plain "8080" key
        // is an integer key to a YAML resolver.
        var result = RoundTrip(new JsonObject { [key] = "value" }.ToJsonString());

        await Assert.That(result[key]!.GetValue<string>()).IsEqualTo("value");
    }

    [Test]
    public async Task Numbers_booleans_and_null_keep_their_own_types()
    {
        const string json = """{"replicas":3,"ratio":1.5,"paused":false,"enabled":true,"selector":null}""";

        var result = RoundTrip(json);

        await Assert.That(result["replicas"]!.GetValueKind()).IsEqualTo(JsonValueKind.Number);
        await Assert.That(result["replicas"]!.GetValue<long>()).IsEqualTo(3L);
        await Assert.That(result["ratio"]!.GetValueKind()).IsEqualTo(JsonValueKind.Number);
        await Assert.That(result["ratio"]!.GetValue<double>()).IsEqualTo(1.5d);
        await Assert.That(result["paused"]!.GetValueKind()).IsEqualTo(JsonValueKind.False);
        await Assert.That(result["enabled"]!.GetValueKind()).IsEqualTo(JsonValueKind.True);
        await Assert.That(result["selector"]).IsNull();
    }

    [Test]
    public async Task A_deployments_own_annotations_survive_a_no_op_apply()
    {
        const string json = """
            {"metadata":{"annotations":{"deployment.kubernetes.io/revision":"1","sidecar.istio.io/inject":"false"}}}
            """;

        using var document = JsonDocument.Parse(json);
        var yaml = YamlJson.ToYamlString(document.RootElement);
        var annotations = YamlJson.ParseYamlToJson(yaml)!["metadata"]!["annotations"]!;

        // The text on screen has to be right too — it gets copied into kubectl.
        await Assert.That(yaml).Contains("\"1\"");
        await Assert.That(yaml).Contains("\"false\"");
        await Assert.That(annotations["deployment.kubernetes.io/revision"]!.GetValue<string>()).IsEqualTo("1");
        await Assert.That(annotations["sidecar.istio.io/inject"]!.GetValue<string>()).IsEqualTo("false");
    }

    [Test]
    public async Task Unambiguous_strings_are_left_unquoted()
    {
        const string json = """{"image":"nginx:1.27-alpine","app.kubernetes.io/name":"checkout"}""";

        using var document = JsonDocument.Parse(json);
        var yaml = YamlJson.ToYamlString(document.RootElement);

        // Quoting everything would round-trip correctly too; it would also turn
        // every manifest into name: "nginx", which is not what this file is for.
        await Assert.That(yaml).Contains("checkout");
        await Assert.That(yaml).DoesNotContain("\"checkout\"");
    }

    [Test]
    public async Task Nested_lists_and_objects_round_trip()
    {
        const string json = """
            {"spec":{"containers":[{"name":"web","ports":[{"containerPort":80}],"args":["--port","8080"]}]}}
            """;

        var container = RoundTrip(json)["spec"]!["containers"]![0]!;

        await Assert.That(container["name"]!.GetValue<string>()).IsEqualTo("web");
        await Assert.That(container["ports"]![0]!["containerPort"]!.GetValue<long>()).IsEqualTo(80L);
        await Assert.That(container["args"]![1]!.GetValueKind()).IsEqualTo(JsonValueKind.String);
        await Assert.That(container["args"]![1]!.GetValue<string>()).IsEqualTo("8080");
    }
}
