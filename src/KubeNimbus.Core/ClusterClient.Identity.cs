using System.Net;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Who the API server says this connection is: <c>kubectl auth whoami</c>, which is a
/// <c>SelfSubjectReview</c>. Two readers: the Argo CD sync, which records the name as the
/// operation's initiator so Argo's own history says who asked rather than only which tool
/// did; and the access review's "Signed in as" line (<see cref="ReviewSelfSubjectAsync"/>),
/// which answers "I connected but everything is 403" — the server thinks I am
/// <c>arn:aws:iam::…:role/dev</c>, in these groups — as no client-side reasoning can.
/// </summary>
/// <remarks>
/// <para>
/// The sync's answer is asked lazily (the first sync on this connection asks; a session that
/// never syncs never does), once per credential: <see cref="RefreshCredentialsAsync"/> forgets
/// it, because a refreshed credential can be somebody else's — an <c>aws sso login</c> as a
/// different role, say. The access review asks afresh every time it loads, so it has nothing
/// to forget.
/// </para>
/// <para>
/// What is read is the username and, for the access review only, the groups. Groups are what
/// RBAC bindings name, so "which groups does the server put me in" is the other half of why a
/// request is refused; they are shown in that pane and held by nothing else — not this
/// object's cache, not the Argo sync (which sends the name alone), not any file. The UID and
/// the extras are never read: extras carry provider-specific identifiers (an access key id, a
/// session name) that no feature here needs, and hard rule 4 is the reason to keep that line
/// sharp even where a value is not strictly a credential.
/// </para>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>The API versions SelfSubjectReview is served at, newest first: GA in 1.28, beta (on by default) in 1.27.</summary>
    internal static readonly string[] SelfSubjectReviewVersions = ["v1", "v1beta1"];

    /// <summary>The time a review is given before it is reported as unanswered.</summary>
    private static readonly TimeSpan SelfSubjectReviewTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The cached answer for the current credential; null until asked, and again after a refresh.</summary>
    private Task<string?>? _username;

    /// <summary>
    /// The username the API server authenticates this connection as, or null when it could
    /// not say: a server older than 1.27 (no SelfSubjectReview), a refusal, or no answer.
    /// Never throws for any of those — the callers want a name when there is one and carry on
    /// without it when there is not — except to report <paramref name="cancellationToken"/>.
    /// </summary>
    public Task<string?> GetCurrentUsernameAsync(CancellationToken cancellationToken = default)
    {
        Task<string?> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Task.Run so the request (and any credential plugin it runs) starts off this
            // lock rather than inline under it.
            pending = _username ??= Task.Run(ReadUsernameAsync);
        }

        return pending.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Asks the API server who this connection is, for the access review: the username and the
    /// groups, or which of the three ways it could not say. Asked afresh on every call and
    /// cached nowhere. Never throws, except to report <paramref name="cancellationToken"/>.
    /// </summary>
    public Task<SelfSubjectIdentity> ReviewSelfSubjectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return PostSelfSubjectReviewAsync(cancellationToken);
    }

    /// <summary>
    /// The request behind the cached username, with no caller's token: the answer is shared by
    /// every later caller on this credential, so one caller giving up must not leave a
    /// cancelled task in the cache. A failure that may be transient (a refusal, a timeout) is
    /// not cached — the next caller asks again — while "neither version is served" is the
    /// server's settled answer and is.
    /// </summary>
    private async Task<string?> ReadUsernameAsync()
    {
        var identity = await PostSelfSubjectReviewAsync(CancellationToken.None).ConfigureAwait(false);
        if (identity.Outcome is SelfSubjectReviewOutcome.Refused or SelfSubjectReviewOutcome.Failed)
        {
            ForgetUsername();
        }

        return identity.Username;
    }

    /// <summary>
    /// POSTs a SelfSubjectReview at each version in turn. A 404 means that version is not
    /// served and the next is tried; a 404 at both is <see cref="SelfSubjectReviewOutcome.NotServed"/>.
    /// </summary>
    private async Task<SelfSubjectIdentity> PostSelfSubjectReviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var version in SelfSubjectReviewVersions)
            {
                using var content = new StringContent(
                    $$"""{"apiVersion":"authentication.k8s.io/{{version}}","kind":"SelfSubjectReview"}""",
                    Encoding.UTF8,
                    "application/json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SelfSubjectReviewTimeout);
                using var response = await SendRequestAsync(
                    HttpMethod.Post, $"apis/authentication.k8s.io/{version}/selfsubjectreviews", content,
                    HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var reason = KubernetesApiException.ReadStatusMessage(body)
                        ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
                    return SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.Refused, reason);
                }

                var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var document = await ClusterJson.ParseAsync(stream, timeout.Token).ConfigureAwait(false);
                return new SelfSubjectIdentity(
                    SelfSubjectReviewOutcome.Answered,
                    ParseSelfSubjectReviewUsername(document.RootElement),
                    ParseSelfSubjectReviewGroups(document.RootElement),
                    Detail: null);
            }

            return SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.NotServed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SelfSubjectIdentity.Unknown(
                SelfSubjectReviewOutcome.Failed, $"no answer within {SelfSubjectReviewTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Drops the cached answer so the next caller asks again.</summary>
    private void ForgetUsername()
    {
        lock (_gate)
        {
            _username = null;
        }
    }

    /// <summary>
    /// <c>status.userInfo.username</c> of a SelfSubjectReview response, or null when the
    /// response does not carry one.
    /// </summary>
    public static string? ParseSelfSubjectReviewUsername(JsonElement review) =>
        UserInfo(review) is { } user
        && user.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String
        && name.GetString() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// <c>status.userInfo.groups</c> of a SelfSubjectReview response, in the server's order;
    /// empty when it carries none. Non-string and empty entries are skipped.
    /// </summary>
    public static IReadOnlyList<string> ParseSelfSubjectReviewGroups(JsonElement review)
    {
        if (UserInfo(review) is not { } user
            || !user.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. groups.EnumerateArray()
            .Where(g => g.ValueKind == JsonValueKind.String)
            .Select(g => g.GetString()!)
            .Where(g => g.Length > 0)];
    }

    private static JsonElement? UserInfo(JsonElement review) =>
        review.ValueKind == JsonValueKind.Object
        && review.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
        && status.TryGetProperty("userInfo", out var user) && user.ValueKind == JsonValueKind.Object
            ? user
            : null;
}

/// <summary>How a SelfSubjectReview went.</summary>
public enum SelfSubjectReviewOutcome
{
    /// <summary>The server answered; <see cref="SelfSubjectIdentity.Username"/> is its answer.</summary>
    Answered,

    /// <summary>Neither v1 nor v1beta1 is served: a server older than Kubernetes 1.27. A settled answer, not an error.</summary>
    NotServed,

    /// <summary>The server refused the review; <see cref="SelfSubjectIdentity.Detail"/> is its own message.</summary>
    Refused,

    /// <summary>No answer: the connection failed or timed out; <see cref="SelfSubjectIdentity.Detail"/> says how.</summary>
    Failed,
}

/// <summary>
/// Who the API server says this connection is (<c>kubectl auth whoami</c>): the username and
/// the groups when it answered, otherwise why it did not. Never the UID or the extras.
/// </summary>
public sealed record SelfSubjectIdentity(
    SelfSubjectReviewOutcome Outcome,
    string? Username,
    IReadOnlyList<string> Groups,
    string? Detail)
{
    /// <summary>An outcome with no identity in it.</summary>
    public static SelfSubjectIdentity Unknown(SelfSubjectReviewOutcome outcome, string? detail) =>
        new(outcome, null, [], detail);
}
