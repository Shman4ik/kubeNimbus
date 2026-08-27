using KubeNimbus.Core;
using TUnit.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Resolves the sandbox cluster kubeconfig (see CLAUDE.md for the k3s recipe).
/// Tests that need a live cluster skip cleanly when it isn't reachable, so the
/// suite still runs in CI without one.
/// </summary>
internal static class SandboxCluster
{
    /// <summary>
    /// How long the API server gets to answer before the cluster is treated as absent.
    /// The sandbox is a container on loopback, so a healthy one answers in milliseconds;
    /// this is generous enough that a busy machine does not read as a missing cluster,
    /// and short enough that a suite run without one is not held up by it.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The probe runs at most once per suite run, whatever order the tests execute in:
    /// its answer is a property of the machine, not of the caller, and forty tests each
    /// paying a connection attempt against a cluster that is not there is exactly the
    /// wait this exists to avoid.
    /// </summary>
    private static readonly Lazy<Task<ClusterContext?>> Probe = new(ProbeAsync);

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

    /// <summary>
    /// The sandbox context — or a skipped test, when there is no cluster to talk to.
    ///
    /// <para>
    /// A kubeconfig on disk is <em>not</em> evidence of a cluster, which is what this
    /// used to assume. <c>.sandbox/kubeconfig.yaml</c> outlives the container it was
    /// written for: stop the container (or reboot without starting Docker) and the file
    /// is still there, still parses, and still names a server on loopback that nothing
    /// is listening on. Every cluster-gated test then failed with `No connection could
    /// be made because the target machine actively refused it`, and two of them sat out
    /// their full 30-second timeouts first — a red suite that says nothing about the
    /// code. So the gate asks the API server instead of the filesystem, once per run.
    /// </para>
    ///
    /// <para>
    /// It <b>skips</b> rather than returning null, because an early <c>return</c> is
    /// reported as a <em>pass</em>: a run that talked to no cluster at all looked exactly
    /// like one that exercised a real API server, and this repo has twice mistaken the
    /// first for the second. The callers' own <c>if (… is null) return;</c> guards are
    /// left in place as the belt to this braces — they are what the tests read as their
    /// gate, and they still work if this ever goes back to returning null.
    /// </para>
    /// </summary>
    public static async Task<ClusterContext?> TryGetContextAsync()
    {
        var context = await Probe.Value;
        if (context is null)
        {
            Skip.Test(SkipReason ?? "no sandbox cluster");
        }

        return context;
    }

    /// <summary>Why the probe gave up, for the skip message. Set once, by the probe.</summary>
    private static string? SkipReason;

    private static async Task<ClusterContext?> ProbeAsync()
    {
        var path = KubeconfigPath;
        if (path is null)
        {
            Skipping("no sandbox kubeconfig found (see CLAUDE.md for the k3s recipe)");
            return null;
        }

        var contexts = await Kubeconfig.LoadContextsAsync([path]);
        if (contexts.Count == 0)
        {
            Skipping($"{path} names no contexts");
            return null;
        }

        var context = contexts[0];
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            // Connect() is inside the try on purpose: it throws for a kubeconfig that
            // parses but names no usable credentials, which is a sandbox this machine
            // cannot talk to for the same practical reason as one that is switched off.
            using var client = ClusterClient.Connect(context);
            await client.GetServerVersionAsync(timeout.Token);
        }
        // Only the ways a sandbox can be unusable from here: nothing listening, TLS that
        // cannot be established (a kubeconfig left over from a previous `-Recreate`
        // carries the old CA), no answer inside the timeout, or a kubeconfig with no
        // usable credentials in it. Anything else — an authorization failure, a
        // malformed response — means a server did answer, and is a real finding that
        // belongs in the test that provoked it rather than in a silent skip.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       or k8s.Exceptions.KubeConfigException)
        {
            Skipping($"{path} does not lead to a cluster this machine can talk to " +
                     $"within {ProbeTimeout.TotalSeconds:0}s ({ex.Message})");
            return null;
        }

        return context;
    }

    private static void Skipping(string reason) => SkipReason = reason;
}
