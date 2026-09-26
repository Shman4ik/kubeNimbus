using System.Globalization;
using System.Text.Json;

namespace KubeNimbus.Core.Applications;

/// <summary>
/// The small set of tolerant <see cref="JsonElement"/> readers the Applications rules are
/// written in. Every reader answers "absent" for a missing property, a property of the wrong
/// JSON type and an owner that is not an object alike, because the objects these rules read
/// come from clusters of every version and from CRDs nobody here has seen: a rule that threw
/// on a shape it did not expect would take the whole list down with it.
/// </summary>
internal static class J
{
    public static JsonElement Obj(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    public static JsonElement Obj(JsonElement owner, params string[] path)
    {
        var current = owner;
        foreach (var name in path)
        {
            current = Obj(current, name);
        }

        return current;
    }

    public static IReadOnlyList<JsonElement> Arr(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : [];

    public static string Str(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static int? Int(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    public static bool Bool(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    public static bool Has(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>
    /// A timestamp field. <c>metav1.Time</c>'s zero value (<c>0001-01-01T00:00:00Z</c>) is
    /// "unset", not a moment two thousand years ago — the same reading EventFields applies.
    /// </summary>
    public static DateTimeOffset? Time(JsonElement owner, string name)
    {
        var text = Str(owner, name);
        if (text.Length == 0
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return null;
        }

        return value.Year <= 1 ? null : value;
    }

    /// <summary>A timestamp as the field holds it, which is what an evidence chip quotes.</summary>
    public static string Iso(DateTimeOffset? value) =>
        value is { } v ? v.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";

    /// <summary>The first condition of <paramref name="type"/> in <c>status.conditions</c>, or default.</summary>
    public static JsonElement Condition(JsonElement status, string type)
    {
        foreach (var condition in Arr(status, "conditions"))
        {
            if (Str(condition, "type") == type)
            {
                return condition;
            }
        }

        return default;
    }
}
