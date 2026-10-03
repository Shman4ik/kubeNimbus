using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>The operating system a pod's containers run on, as far as the API can tell.</summary>
public enum PodOperatingSystem
{
    /// <summary>Nothing on the pod said, and its node could not be read (RBAC often refuses <c>get nodes</c>).</summary>
    Unknown,
    Linux,
    Windows,
}

/// <summary>
/// Which shells the exec pane tries, and how it tells "this image has no shell" apart from
/// every other way an exec can fail. Pure functions over the pod's JSON and the error
/// channel's sentence, so <c>ExecShellsTests</c> pins them with no cluster.
/// </summary>
/// <remarks>
/// <para>
/// The pane used to try <c>/bin/bash</c>, <c>/bin/sh</c> and <c>/bin/ash</c> on every pod.
/// That is three guaranteed failures on a Windows node, whose containers carry
/// <c>powershell</c> (Server Core) or only <c>cmd</c> (Nano Server) — Lens and FreeLens
/// send <c>powershell</c> there, and so does this now. The node is identified the way the
/// scheduler identifies it: the pod's own <c>spec.os.name</c> (Kubernetes 1.25+), then its
/// <c>kubernetes.io/os</c> node selector (which every Windows workload needs on a mixed
/// cluster), then the node's own label.
/// </para>
/// <para>
/// When the OS cannot be told, both lists are tried, Linux first. A wrong guess costs one
/// refused exec per shell, which the API server answers within a round trip.
/// </para>
/// </remarks>
public static class ExecShells
{
    /// <summary>The well-known label (and node-selector key) every kubelet stamps with its OS.</summary>
    public const string OsLabel = "kubernetes.io/os";

    public static readonly IReadOnlyList<string> LinuxShells = ["/bin/bash", "/bin/sh", "/bin/ash"];

    /// <summary>
    /// Bare names, not paths: Windows resolves them through <c>PATH</c> and <c>PATHEXT</c>,
    /// which is how <c>kubectl exec … -- powershell</c> finds it. Nano Server has no
    /// PowerShell, hence <c>cmd</c> after it.
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsShells = ["powershell", "cmd"];

    /// <summary>The shells to try, in order, for a pod on <paramref name="os"/>.</summary>
    public static IReadOnlyList<string> Candidates(PodOperatingSystem os) => os switch
    {
        PodOperatingSystem.Linux => LinuxShells,
        PodOperatingSystem.Windows => WindowsShells,
        _ => [.. LinuxShells, .. WindowsShells],
    };

    /// <summary>
    /// What the pod itself says: <c>spec.os.name</c>, then <c>spec.nodeSelector</c>'s
    /// <c>kubernetes.io/os</c>. <see cref="PodOperatingSystem.Unknown"/> when it says
    /// neither, which on a Linux-only cluster is nearly every pod — the caller then asks
    /// the node (<see cref="FromNode"/>).
    /// </summary>
    public static PodOperatingSystem FromPod(JsonElement pod)
    {
        if (!TryObject(pod, "spec", out var spec))
        {
            return PodOperatingSystem.Unknown;
        }

        if (TryObject(spec, "os", out var os) && Parse(String(os, "name")) is var declared
            && declared != PodOperatingSystem.Unknown)
        {
            return declared;
        }

        return TryObject(spec, "nodeSelector", out var selector)
            ? Parse(String(selector, OsLabel))
            : PodOperatingSystem.Unknown;
    }

    /// <summary>The node's <c>kubernetes.io/os</c> label.</summary>
    public static PodOperatingSystem FromNode(JsonElement node) =>
        TryObject(node, "metadata", out var metadata) && TryObject(metadata, "labels", out var labels)
            ? Parse(String(labels, OsLabel))
            : PodOperatingSystem.Unknown;

    /// <summary>
    /// True when the exec failed because the command does not exist in the image — the
    /// case where trying another shell, or a debug container, is the answer. Anything else
    /// (a 403, a container that is not running, a node that cannot be reached) is a
    /// different problem, and the pane states the server's own sentence for it instead.
    /// </summary>
    /// <remarks>
    /// The wording is the container runtime's, not the API server's, so it is matched on
    /// the phrases runc, crun and containerd print on Linux and hcsshim prints on Windows:
    /// <c>stat /bin/sh: no such file or directory</c>,
    /// <c>exec: "sh": executable file not found in $PATH</c>, and
    /// <c>The system cannot find the file specified.</c>
    /// </remarks>
    public static bool IsMissingExecutable(string? message) =>
        message is not null
        && (message.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase));

    private static PodOperatingSystem Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "linux" => PodOperatingSystem.Linux,
        "windows" => PodOperatingSystem.Windows,
        _ => PodOperatingSystem.Unknown,
    };

    private static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
