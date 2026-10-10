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
/// client-go also re-reads the file about once a minute while connected; here a 401 is what
/// triggers the re-read, through the informer's refresh.
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
    /// <exception cref="KubeconfigSetupException">The file cannot be read or is empty, and there is no inline token.</exception>
    internal static void Apply(UserCredentials credentials, string tokenFile, string? kubeconfigDirectory)
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
                return;
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
            return;
        }

        throw new KubeconfigSetupException(
            $"The user entry's tokenFile {path} could not be used: {problem}. kubeNimbus reads the bearer token from it on every connect.");
    }
}
