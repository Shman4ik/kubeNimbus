using k8s;

namespace KubeNimbus.Core;

/// <summary>
/// Kubeconfig discovery and loading. Kubeconfig is the single source of truth:
/// $KUBECONFIG (path-separator list) plus the default ~/.kube/config.
/// </summary>
public static class Kubeconfig
{
    /// <summary>
    /// Kubeconfig file paths in precedence order: every entry of $KUBECONFIG,
    /// then ~/.kube/config. Only existing files are returned.
    /// </summary>
    public static IReadOnlyList<string> DiscoverPaths() =>
        [.. CandidatePaths().Where(c => c.Exists).Select(c => c.Path).Distinct()];

    /// <summary>
    /// The same search, including paths that don't exist — so a UI with no
    /// contexts can say *where* it looked instead of just "none found".
    /// </summary>
    public static IReadOnlyList<KubeconfigCandidate> CandidatePaths()
    {
        var candidates = new List<KubeconfigCandidate>();

        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var path in env.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(new KubeconfigCandidate(path, File.Exists(path), "$KUBECONFIG"));
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
    public static async Task<IReadOnlyList<ClusterContext>> LoadContextsAsync(
        IEnumerable<string>? kubeconfigPaths = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ClusterContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in kubeconfigPaths ?? DiscoverPaths())
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
