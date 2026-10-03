using System.Buffers;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>Where a debug (ephemeral) container is on its way to a shell.</summary>
public enum DebugContainerPhase
{
    /// <summary>Not in the pod's status yet, or waiting for something that will pass (an image pull in progress).</summary>
    Starting,
    Running,

    /// <summary>It will not run: the image cannot be pulled, it exited, or the pod has gone.</summary>
    Failed,
}

/// <param name="Phase">Where it is.</param>
/// <param name="Reason">The kubelet's reason (<c>ImagePullBackOff</c>, <c>Completed</c>, …), when it gave one.</param>
/// <param name="Message">The kubelet's sentence, when it gave one.</param>
public sealed record DebugContainerState(DebugContainerPhase Phase, string? Reason = null, string? Message = null)
{
    public static readonly DebugContainerState Starting = new(DebugContainerPhase.Starting);
    public static readonly DebugContainerState Running = new(DebugContainerPhase.Running);

    /// <summary>"ImagePullBackOff: Back-off pulling image …", or whichever half there is.</summary>
    public string Describe() => (Reason, Message) switch
    {
        ({ Length: > 0 } r, { Length: > 0 } m) => $"{r}: {m}",
        ({ Length: > 0 } r, _) => r,
        (_, { Length: > 0 } m) => m,
        _ => Phase.ToString(),
    };
}

/// <summary>
/// The rules behind <c>kubectl debug -it &lt;pod&gt; --image=… --target=&lt;container&gt;</c>:
/// the ephemeral container's name, the patch that adds it, how its status reads, and
/// whether one already running can be reused instead of adding another. The HTTP is in
/// <c>ClusterClient.Debug.cs</c>; this is the part that can be wrong silently, so
/// <c>DebugContainersTests</c> pins it byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// An ephemeral container is the answer to an image with no shell (distroless, .NET
/// chiseled): a container from another image joins the pod, shares the target's process
/// namespace (<c>targetContainerName</c>) and network, and the target's filesystem is
/// reachable as <c>/proc/1/root</c>. It is a real change to the pod — it cannot be
/// removed, and it stays in the spec until the pod is recreated — which is why the pane
/// states that before adding one and reuses a running one rather than piling them up.
/// </para>
/// <para>
/// <c>SYS_PTRACE</c> first, as kubectl's <c>general</c> profile asks: the target usually
/// runs as its own user (a .NET chiseled image runs as 1654) and the debug image as root,
/// and without the capability the kernel refuses root a look at another user's
/// <c>/proc/1/root</c> — the one path that makes the target's files reachable. Pod
/// Security's <c>baseline</c> level refuses the capability, and admission refuses the
/// whole patch when it does, so nothing has been added and the patch is sent again
/// without it (<see cref="IsPodSecurityRefusal"/>). A namespace enforcing
/// <c>restricted</c> refuses a root debug image either way, and its message is what the
/// pane shows.
/// </para>
/// </remarks>
public static class DebugContainers
{
    /// <summary>The subresource the patch goes to — <c>pods/{name}/ephemeralcontainers</c>, GA since Kubernetes 1.25.</summary>
    public const string Subresource = "ephemeralcontainers";

    /// <summary>
    /// A shell, coreutils, <c>wget</c>, <c>nc</c>, <c>ps</c> and <c>top</c> in 4 MB. Pinned to a
    /// minor so what the pane offers does not change under anyone; an air-gapped cluster
    /// types its own mirror into the box.
    /// </summary>
    public const string DefaultImage = "busybox:1.37";

    /// <summary>kubectl's own prefix, so a container this app added reads the same in <c>kubectl describe</c>.</summary>
    public const string NamePrefix = "debugger-";

    /// <summary>
    /// The alphabet of Kubernetes' <c>utilrand.String</c>: no vowels (no accidental words)
    /// and no 0/1/3 (no confusion with o/l/e). Five of them is kubectl's suffix length.
    /// </summary>
    private const string SuffixAlphabet = "bcdfghjklmnpqrstvwxz2456789";

    /// <summary>A fresh name, <c>debugger-xxxxx</c>.</summary>
    public static string NewName(Random? random = null)
    {
        random ??= Random.Shared;
        Span<char> suffix = stackalloc char[5];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = SuffixAlphabet[random.Next(SuffixAlphabet.Length)];
        }

