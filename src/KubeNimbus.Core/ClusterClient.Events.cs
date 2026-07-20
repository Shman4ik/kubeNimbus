namespace KubeNimbus.Core;

/// <summary>
/// Events-for-a-resource and owner-reference navigation (pod → replicaset →
/// deployment). Owner lookup needs a plural name for an ownerReference's bare
/// Kind, which only discovery knows — the catalog is fetched once per
/// connection and cached, since it rarely changes mid-session.
/// </summary>
public sealed partial class ClusterClient
{
    private IReadOnlyList<ResourceDescriptor>? _resourceCatalog;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);

    /// <summary>Discovery catalog, fetched once and cached for the life of this connection.</summary>
    public async Task<IReadOnlyList<ResourceDescriptor>> GetResourceCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_resourceCatalog is { } cached)
        {
            return cached;
        }

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _resourceCatalog ??= await DiscoverResourcesAsync(cancellationToken).ConfigureAwait(false);
            return _resourceCatalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>
    /// Events involving one resource (core/v1 Events, involvedObject match),
    /// newest first.
    /// </summary>
    public async Task<IReadOnlyList<DynamicResource>> GetEventsForAsync(
        DynamicResource target, CancellationToken cancellationToken = default)
    {
        var selector = target.Namespace is { } ns
            ? $"involvedObject.name={target.Name},involvedObject.namespace={ns}"
            : $"involvedObject.name={target.Name}";

        var events = await ListResourceOnceAsync(
            ResourceDescriptor.Events, target.Namespace, selector, cancellationToken).ConfigureAwait(false);

        return [.. events.OrderByDescending(e => e.LastTimestamp() ?? DateTimeOffset.MinValue)];
    }

    /// <summary>Resolves an ownerReference to the actual object, or null if it's gone or unresolvable.</summary>
    public async Task<DynamicResource?> ResolveOwnerAsync(
        OwnerRef owner, string? namespaceHint, CancellationToken cancellationToken = default)
    {
        var catalog = await GetResourceCatalogAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = catalog.FirstOrDefault(d =>
            string.Equals(d.ApiVersion, owner.ApiVersion, StringComparison.Ordinal)
            && string.Equals(d.Kind, owner.Kind, StringComparison.Ordinal));

        if (descriptor is null)
        {
            return null;
        }

        return await ReadResourceAsync(descriptor, descriptor.Namespaced ? namespaceHint : null, owner.Name, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Field readers for core/v1 Event objects fetched as <see cref="DynamicResource"/>.</summary>
public static class EventFields
{
    public static string Type(this DynamicResource e) => e.Raw.TryGetProperty("type", out var v) ? v.GetString() ?? "" : "";

    public static string Reason(this DynamicResource e) => e.Raw.TryGetProperty("reason", out var v) ? v.GetString() ?? "" : "";

    public static string Message(this DynamicResource e) => e.Raw.TryGetProperty("message", out var v) ? v.GetString() ?? "" : "";

    public static int Count(this DynamicResource e) => e.Raw.TryGetProperty("count", out var v) && v.TryGetInt32(out var i) ? i : 0;

    public static DateTimeOffset? LastTimestamp(this DynamicResource e)
    {
        if (e.Raw.TryGetProperty("lastTimestamp", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out var dt))
        {
            return dt;
        }

        return e.CreationTimestamp;
    }

    /// <summary>
    /// The object this event is about, e.g. the pod a "BackOff" event fired on —
    /// reuses <see cref="OwnerRef"/> since the shape (apiVersion/kind/name/uid)
    /// is identical to an ownerReference, letting callers navigate to it through
    /// the same resolve-and-open path as owner-chip navigation.
    /// </summary>
    public static OwnerRef? InvolvedObject(this DynamicResource e)
    {
        if (!e.Raw.TryGetProperty("involvedObject", out var io) || io.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        var kind = io.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        var name = io.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (kind.Length == 0 || name.Length == 0)
        {
            return null;
        }

        return new OwnerRef(
            ApiVersion: io.TryGetProperty("apiVersion", out var av) ? av.GetString() ?? "" : "",
            Kind: kind,
            Name: name,
            Uid: io.TryGetProperty("uid", out var u) ? u.GetString() : null,
            Controller: false);
    }

    /// <summary>The involved object's own namespace (may differ from the event's), when set.</summary>
    public static string? InvolvedObjectNamespace(this DynamicResource e) =>
        e.Raw.TryGetProperty("involvedObject", out var io) && io.ValueKind == System.Text.Json.JsonValueKind.Object
        && io.TryGetProperty("namespace", out var ns)
            ? ns.GetString()
            : null;
}
