using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace KubeNimbus.Core;

/// <summary>
/// YAML ⇄ JSON conversion for the YAML editor and server-side apply, using
/// YamlDotNet's structural <see cref="YamlNode"/> tree (RepresentationModel)
/// rather than its attribute/reflection-based object (de)serializer — the
/// latter is not NativeAOT/trim-safe and CRD shapes aren't known at compile
/// time anyway, so everything here stays a plain node-to-node walk.
/// </summary>
public static class YamlJson
{
    /// <summary>Parses YAML text (one document) into a JSON node tree, or null for an empty document.</summary>
    public static JsonNode? ParseYamlToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0)
        {
            return null;
        }

        return ToJson(stream.Documents[0].RootNode);
    }

    /// <summary>Renders a JSON element as YAML text.</summary>
    public static string ToYamlString(JsonElement element)
    {
        var yamlDoc = new YamlDocument(ToYaml(element));
        var stream = new YamlStream(yamlDoc);
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => MapToJson(map),
        YamlSequenceNode seq => SeqToJson(seq),
        YamlScalarNode scalar => ScalarToJson(scalar),
        _ => null,
    };

    private static JsonObject MapToJson(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (keyNode, valueNode) in map.Children)
        {
            var key = keyNode is YamlScalarNode s ? s.Value ?? "" : keyNode.ToString();
            obj[key] = ToJson(valueNode);
        }

        return obj;
    }

    private static JsonArray SeqToJson(YamlSequenceNode seq)
    {
        var arr = new JsonArray();
        foreach (var item in seq.Children)
        {
            arr.Add(ToJson(item));
        }

        return arr;
    }

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var value = scalar.Value;

        // Only plain (unquoted) scalars get YAML 1.1-ish type inference; quoted
        // strings ("true", "123") must stay strings, matching kubectl/yaml
        // parsers used against the Kubernetes API.
        if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain || value is null)
        {
            return value;
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return true;
        }

        if (value is "false" or "False" or "FALSE")
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return value;
    }

    private static YamlNode ToYaml(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ObjectToYaml(element),
        JsonValueKind.Array => ArrayToYaml(element),
        JsonValueKind.String => new YamlScalarNode(element.GetString()),
        JsonValueKind.Number => new YamlScalarNode(element.GetRawText()),
        JsonValueKind.True => new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        JsonValueKind.False => new YamlScalarNode("false") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        JsonValueKind.Null or JsonValueKind.Undefined => new YamlScalarNode("null") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        _ => new YamlScalarNode(element.GetRawText()),
    };

    private static YamlMappingNode ObjectToYaml(JsonElement element)
    {
        var map = new YamlMappingNode();
        foreach (var prop in element.EnumerateObject())
        {
            map.Add(new YamlScalarNode(prop.Name), ToYaml(prop.Value));
        }

        return map;
    }

    private static YamlSequenceNode ArrayToYaml(JsonElement element)
    {
        var seq = new YamlSequenceNode();
        foreach (var item in element.EnumerateArray())
        {
            seq.Add(ToYaml(item));
        }

        return seq;
    }
}
