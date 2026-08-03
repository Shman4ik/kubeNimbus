using System.Buffers;
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

    /// <summary>
    /// Materializes one object out of a <c>*List</c> response, restoring the
    /// <c>kind</c>/<c>apiVersion</c> the API server leaves off list items.
    /// </summary>
    /// <remarks>
    /// A list's <c>items[]</c> entries carry no type identity — it is implied by
    /// the enclosing <c>PodList</c> — while watch frames carry it on every
    /// object. Left as-is that made a row's origin observable: the pod list's
    /// Status column fell back to <c>Ready: True</c> because the App's status
    /// summary gates its Pod branch on <c>Kind == "Pod"</c>, and
    /// <see cref="ToYaml"/> emitted a document that <c>kubectl apply -f</c>
    /// refuses. Injecting into <see cref="Raw"/> (rather than papering over it
    /// in the properties) fixes YAML and every other consumer at once, and the
    /// two fields lead so the YAML reads the way a manifest is written. Only
    /// what is missing is filled in: an item that already declares its type is
    /// cloned untouched, which is both cheaper and keeps the server's own
    /// property order.
    /// </remarks>
    public static DynamicResource FromListItem(JsonElement item, ResourceDescriptor descriptor)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (HasNonEmptyString(item, "kind") && HasNonEmptyString(item, "apiVersion")))
        {
            return new DynamicResource(item.Clone());
        }

        var kind = HasNonEmptyString(item, "kind") ? item.GetProperty("kind").GetString()! : descriptor.Kind;
        var apiVersion = HasNonEmptyString(item, "apiVersion")
            ? item.GetProperty("apiVersion").GetString()!
            : descriptor.ApiVersion;

        // Utf8JsonWriter + JsonElement.WriteTo copies the subtrees verbatim with
        // no reflection and no intermediate model — this runs per item on pages
        // of up to 500.
        var buffer = new ArrayBufferWriter<byte>(RewriteBufferSize);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", apiVersion);
            writer.WriteString("kind", kind);
            foreach (var property in item.EnumerateObject())
            {
                if (property.NameEquals("apiVersion") || property.NameEquals("kind"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        // Clone() detaches the element from the document backing this buffer —
        // the same "safe past the JsonDocument that produced it" guarantee the
        // watch path relies on.
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>
    /// Starting capacity for the rewrite buffer. A typical Kubernetes object is
    /// a few KB, so this is sized to cover most items in one allocation without
    /// measuring the source (which would mean materializing its raw text first).
    /// </summary>
    private const int RewriteBufferSize = 4096;

    private static bool HasNonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(value.GetString());

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
