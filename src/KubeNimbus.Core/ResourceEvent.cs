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
/// <param name="credentialsRejected">
/// The API server answered 401: the credential has expired or been revoked, and the
/// kubeconfig was re-read before the retry. The UI offers a reconnect for this one.
/// </param>
public sealed class WatchConnectionException(string message, Exception? inner = null, bool credentialsRejected = false)
    : Exception(message, inner)
{
    public bool CredentialsRejected => credentialsRejected;
}

/// <summary>
/// The API server refused a list or watch (403): this user may not read the collection. Ends a
/// shared watch (<see cref="WatchCache"/>) instead of retrying it, because a refusal is an
/// answer, not a lost connection, and asking again every backoff changes nothing (#339).
/// <see cref="Exception.Message"/> is the server's own sentence, which names the user, the verb
/// and the resource.
/// </summary>
public sealed class WatchRefusedException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// The refusal in <paramref name="ex"/>, as a <see cref="WatchRefusedException"/>, or null when
    /// it is not a 403. A 403 on the list is a <see cref="KubernetesApiException"/> carrying the
    /// server's sentence; on the watch request (list allowed, watch not) a bare
    /// <see cref="HttpRequestException"/>; either may arrive wrapped in a
    /// <see cref="WatchConnectionException"/>.
    /// </summary>
    public static WatchRefusedException? From(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case WatchRefusedException refused:
                    return refused;
                case HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } forbidden:
                    return new WatchRefusedException(
                        (forbidden as KubernetesApiException)?.ServerMessage ?? forbidden.Message, forbidden);
            }
        }

        return null;
    }
}
