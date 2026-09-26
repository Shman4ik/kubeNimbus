namespace KubeNimbus.Core.Applications;

/// <summary>The two latest deployed revisions of an Argo CD Application, and a link comparing them when the host is known.</summary>
/// <param name="Link">Null for an unknown host and for a Helm chart source — both SHAs (or chart versions) are still shown.</param>
public sealed record RevisionCompare(string From, string To, Uri? Link, bool IsChart)
{
    public string FromShort => IsChart ? From : ApplicationRules.RevisionLabel(From, chart: false);

    public string ToShort => IsChart ? To : ApplicationRules.RevisionLabel(To, chart: false);
}

/// <summary>
/// Builds a "compare these two commits" link on the Git host an Application's
/// <c>repoURL</c> points at. The link is the one place the Applications mode reaches
/// past the Kubernetes API, and it does not reach itself: it is opened in the system browser,
/// where the reader's own session on their Git host does the rest. This app sends nothing.
/// </summary>
/// <remarks>
/// Only hosts whose compare URL shape is known are linked — github.com, GitLab (gitlab.com or
/// any host whose name says gitlab), Azure DevOps (<c>dev.azure.com</c>,
/// <c>*.visualstudio.com</c>, and its SSH form) and Bitbucket Cloud. Anything else gets no
/// link rather than a guessed one: a link that 404s under pressure is worse than two SHAs to
/// paste.
/// </remarks>
public static class GitCompareLink
{
    public static RevisionCompare? For(ArgoApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.History.Count < 2)
        {
            return null;
        }

        // History is newest first (ArgoCd.ReadHistory).
        var to = app.History[0].Revision;
        var from = app.History[1].Revision;
        if (from.Length == 0 || to.Length == 0)
        {
            return null;
        }

        var chart = ApplicationRules.IsChartSource(app);
        return new RevisionCompare(from, to, chart ? null : CompareUrl(app.RepoUrl, from, to), chart);
    }

    /// <summary>The compare URL from <paramref name="from"/> (older) to <paramref name="to"/> (newer), or null.</summary>
    public static Uri? CompareUrl(string repoUrl, string from, string to)
    {
        if (Normalize(repoUrl) is not { } repo)
        {
            return null;
        }

        var host = repo.Host.ToLowerInvariant();
        var path = Uri.UnescapeDataString(repo.AbsolutePath).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var a = Uri.EscapeDataString(from);
        var b = Uri.EscapeDataString(to);

        if (host is "github.com" or "www.github.com")
        {
            return Owned(path, 2) ? new Uri($"https://github.com/{path}/compare/{a}...{b}") : null;
        }

        if (host == "dev.azure.com" || host == "ssh.dev.azure.com")
        {
            // https: org/project/_git/repo — ssh: v3/org/project/repo
            var parts = path.Split('/');
            if (host == "ssh.dev.azure.com" && parts.Length == 4 && parts[0] == "v3")
            {
                return AzureUrl($"https://dev.azure.com/{parts[1]}/{parts[2]}/_git/{parts[3]}", a, b);
            }

            return parts.Length == 4 && parts[2] == "_git"
                ? AzureUrl($"https://dev.azure.com/{path}", a, b)
                : null;
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            // https://org.visualstudio.com/[DefaultCollection/]project/_git/repo
            return path.Contains("/_git/", StringComparison.Ordinal)
                ? AzureUrl($"https://{host}/{path}", a, b)
                : null;
        }

        if (host is "bitbucket.org" or "www.bitbucket.org")
        {
            // Bitbucket Cloud reads compare/<source>..<destination>: the newer first.
            return Owned(path, 2) ? new Uri($"https://bitbucket.org/{path}/branches/compare/{b}..{a}#diff") : null;
        }

        if (host == "gitlab.com" || host.StartsWith("gitlab.", StringComparison.Ordinal) || host.Contains(".gitlab.", StringComparison.Ordinal))
        {
            return path.Contains('/', StringComparison.Ordinal)
                ? new Uri($"https://{host}{(repo.IsDefaultPort || repo.Scheme != "https" ? "" : $":{repo.Port}")}/{path}/-/compare/{a}...{b}")
                : null;
        }

        return null;
    }

    private static Uri AzureUrl(string repo, string a, string b) =>
        new($"{repo}/branchCompare?baseVersion=GC{a}&targetVersion=GC{b}");

    private static bool Owned(string path, int segments) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == segments;

    /// <summary>
    /// A repoURL as a URI, whatever form Argo was given it in: <c>https://…</c>,
    /// <c>ssh://git@host/…</c>, or scp-style <c>git@host:owner/repo.git</c>. Credentials in
    /// the user-info part are dropped; the result is only ever used for its host and path.
    /// </summary>
    internal static Uri? Normalize(string repoUrl)
    {
        var text = repoUrl.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            // scp-like: [user@]host:path
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            var hostPart = text[..colon];
            var at = hostPart.LastIndexOf('@');
            text = $"ssh://{(at >= 0 ? hostPart[(at + 1)..] : hostPart)}/{text[(colon + 1)..].TrimStart('/')}";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri : null;
    }
}
