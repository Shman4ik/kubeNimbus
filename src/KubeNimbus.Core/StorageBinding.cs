using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The other end of a PersistentVolume ↔ PersistentVolumeClaim binding (FEAT-47): a claim
/// names its volume in <c>spec.volumeName</c>, and a volume names its claim in
/// <c>spec.claimRef</c>. Both are read off the object, which is where kubectl's own VOLUME
/// and CLAIM columns come from.
/// </summary>
public static class StorageBinding
{
    /// <summary>
    /// The object this one is bound to, or null when it is bound to nothing — an unbound
    /// claim (Pending) or a volume nobody has claimed (Available). A Released volume still
    /// names the claim it was bound to, and that claim may be gone; opening it then reports
    /// that it could not be found, which is the honest answer.
    /// </summary>
    public static BoundObject? For(ResourceDescriptor descriptor, DynamicResource resource)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.Raw.ValueKind != JsonValueKind.Object
            || !resource.Raw.TryGetProperty("spec", out var spec)
            || spec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return descriptor switch
        {
            { Group: "", Kind: "PersistentVolumeClaim" } =>
                Str(spec, "volumeName") is { Length: > 0 } volume
                    ? new BoundObject("PersistentVolume", null, volume)
                    : null,
            { Group: "", Kind: "PersistentVolume" } =>
                spec.TryGetProperty("claimRef", out var claim) && claim.ValueKind == JsonValueKind.Object
                && Str(claim, "name") is { Length: > 0 } claimName
                && Str(claim, "kind") is "" or "PersistentVolumeClaim"
                    ? new BoundObject("PersistentVolumeClaim", Str(claim, "namespace"), claimName)
                    : null,
            _ => null,
        };
    }

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>
/// One end of a storage binding: a core/v1 kind, its namespace (null for the
/// cluster-scoped PersistentVolume) and its name.
/// </summary>
public sealed record BoundObject(string Kind, string? Namespace, string Name)
{
    /// <summary>"volume pvc-…" or "claim payments/data-0" — how kubectl's columns name the other end.</summary>
    public string Description => Namespace is { Length: > 0 } ns
        ? $"claim {ns}/{Name}"
        : Kind == "PersistentVolume" ? $"volume {Name}" : $"claim {Name}";
}
