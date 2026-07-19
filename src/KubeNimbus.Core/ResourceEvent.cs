namespace KubeNimbus.Core;

public enum ResourceEventType
{
    /// <summary>Consumers must clear their local cache; a fresh list follows (initial sync or 410-Gone relist).</summary>
    Reset,
    Added,
    Modified,
    Deleted,
}

/// <summary>One informer-style event in a list+watch stream. Resource is null for Reset.</summary>
public sealed record ResourceEvent<T>(ResourceEventType Type, T? Resource) where T : class
{
    public static readonly ResourceEvent<T> Reset = new(ResourceEventType.Reset, null);
}

/// <summary>Raised inside a watch stream when the connection drops and a reconnect is scheduled.</summary>
public sealed class WatchConnectionException(string message, Exception? inner = null) : Exception(message, inner);