        return NamePrefix + new string(suffix);
    }

    /// <summary>
    /// The strategic merge patch kubectl sends to <c>pods/{name}/ephemeralcontainers</c>:
    /// the list merges by name, so this adds one container and leaves any earlier ones
    /// alone. <c>stdin</c> and <c>tty</c> are what keep the image's default shell alive
    /// with nobody attached — without them BusyBox's <c>sh</c> reads EOF and exits at
    /// once, and there is nothing left to exec into.
    /// </summary>
    /// <param name="ptrace">Ask for <c>SYS_PTRACE</c>; see the class remarks for why, and why it can be refused.</param>
    public static string Patch(string name, string image, string targetContainer, bool ptrace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetContainer);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("spec");
            writer.WriteStartArray("ephemeralContainers");
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("image", image.Trim());
            writer.WriteString("imagePullPolicy", "IfNotPresent");
            writer.WriteString("targetContainerName", targetContainer);
            writer.WriteBoolean("stdin", true);
            writer.WriteBoolean("tty", true);
            writer.WriteString("terminationMessagePolicy", "File");
            if (ptrace)
            {
                writer.WriteStartObject("securityContext");
                writer.WriteStartObject("capabilities");
                writer.WriteStartArray("add");
                writer.WriteStringValue("SYS_PTRACE");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// True when Pod Security admission refused the patch — the API server's
    /// <c>violates PodSecurity "baseline:latest": non-default capabilities …</c>. Admission
    /// refuses a request whole, so after this nothing was added and a second patch without
    /// the capability is not a second container.
    /// </summary>
    public static bool IsPodSecurityRefusal(string? message) =>
        message is not null && message.Contains("violates PodSecurity", StringComparison.Ordinal);

    /// <summary>
    /// Waiting reasons that will not clear on their own. <c>ErrImagePull</c> is on the list
    /// although the kubelet retries it: the retry is <c>ImagePullBackOff</c> a few seconds
    /// later with the same cause, and waiting through the back-off only delays the sentence
    /// that says the image name is wrong or the registry is unreachable.
    /// </summary>
    private static readonly HashSet<string> FatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "ErrImagePull",
        "ImagePullBackOff",
        "InvalidImageName",
        "ErrImageNeverPull",
        "CreateContainerConfigError",
        "CreateContainerError",
        "RunContainerError",
    };

    /// <summary>Reads one ephemeral container's state out of the pod.</summary>
    public static DebugContainerState StateOf(JsonElement pod, string name)
    {
        if (!pod.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return DebugContainerState.Starting;
        }

        var phase = String(status, "phase");
        if (phase is "Succeeded" or "Failed")
        {
            return new DebugContainerState(DebugContainerPhase.Failed, $"Pod{phase}",
                "the pod is no longer running, so nothing can be added to it");
        }

        if (!status.TryGetProperty("ephemeralContainerStatuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            return DebugContainerState.Starting;
        }

        foreach (var entry in statuses.EnumerateArray())
        {
            if (!string.Equals(String(entry, "name"), name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!entry.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            {
                return DebugContainerState.Starting;
            }

            if (state.TryGetProperty("running", out _))
            {
                return DebugContainerState.Running;
            }

            if (state.TryGetProperty("terminated", out var terminated) && terminated.ValueKind == JsonValueKind.Object)
            {
                var exit = terminated.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number
                    ? $" (exit code {code.GetInt32()})"
                    : "";
                return new DebugContainerState(
                    DebugContainerPhase.Failed,
                    (String(terminated, "reason") ?? "Terminated") + exit,
                    String(terminated, "message"));
            }

            if (state.TryGetProperty("waiting", out var waiting) && waiting.ValueKind == JsonValueKind.Object)
            {
                var reason = String(waiting, "reason");
                return reason is not null && FatalWaitingReasons.Contains(reason)
                    ? new DebugContainerState(DebugContainerPhase.Failed, reason, String(waiting, "message"))
                    : new DebugContainerState(DebugContainerPhase.Starting, reason, String(waiting, "message"));
            }

            return DebugContainerState.Starting;
        }

        return DebugContainerState.Starting;
    }

    /// <summary>
    /// A debug container this pod already has that can be opened instead of adding
    /// another: running, targeting the same container, from the same image. Ephemeral
    /// containers cannot be removed, so a second click on "Start debug container" that
    /// added a second one would leave the pod carrying both until it is recreated.
    /// </summary>
    public static string? FindReusable(JsonElement pod, string targetContainer, string image)
    {
        if (!pod.TryGetProperty("spec", out var spec)
            || !spec.TryGetProperty("ephemeralContainers", out var containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var container in containers.EnumerateArray())
        {
            var name = String(container, "name");
            if (name is null
                || !string.Equals(String(container, "targetContainerName"), targetContainer, StringComparison.Ordinal)
                || !string.Equals(String(container, "image"), image.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            if (StateOf(pod, name).Phase == DebugContainerPhase.Running)
            {
                return name;
            }
        }

        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
