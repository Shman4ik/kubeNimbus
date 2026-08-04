namespace KubeNimbus.Core;

/// <summary>
/// A selectable kubeconfig context. Points back at the file it came from so the
/// client config is always re-resolved through the kubeconfig chain at connect
/// time — the app never copies or persists credentials.
/// </summary>
public sealed record ClusterContext(
    string Name,
    string ClusterName,
    string? Namespace,
    string? UserName,
    string KubeconfigPath)
{
    /// <summary>
    /// The sentinel <see cref="KubeconfigPath"/> of the built-in demo cluster — a
    /// dataset that ships with the app and is served from memory, with no cluster
    /// and no credentials behind it (see CLAUDE.md's "Demo cluster" section).
    ///
    /// A sentinel path rather than a new field on this record, deliberately: everything
    /// that already keys off a context — workspace tab snapshots
    /// (<c>TabSnapshot(ContextName, KubeconfigPath)</c>), the cluster switcher's
    /// name-and-path matching, fleet member naming — keeps working unchanged, and a
    /// build that reads an older workspace.json simply finds no such context. The
    /// angle brackets are not legal in a path on Windows and would be a bizarre one on
    /// Unix, so it cannot collide with a file a user actually picked.
    /// </summary>
    public const string DemoKubeconfigPath = "<demo>";

    /// <summary>
    /// True for the built-in demo cluster. Everything downstream branches on this
    /// rather than on a parallel type: a demo tab is an ordinary
    /// <see cref="ClusterContext"/> that happens to have no client behind it.
    /// </summary>
    public bool IsDemo => KubeconfigPath == DemoKubeconfigPath;

    /// <summary>
    /// The one demo context. Named so that nothing about it reads like a real
    /// cluster — and so <c>ClusterEnvironments.Classify</c> lands it on
    /// <see cref="ClusterEnvironment.Development"/> rather than anywhere near
    /// production.
    /// </summary>
    public static ClusterContext Demo { get; } =
        new("Demo cluster", "kubenimbus-demo", "payments", "demo", DemoKubeconfigPath);
}
