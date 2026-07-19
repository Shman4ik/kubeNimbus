using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>An ownerReference entry, enough for owner-chain navigation (pod → replicaset → deployment).</summary>
public sealed record OwnerRef(string ApiVersion, string Kind, string Name, string? Uid, bool Controller);

/// <summary>
/// Any Kubernetes object (built-in or CRD), kept as its raw JSON — CRD shapes
/// aren't known at compile time so there's no source-generated POCO for them.
/// <see cref="Raw"/> is a detached clone (safe to keep past the JsonDocument
/// that produced it, e.g. inside a watch loop that disposes per line).
/// </summary>
public sealed class DynamicResource
{
    public JsonElement Raw { get; }

    public DynamicResource(JsonElement raw) => Raw = raw;

    public string Kind => Raw.TryGetProperty("kind", out var v) ? v.GetString() ?? "" : "";
    public string ApiVersion => Raw.TryGetProperty("apiVersion", out var v) ? v.GetString() ?? "" : "";

    private JsonElement Metadata => Raw.TryGetProperty("metadata", out var m) ? m : default;

    public string Name => Metadata.ValueKind == JsonValueKind.Object && Metadata.TryGetProperty("name", out var v)
        ? v.GetString() ?? ""
        : "";

    public string? Namespace => Metadata.ValueKind == JsonValueKind.Object && Metadata.TryGetProperty("namespace", out var v)
        ? v.GetString()
        : null;

    public string? ResourceVersion => Metadata.ValueKind == JsonValueKind.Object && Metadata.TryGetProperty("resourceVersion", out var v)
        ? v.GetString()
        : null;

    public string? Uid => Metadata.ValueKind == JsonValueKind.Object && Metadata.TryGetProperty("uid", out var v)
        ? v.GetString()
        : null;

    public DateTimeOffset? CreationTimestamp =>
        Metadata.ValueKind == JsonValueKind.Object
        && Metadata.TryGetProperty("creationTimestamp", out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var dt)
            ? dt
            : null;

    public string Key => $"{Namespace}/{Name}";

    public IReadOnlyDictionary<string, string> Labels => ReadStringMap("labels");

    public IReadOnlyDictionary<string, string> Annotations => ReadStringMap("annotations");

    private IReadOnlyDictionary<string, string> ReadStringMap(string property)
    {
        if (Metadata.ValueKind != JsonValueKind.Object || !Metadata.TryGetProperty(property, out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            return EmptyMap;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in map.EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetString() ?? "";
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<OwnerRef> OwnerReferences
    {
        get
        {
            if (Metadata.ValueKind != JsonValueKind.Object
                || !Metadata.TryGetProperty("ownerReferences", out var refs)
                || refs.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<OwnerRef>();
            foreach (var r in refs.EnumerateArray())
            {
                result.Add(new OwnerRef(
                    ApiVersion: r.TryGetProperty("apiVersion", out var av) ? av.GetString() ?? "" : "",
                    Kind: r.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    Name: r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Uid: r.TryGetProperty("uid", out var u) ? u.GetString() : null,
                    Controller: r.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.True));
            }

            return result;
        }
    }

    /// <summary>Pretty-printed YAML for the editor — round-trips through <see cref="YamlJson"/>.</summary>
    public string ToYaml() => YamlJson.ToYamlString(Raw);
}
