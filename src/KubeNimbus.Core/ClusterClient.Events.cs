using System.Globalization;
using System.Text.Json;

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
    internal string? DiscoveryCacheDirectory { get; set; }
    private readonly SemaphoreSlim _catalogLock = new(1, 1);

    /// <summary>Discovery catalog, fetched once and cached for the life of this connection.</summary>
    public async Task<IReadOnlyList<ResourceDescriptor>> GetResourceCatalogAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (!forceRefresh && _resourceCatalog is { } cached)
        {
            return cached;
        }

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _resourceCatalog is not null) return _resourceCatalog;
            var cache = new DiscoveryCache(DiscoveryCacheDirectory);
            // The endpoint is essential; context/user also separates discovery filtered by a proxy or RBAC.
            var identity = $"{_client.BaseUri.AbsoluteUri}\n{Context.Name}\n{Context.UserName}\n{Context.KubeconfigPath}";
            if (forceRefresh) cache.Invalidate(identity);
            if (!forceRefresh && _serverVersion is { } version)
                _resourceCatalog = await cache.ReadAsync(identity, version, cancellationToken).ConfigureAwait(false);
            if (forceRefresh || _resourceCatalog is null)
            {
                _resourceCatalog = await DiscoverResourcesAsync(cancellationToken).ConfigureAwait(false);
                if (_discoveryComplete && _serverVersion is { } freshVersion)
                    await cache.WriteAsync(identity, freshVersion, _resourceCatalog, cancellationToken).ConfigureAwait(false);
            }
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
        var events = await ListResourceOnceAsync(
            ResourceDescriptor.Events, target.Namespace, EventSelectorFor(target), cancellationToken).ConfigureAwait(false);

        return [.. events.OrderByDescending(e => e.LastTimestamp() ?? DateTimeOffset.MinValue)];
    }

    /// <summary>
    /// The <c>fieldSelector</c> that finds the events about <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// The object's UID is the precise key and is used wherever it can be trusted — it
    /// keeps a recreated pod from inheriting its predecessor's events. A <b>Node</b> is
    /// the exception: the kubelet writes its own node events (<c>Starting</c>,
    /// <c>NodeReady</c>, <c>Rebooted</c>, the pressure transitions) with
    /// <c>involvedObject.uid</c> set to the node's <em>name</em>, not its UID, while the
    /// node controller uses the real UID. Selecting on either UID drops half of them, so
    /// a node is matched on kind and name — which is also what <c>kubectl describe node</c>
    /// does. Nodes are cluster-scoped, so the list runs across every namespace (the
    /// kubelet records into <c>default</c>).
    /// </remarks>
    internal static string EventSelectorFor(DynamicResource target)
    {
        var selector = target.Namespace is { } ns
            ? $"involvedObject.name={target.Name},involvedObject.namespace={ns}"
            : $"involvedObject.name={target.Name}";

        return selector + (target.Kind != "Node" && target.Uid is { Length: > 0 } uid
            ? $",involvedObject.uid={uid}" : $",involvedObject.kind={target.Kind}");
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

/// <summary>
/// Field readers for Event objects fetched as <see cref="DynamicResource"/> — core/v1
/// Events and their <c>events.k8s.io/v1</c> twin, which is the same object served
/// under different field names (<c>regarding</c> for <c>involvedObject</c>,
/// <c>note</c> for <c>message</c>, <c>deprecatedCount</c>/<c>deprecated*Timestamp</c>
/// for the counters). Every reader here accepts either shape, so the list and the
/// navigation behave the same whichever of the two kinds the sidebar opened.
/// </summary>
public static class EventFields
{
    /// <summary>
    /// Whether a group/kind pair is an Event: core/v1 or <c>events.k8s.io</c>. A CRD
    /// that happens to be called <c>Event</c> in some other group is not one, which is
    /// the same group-first rule the sidebar applies to a CRD called <c>Deployment</c>.
    /// </summary>
    public static bool IsEventKind(string group, string kind) =>
        kind == "Event" && (group.Length == 0 || group == "events.k8s.io");

    public static bool IsEventKind(this ResourceDescriptor descriptor) =>
        IsEventKind(descriptor.Group, descriptor.Kind);

    /// <summary>Whether this object is an Event, from its own apiVersion and kind.</summary>
    public static bool IsEvent(this DynamicResource e)
    {
        var apiVersion = e.ApiVersion;
        var slash = apiVersion.IndexOf('/');
        return IsEventKind(slash < 0 ? "" : apiVersion[..slash], e.Kind);
    }

    public static string Type(this DynamicResource e) => e.Raw.TryGetProperty("type", out var v) ? v.GetString() ?? "" : "";

    public static string Reason(this DynamicResource e) => e.Raw.TryGetProperty("reason", out var v) ? v.GetString() ?? "" : "";

    /// <summary>The event's text: <c>message</c>, or <c>note</c> on an events.k8s.io object.</summary>
    public static string Message(this DynamicResource e) =>
        Text(e.Raw, "message") is { Length: > 0 } message ? message : Text(e.Raw, "note");

    public static int Count(this DynamicResource e) => e.Raw.TryGetProperty("count", out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>
    /// How many times this event has happened, as kubectl counts it: the series count
    /// when the event is a series, otherwise <c>count</c> (<c>deprecatedCount</c> on an
    /// events.k8s.io object). Never less than one — an event that exists happened at
    /// least once, and a singleton written through the new Events API carries no count
    /// at all, which is not the same thing as zero.
    /// </summary>
    public static int Occurrences(this DynamicResource e)
    {
        var count = 0;
        if (e.Raw.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object
            && series.TryGetProperty("count", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var seriesCount))
        {
            count = seriesCount;
        }
        else if (Int(e.Raw, "count") is { } legacy)
        {
            count = legacy;
        }
        else if (Int(e.Raw, "deprecatedCount") is { } deprecated)
        {
            count = deprecated;
        }

        return Math.Max(count, 1);
    }

    public static DateTimeOffset? LastTimestamp(this DynamicResource e)
    {
        if (e.Raw.TryGetProperty("lastTimestamp", out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out var dt))
        {
            return dt;
        }

        return e.CreationTimestamp;
    }

    /// <summary>
    /// When the event last happened — the list's "Last seen". The first of these that
    /// is set wins:
    /// <list type="number">
    /// <item><c>lastTimestamp</c> (<c>deprecatedLastTimestamp</c> on events.k8s.io) —
    /// what every controller writing core/v1 events keeps current;</item>
    /// <item><c>series.lastObservedTime</c> — for an event written through the new
    /// Events API as a series, the latest occurrence;</item>
    /// <item><c>eventTime</c> — the new API's own timestamp, which for a series is the
    /// <em>first</em> occurrence, and that is why the series is read before it (kubectl's
    /// own printer does the same);</item>
    /// <item><c>firstTimestamp</c> (<c>deprecatedFirstTimestamp</c>);</item>
    /// <item>the object's creation timestamp.</item>
    /// </list>
    /// Null when none is set. A zero <c>metav1.Time</c> ("0001-01-01T00:00:00Z") counts
    /// as unset rather than as an event two thousand years old.
    /// </summary>
    public static DateTimeOffset? LastSeen(this DynamicResource e) =>
        Time(e.Raw, "lastTimestamp")
        ?? Time(e.Raw, "deprecatedLastTimestamp")
        ?? (e.Raw.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object
            ? Time(series, "lastObservedTime")
            : null)
        ?? Time(e.Raw, "eventTime")
        ?? Time(e.Raw, "firstTimestamp")
        ?? Time(e.Raw, "deprecatedFirstTimestamp")
        ?? NonZero(e.CreationTimestamp);

    /// <summary>
    /// When the event first happened: <c>firstTimestamp</c>, else <c>eventTime</c> (for
    /// the new API that is the first occurrence). Null when neither is set — the list
    /// only uses it to add "first seen" to the Last seen tooltip of a repeated event.
    /// </summary>
    public static DateTimeOffset? FirstSeen(this DynamicResource e) =>
        Time(e.Raw, "firstTimestamp")
        ?? Time(e.Raw, "deprecatedFirstTimestamp")
        ?? Time(e.Raw, "eventTime");

    /// <summary>
    /// kubectl's OBJECT column: <c>Kind/name</c> of the object the event is about
    /// ("Pod/checkout-worker-5d8f7b9c4-qz9pl"). Just the name when the reference carries
    /// no kind, and empty when the event names no object at all.
    /// </summary>
    public static string ObjectText(this DynamicResource e)
    {
        if (InvolvedElement(e) is not { } io)
        {
            return "";
        }

        var kind = Text(io, "kind");
        var name = Text(io, "name");
        return name.Length == 0 ? "" : kind.Length == 0 ? name : $"{kind}/{name}";
    }

    /// <summary>
    /// The object this event is about, e.g. the pod a "BackOff" event fired on —
    /// reuses <see cref="OwnerRef"/> since the shape (apiVersion/kind/name/uid)
    /// is identical to an ownerReference, letting callers navigate to it through
    /// the same resolve-and-open path as owner-chip navigation. Read from
    /// <c>involvedObject</c>, or <c>regarding</c> on an events.k8s.io object.
    /// </summary>
    public static OwnerRef? InvolvedObject(this DynamicResource e)
    {
        if (InvolvedElement(e) is not { } io)
        {
            return null;
        }

        var kind = Text(io, "kind");
        var name = Text(io, "name");
        if (kind.Length == 0 || name.Length == 0)
        {
            return null;
        }

        return new OwnerRef(
            ApiVersion: Text(io, "apiVersion"),
            Kind: kind,
            Name: name,
            Uid: io.TryGetProperty("uid", out var u) ? u.GetString() : null,
            Controller: false);
    }

    /// <summary>The involved object's own namespace (may differ from the event's), when set.</summary>
    public static string? InvolvedObjectNamespace(this DynamicResource e) =>
        InvolvedElement(e) is { } io && io.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String
            ? ns.GetString()
            : null;

    private static JsonElement? InvolvedElement(DynamicResource e)
    {
        foreach (var name in (ReadOnlySpan<string>)["involvedObject", "regarding"])
        {
            if (e.Raw.TryGetProperty(name, out var io) && io.ValueKind == JsonValueKind.Object)
            {
                return io;
            }
        }

        return null;
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset? Time(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? NonZero(parsed)
            : null;

    private static DateTimeOffset? NonZero(DateTimeOffset? value) =>
        value is { } t && t.UtcDateTime.Year > 1 ? t : null;
}
