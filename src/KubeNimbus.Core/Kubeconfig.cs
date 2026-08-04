using k8s;

namespace KubeNimbus.Core;

/// <summary>
/// Kubeconfig discovery and loading. Kubeconfig is the single source of truth:
/// $KUBECONFIG (path-separator list) plus the default ~/.kube/config.
/// </summary>
public static class Kubeconfig
{
    /// <summary>
    /// The source label <see cref="CandidatePaths"/> stamps on a path the user
    /// chose through the app's file picker, so the empty state's "Searched:" list
    /// distinguishes it from the two locations the app looks in on its own.
    /// </summary>
    public const string PickedSource = "picked";

    /// <summary>
    /// Kubeconfig file paths in precedence order: any user-picked file, then every
    /// entry of $KUBECONFIG, then ~/.kube/config. Only existing files are returned —
    /// which is what makes a picked file that has since been moved or deleted
    /// degrade to "no contexts" rather than to an exception.
    /// </summary>
    /// <param name="extraPaths">
    /// Files the user pointed the app at explicitly (the "Open kubeconfig file…"
    /// picker). Only the <em>path</em> is ever kept — the file is re-read through
    /// this chain at connect time like any other, so no credential is copied
    /// anywhere (CLAUDE.md rule #4).
    /// </param>
    public static IReadOnlyList<string> DiscoverPaths(IEnumerable<string>? extraPaths = null) =>
        [.. CandidatePaths(extraPaths).Where(c => c.Exists).Select(c => c.Path).Distinct()];

    /// <summary>
    /// The same search, including paths that don't exist — so a UI with no
    /// contexts can say *where* it looked instead of just "none found". A picked
    /// path that has gone away is reported here as missing, which is the whole
    /// reason it is listed rather than silently dropped.
    /// </summary>
    public static IReadOnlyList<KubeconfigCandidate> CandidatePaths(IEnumerable<string>? extraPaths = null)
    {
        var candidates = new List<KubeconfigCandidate>();

        // First, so a file the user explicitly chose wins on duplicate context
        // names — an explicit choice outranks whatever the environment happened
        // to be carrying.
        foreach (var path in extraPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path)
                && !candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(new KubeconfigCandidate(path, File.Exists(path), PickedSource));
            }
        }

        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var path in env.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(new KubeconfigCandidate(path, File.Exists(path), "$KUBECONFIG"));
                }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(home, ".kube", "config");
        if (!candidates.Any(c => string.Equals(c.Path, defaultPath, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(new KubeconfigCandidate(defaultPath, File.Exists(defaultPath), "default location"));
        }

        return candidates;
    }

    /// <summary>
    /// All contexts across the discovered kubeconfig files (first file wins on
    /// duplicate context names, matching kubectl merge semantics).
    /// </summary>
    /// <param name="kubeconfigPaths">An explicit file list, bypassing discovery entirely.</param>
    /// <param name="extraPaths">
    /// User-picked files to search <em>in addition to</em> the usual chain — ignored
    /// when <paramref name="kubeconfigPaths"/> is supplied, since that is already an
    /// explicit list. A picked file that no longer exists is dropped by
    /// <see cref="DiscoverPaths"/> before it gets here, so a stale pick costs a
    /// missing row in the empty state's search list, not an exception.
    /// </param>
    public static async Task<IReadOnlyList<ClusterContext>> LoadContextsAsync(
        IEnumerable<string>? kubeconfigPaths = null,
        IEnumerable<string>? extraPaths = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ClusterContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in kubeconfigPaths ?? DiscoverPaths(extraPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = await KubernetesClientConfiguration.LoadKubeConfigAsync(path).ConfigureAwait(false);
            foreach (var ctx in config.Contexts ?? [])
            {
                if (ctx.Name is null || !seen.Add(ctx.Name))
                {
                    continue;
                }

                result.Add(new ClusterContext(
                    Name: ctx.Name,
                    ClusterName: ctx.ContextDetails?.Cluster ?? "",
                    Namespace: ctx.ContextDetails?.Namespace,
                    UserName: ctx.ContextDetails?.User,
                    KubeconfigPath: path));
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a client configuration for one context, re-resolving the file on
    /// every call (exec plugins, rotated certs and tokens are picked up fresh).
    /// </summary>
    public static KubernetesClientConfiguration BuildClientConfig(ClusterContext context) =>
        KubernetesClientConfiguration.BuildConfigFromConfigFile(
            kubeconfigPath: context.KubeconfigPath,
            currentContext: context.Name);
}

/// <summary>One place the kubeconfig search looked, and whether anything was there.</summary>
public sealed record KubeconfigCandidate(string Path, bool Exists, string Source);
