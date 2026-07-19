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
    string KubeconfigPath);
