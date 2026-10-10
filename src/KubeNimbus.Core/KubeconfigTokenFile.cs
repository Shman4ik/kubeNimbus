using k8s.KubeConfigModels;

namespace KubeNimbus.Core;

/// <summary>
/// A user entry's <c>tokenFile</c>: a path to a file holding the bearer token, which the
/// client library's model has no field for, so such a context used to connect with no
/// credential at all (ENG-56, found in the 2026-10-07 security audit).
/// </summary>
/// <remarks>
/// <para>
/// <b>client-go's rules.</b> A relative path resolves against the kubeconfig's own folder, as
/// every other path in the file does. When the entry also carries <c>token</c>, the file still
/// wins whenever it can be read and the inline token is the fallback — client-go's
/// <c>BearerTokenFile</c> "takes precedence over BearerToken" once read. The content is
/// trimmed, as client-go's cached file token source trims it.
/// </para>
/// <para>
/// <b>Read on every build, kept nowhere.</b> It is read inside
/// <see cref="Kubeconfig.BuildClientSetupAsync"/>, which runs on every connect and every
/// credential refresh, and written only into the in-memory model the client is built from —
/// so a token rotated on disk (a projected ServiceAccount token, a file a sign-in script
/// rewrites) is picked up by the next refresh, and nothing is persisted (hard rule 4).
/// While connected, <see cref="TokenFileProvider"/> re-reads it at most once a minute, as
/// client-go's cached file token source does, so a token rotated without the old one being
/// revoked is still picked up without a 401 (#262).
/// </para>
/// <para>
/// A file that cannot be read, or is empty, with no inline token to fall back on, fails the
/// connect at "Reading the kubeconfig" with a sentence naming the path. Connecting with no
/// credential instead would hide the problem behind a 401 or, worse, an anonymous identity.
/// </para>
/// </remarks>
internal static class KubeconfigTokenFile
{
    /// <summary>
    /// Reads the token file named by <paramref name="tokenFile"/> into
    /// <paramref name="credentials"/>' <c>Token</c>, in memory only.
    /// </summary>
    /// <returns>The resolved path when the token came from the file, which is when the client
    /// gets a <see cref="TokenFileProvider"/>; null when the inline token stands.</returns>
    /// <exception cref="KubeconfigSetupException">The file cannot be read or is empty, and there is no inline token.</exception>
    internal static string? Apply(UserCredentials credentials, string tokenFile, string? kubeconfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var path = Kubeconfig.ResolveAgainstFile(tokenFile, kubeconfigDirectory);
        string? problem;
        try
        {
            var token = File.ReadAllText(path).Trim();
            if (token.Length > 0)
            {
                credentials.Token = token;
                return path;
            }

            problem = "it is empty";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            problem = e is FileNotFoundException or DirectoryNotFoundException ? "there is no such file" : e.Message;
        }

        if (!string.IsNullOrEmpty(credentials.Token))
        {
            // client-go's fallback: the inline token stands when the file cannot be read.
            return null;
        }

        throw new KubeconfigSetupException(
            $"The user entry's tokenFile {path} could not be used: {problem}. kubeNimbus reads the bearer token from it on every connect.");
    }

    /// <summary>Reads the file's token, trimmed, or null when it cannot be read or is empty.</summary>
    internal static string? TryRead(string path)
    {
        try
        {
            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The bearer token of a <c>tokenFile</c> user, re-read from the file at most once every
/// <see cref="Period"/> while the client is in use — client-go's <c>cachingTokenSource</c>
/// over its file token source (#262).
/// </summary>
/// <remarks>
/// <para>
/// A provider rather than a timer driving <see cref="ClusterClient.RefreshCredentialsAsync"/>:
/// a refresh builds a new client and retires the old one, and only four retired clients are
/// kept, so a refresh every minute would close a log follow or an exec session opened more
/// than four minutes earlier. The provider changes the header of the next request and
/// nothing else, and reads nothing while the client is idle.
/// </para>
/// <para>
/// A read that fails, or finds the file empty, keeps the token it had, as client-go does: a
/// file being rewritten is briefly empty, and failing every request for that instant would be
/// worse than one more request with the token that was valid a minute ago. A token that has
/// stopped working is still caught by the informer's 401, which rebuilds the client and reads
/// the file again. The token lives in this object only, inside the client (hard rule 4).
/// </para>
/// </remarks>
internal sealed class TokenFileProvider(string path, string token, TimeProvider? time = null) : k8s.Authentication.ITokenProvider
{
    /// <summary>How long a token read from the file is used before the file is read again.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string _token = token;
    private DateTimeOffset _readAt = (time ?? TimeProvider.System).GetUtcNow(); // the build read it just now

    public Task<System.Net.Http.Headers.AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        string current;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _readAt >= Period)
            {
                _readAt = now;
                if (KubeconfigTokenFile.TryRead(path) is { } fresh)
                {
                    _token = fresh;
                }
            }

            current = _token;
        }

        return Task.FromResult(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", current));
    }
}
