namespace KubeNimbus.Core;

public enum ResourceEventType
{
    /// <summary>Consumers must clear their local cache; a fresh list follows (initial sync or 410-Gone relist).</summary>
    Reset,
    Added,
    Modified,
    Deleted,

    /// <summary>
    /// The initial list is complete: every object that existed when the sync started
    /// has been delivered, and everything after this is a live change. It is the only
    /// honest end of "loading" — <see cref="Reset"/> is the *start* of a sync, and a
    /// consumer that treats it as the end renders an empty list for however long the
    /// list request takes, which on a distant cluster is seconds (UI rule 18).
    /// Resource is null, as it is for Reset.
    /// </summary>
    Synced,
}

/// <summary>One informer-style event in a list+watch stream. Resource is null for Reset.</summary>
public sealed record ResourceEvent<T>(ResourceEventType Type, T? Resource) where T : class
{
    public static readonly ResourceEvent<T> Reset = new(ResourceEventType.Reset, null);

    public static readonly ResourceEvent<T> Synced = new(ResourceEventType.Synced, null);
}

/// <summary>Raised inside a watch stream when the connection drops and a reconnect is scheduled.</summary>
public sealed class WatchConnectionException(string message, Exception? inner = null) : Exception(message, inner);
