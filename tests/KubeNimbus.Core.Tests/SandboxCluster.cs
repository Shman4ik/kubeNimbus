using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Resolves the sandbox cluster kubeconfig (see CLAUDE.md for the k3s recipe).
/// Tests that need a live cluster skip cleanly when it isn't reachable, so the
/// suite still runs in CI without one.
/// </summary>
internal static class SandboxCluster
{
    /// <summary>Path to the sandbox kubeconfig, or null when absent.</summary>
    public static string? KubeconfigPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("KUBENIMBUS_TEST_KUBECONFIG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            // Walk up from the test binary to the repo root's .sandbox/kubeconfig.yaml.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, ".sandbox", "kubeconfig.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }

    public static async Task<ClusterContext?> TryGetContextAsync(CancellationToken ct = default)
    {
        var path = KubeconfigPath;
        if (path is null)
        {
            return null;
        }

        var contexts = await Kubeconfig.LoadContextsAsync([path], ct);
        return contexts.Count > 0 ? contexts[0] : null;
    }
}
