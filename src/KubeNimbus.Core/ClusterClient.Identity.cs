using System.Net;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Who the API server says this connection is: <c>kubectl auth whoami</c>, which is a
/// <c>SelfSubjectReview</c>. The one reader today is the Argo CD sync, which records the name
/// as the operation's initiator so Argo's own history says who asked rather than only which
/// tool did.
/// </summary>
/// <remarks>
/// <para>
/// Asked lazily (the first sync on this connection asks; a session that never syncs never
/// does), once per credential: <see cref="RefreshCredentialsAsync"/> forgets the answer,
/// because a refreshed credential can be somebody else's — an <c>aws sso login</c> as a
/// different role, say.
/// </para>
/// <para>
/// Never cached anywhere but this object, and never more than the username (hard rule 4 is
/// about credentials, and a name is not one, but the groups and extras say more about a person
/// than any feature here needs).
/// </para>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>The API versions SelfSubjectReview is served at, newest first: GA in 1.28, beta (on by default) in 1.27.</summary>
    internal static readonly string[] SelfSubjectReviewVersions = ["v1", "v1beta1"];

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
    /// The request itself, with no caller's token: the answer is shared by every later caller
    /// on this credential, so one caller giving up must not leave a cancelled task in the cache.
    /// A failure that may be transient (a refusal, a timeout) is not cached — the next caller
    /// asks again — while "neither version is served" is the server's settled answer and is.
    /// </summary>
    private async Task<string?> ReadUsernameAsync()
    {
        try
        {
            foreach (var version in SelfSubjectReviewVersions)
            {
                using var content = new StringContent(
                    $$"""{"apiVersion":"authentication.k8s.io/{{version}}","kind":"SelfSubjectReview"}""",
                    Encoding.UTF8,
                    "application/json");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await SendRequestAsync(
                    HttpMethod.Post, $"apis/authentication.k8s.io/{version}/selfsubjectreviews", content,
                    HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    ForgetUsername();
                    return null;
                }

                var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var document = await ClusterJson.ParseAsync(stream, timeout.Token).ConfigureAwait(false);
                return ParseSelfSubjectReviewUsername(document.RootElement);
            }

            return null;
        }
        catch (Exception)
        {
            ForgetUsername();
            return null;
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
    /// response does not carry one. Nothing else in <c>userInfo</c> is read.
    /// </summary>
    public static string? ParseSelfSubjectReviewUsername(JsonElement review) =>
        review.ValueKind == JsonValueKind.Object
        && review.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
        && status.TryGetProperty("userInfo", out var user) && user.ValueKind == JsonValueKind.Object
        && user.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String
        && name.GetString() is { Length: > 0 } value
            ? value
            : null;
}
