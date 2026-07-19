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
    public static IReadOnlyList<string> DiscoverPaths()
    {
        var paths = new List<string>();

        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            paths.AddRange(env
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(home, ".kube", "config");
        if (!paths.Contains(defaultPath))
        {
            paths.Add(defaultPath);
        }

        return paths.Where(File.Exists).Distinct().ToList();
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
