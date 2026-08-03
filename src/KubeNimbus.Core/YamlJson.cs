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
        JsonValueKind.String => StringToYaml(element.GetString()),
        JsonValueKind.Number => new YamlScalarNode(element.GetRawText()),
        JsonValueKind.True => new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        JsonValueKind.False => new YamlScalarNode("false") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        JsonValueKind.Null or JsonValueKind.Undefined => new YamlScalarNode("null") { Style = YamlDotNet.Core.ScalarStyle.Plain },
        _ => new YamlScalarNode(element.GetRawText()),
    };

    /// <summary>
    /// A JSON string has to come back as a string. YamlDotNet's emitter only
    /// promotes a plain scalar to a quoted one when plain would be
    /// <em>syntactically</em> invalid, never when it would merely change type —
    /// so <c>"1"</c>, <c>"false"</c> and <c>"1E50"</c> used to be emitted bare
    /// and read back as a number, a bool and a double. That is not cosmetic:
    /// Kubernetes types annotations and labels as <c>map[string]string</c> and
    /// every Deployment carries <c>deployment.kubernetes.io/revision: "1"</c>,
    /// so opening one and pressing Apply with no edits produced a body the API
    /// server rejects — and the YAML on screen was wrong for anyone copying it
    /// into <c>kubectl apply</c>.
    /// Only type-ambiguous values are quoted (see <see cref="NeedsQuoting"/>);
    /// quoting every string would also be correct but turns each manifest into
    /// <c>name: "nginx"</c>, which is not what anyone expects to read.
    /// </summary>
    private static YamlScalarNode StringToYaml(string? value)
    {
        var text = value ?? "";
        return NeedsQuoting(text)
            ? new YamlScalarNode(text) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted }
            : new YamlScalarNode(text);
    }

    /// <summary>
    /// True when emitting <paramref name="value"/> as a plain scalar would let a
    /// YAML resolver read it back as something other than a string.
    /// </summary>
    /// <remarks>
    /// The set is deliberately wider than <see cref="ScalarToJson"/>'s own
    /// inference: this YAML is not only re-read by us, it is copied out and fed
    /// to <c>kubectl</c>, whose Go decoder still implements YAML 1.1 — where
    /// <c>yes</c>/<c>off</c> are booleans, <c>0755</c> is octal and
    /// <c>2024-01-01</c> is a timestamp. Over-quoting costs a pair of quotes;
    /// under-quoting silently changes the value's type on the wire, so anything
    /// ambiguous is quoted.
    /// </remarks>
    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
        {
            // A plain empty scalar resolves to null, not "".
            return true;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            // A plain scalar cannot carry edge whitespace; say so here rather
            // than leaving it to the emitter's own analysis.
            return true;
        }

        return NullLiterals.Contains(value)
            || BooleanLiterals.Contains(value)
            || LooksNumeric(value)
            || LooksLikeTimestamp(value);
    }

    /// <summary>The core schema's null spellings (plus YAML's empty scalar, handled separately).</summary>
    private static readonly HashSet<string> NullLiterals =
        new(StringComparer.Ordinal) { "~", "null", "Null", "NULL" };

    /// <summary>
    /// YAML 1.1's boolean spellings, which is what the API server's Go decoder
    /// implements — <c>yes</c>, <c>off</c> and bare <c>y</c>/<c>n</c> included,
    /// not just the core schema's true/false.
    /// </summary>
    private static readonly HashSet<string> BooleanLiterals = new(StringComparer.Ordinal)
    {
        "y", "Y", "yes", "Yes", "YES",
        "n", "N", "no", "No", "NO",
        "true", "True", "TRUE",
        "false", "False", "FALSE",
        "on", "On", "ON",
        "off", "Off", "OFF",
    };

    private static bool LooksNumeric(string value)
    {
        // Exactly the two probes ScalarToJson uses, so whatever it would re-type
        // is guaranteed to be quoted — that is what makes the round trip lossless.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        // Forms .NET's parsers reject but a YAML resolver accepts: based
        // integers (0x1f, 0o755, 0b1010) and the infinity/NaN spellings.
        var body = value[0] is '-' or '+' ? value.AsSpan(1) : value.AsSpan();
        if (body.Length > 2 && body[0] == '0' && body[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            return true;
        }

        return body.Equals(".inf", StringComparison.OrdinalIgnoreCase)
            || body.Equals(".nan", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches the leading <c>yyyy-m-d</c> of YAML 1.1's timestamp resolver. Only
    /// the date part is tested — anything shaped that way is already ambiguous
    /// enough to quote, and a decoder that retypes it hands the API server
    /// <c>2024-01-01T00:00:00Z</c> where the manifest said <c>2024-01-01</c>.
    /// </summary>
    private static bool LooksLikeTimestamp(string value)
    {
        var span = value.AsSpan();
        if (span.Length < 8 || span[4] != '-' || !AllAsciiDigits(span[..4]))
        {
            return false;
        }

        span = span[5..];
        var month = LeadingAsciiDigits(span);
        if (month is < 1 or > 2 || span.Length <= month || span[month] != '-')
        {
            return false;
        }

        span = span[(month + 1)..];
        var day = LeadingAsciiDigits(span);
        if (day is < 1 or > 2)
        {
            return false;
        }

        span = span[day..];
        return span.IsEmpty || span[0] is 'T' or 't' or ' ';
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static int LeadingAsciiDigits(ReadOnlySpan<char> span)
    {
        var count = 0;
        while (count < span.Length && char.IsAsciiDigit(span[count]))
        {
            count++;
        }

        return count;
    }

    private static YamlMappingNode ObjectToYaml(JsonElement element)
    {
        var map = new YamlMappingNode();
        foreach (var prop in element.EnumerateObject())
        {
            // Keys get the same treatment as values: a ConfigMap key of "8080"
            // or "null" is a string in JSON and has to stay one in YAML.
            map.Add(StringToYaml(prop.Name), ToYaml(prop.Value));
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
