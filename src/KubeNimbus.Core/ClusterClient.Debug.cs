using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// What the exec pane needs beyond the exec itself: which OS a pod runs on (so it tries
/// <c>powershell</c> rather than <c>/bin/bash</c> on a Windows node), and the
/// <c>kubectl debug</c> gesture — add an ephemeral container to a pod whose image has no
/// shell, and wait for it to start. The rules are in <see cref="ExecShells"/> and
/// <see cref="DebugContainers"/>; this file is only the HTTP.
/// </summary>
public sealed partial class ClusterClient
{
    private const string StrategicMergePatchContentType = "application/strategic-merge-patch+json";

    private static readonly ResourceDescriptor NodeKind = new(
        Group: "", Version: "v1", Kind: "Node", Plural: "nodes", SingularName: "node",
        Namespaced: false, ShortNames: ["no"], Categories: []);

    /// <summary>
    /// The pod's OS: what the pod says (<see cref="ExecShells.FromPod"/>), else its node's
    /// label. A node the caller may not read — <c>get nodes</c> is cluster-scoped, and a
    /// namespace-scoped user is refused it as a matter of course — is
    /// <see cref="PodOperatingSystem.Unknown"/>, not an error: the pane then tries both
    /// families of shells. A pod that cannot be read throws, and the caller decides.
    /// </summary>
    public async Task<PodOperatingSystem> GetPodOperatingSystemAsync(
        string @namespace, string podName, CancellationToken cancellationToken = default)
    {
        var pod = await ReadResourceAsync(ResourceDescriptor.Pods, @namespace, podName, cancellationToken).ConfigureAwait(false);
        if (pod is null)
        {
            return PodOperatingSystem.Unknown;
        }

        var declared = ExecShells.FromPod(pod.Raw);
        if (declared != PodOperatingSystem.Unknown)
        {
            return declared;
        }

        var nodeName = NodeActions.NodeNameOf(pod);
        if (nodeName.Length == 0)
        {
            return PodOperatingSystem.Unknown;
        }

        try
        {
            var node = await ReadResourceAsync(NodeKind, null, nodeName, cancellationToken).ConfigureAwait(false);
            return node is null ? PodOperatingSystem.Unknown : ExecShells.FromNode(node.Raw);
        }
        catch (HttpRequestException)
        {
            return PodOperatingSystem.Unknown;
        }
    }

    /// <summary>
    /// Adds an ephemeral debug container to a pod, the patch <c>kubectl debug</c> sends
    /// (<see cref="DebugContainers.Patch"/>). Does not wait for it: see
    /// <see cref="WaitForDebugContainerAsync"/>. <c>SYS_PTRACE</c> is asked for first and
    /// dropped only when Pod Security refuses it; <see cref="DebugContainerAdded.CanTrace"/>
    /// says which. Any other refusal — a 403 for <c>patch</c> on
    /// <c>pods/ephemeralcontainers</c>, <c>restricted</c> refusing a root image — arrives as
    /// a <see cref="KubernetesApiException"/> carrying the server's sentence.
    /// </summary>
    public async Task<DebugContainerAdded> AddDebugContainerAsync(
        string @namespace,
        string podName,
        string targetContainer,
        string image,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        name ??= DebugContainers.NewName();
        try
        {
            await PatchEphemeralContainersAsync(
                @namespace, podName, DebugContainers.Patch(name, image, targetContainer, ptrace: true), cancellationToken)
                .ConfigureAwait(false);
            return new DebugContainerAdded(name, CanTrace: true);
        }
        catch (KubernetesApiException ex) when (DebugContainers.IsPodSecurityRefusal(ex.Message))
        {
            await PatchEphemeralContainersAsync(
                @namespace, podName, DebugContainers.Patch(name, image, targetContainer, ptrace: false), cancellationToken)
                .ConfigureAwait(false);
            return new DebugContainerAdded(name, CanTrace: false);
        }
    }

    private async Task PatchEphemeralContainersAsync(
        string @namespace, string podName, string patch, CancellationToken cancellationToken)
    {
        using var content = new StringContent(patch, Encoding.UTF8, StrategicMergePatchContentType);
        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            ResourceDescriptor.Pods.SubresourcePath(@namespace, podName, DebugContainers.Subresource),
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Follows the pod until the debug container runs or cannot: a field-selected watch of
    /// the one pod, the same informer loop every list uses, rather than a GET on a timer.
    /// <paramref name="progress"/> hears each intermediate state (the image pull is the
    /// slow part, and saying so is the difference between "pulling" and "stuck").
    /// </summary>
    /// <param name="timeout">
    /// How long to wait in all. A pull that is still going when it expires is reported as
    /// such, with the kubelet's last word — not as a failure the image caused.
    /// </param>
    public async Task<DebugContainerState> WaitForDebugContainerAsync(
        string @namespace,
        string podName,
        string name,
        TimeSpan timeout,
        Action<DebugContainerState>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var last = DebugContainerState.Starting;
        try
        {
            await foreach (var change in WatchResourceAsync(
                               ResourceDescriptor.Pods,
                               @namespace,
                               cancellationToken: deadline.Token,
                               fieldSelector: $"metadata.name={podName}").ConfigureAwait(false))
            {
                if (change.Type == ResourceEventType.Deleted)
                {
                    return new DebugContainerState(DebugContainerPhase.Failed, "PodDeleted", "the pod was deleted");
                }

                if (change.Resource is not { } pod)
                {
                    continue;
                }

                var state = DebugContainers.StateOf(pod.Raw, name);
                if (state.Phase != DebugContainerPhase.Starting)
                {
                    return state;
                }

                if (state != last)
                {
                    last = state;
                    progress?.Invoke(state);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline, not the caller: fall through to the timeout verdict.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lastWord = last.Reason is null && last.Message is null ? "" : $" Last state: {last.Describe()}.";
        return new DebugContainerState(
            DebugContainerPhase.Failed,
            "Timeout",
            $"it did not start within {timeout.TotalSeconds:0} seconds.{lastWord}");
    }
}

/// <param name="Name">The ephemeral container's name, <c>debugger-xxxxx</c>.</param>
/// <param name="CanTrace">
/// False when Pod Security refused <c>SYS_PTRACE</c> and the container was added without it:
/// the target's files under <c>/proc/1/root</c> are then readable only if both run as the
/// same user.
/// </param>
public sealed record DebugContainerAdded(string Name, bool CanTrace);
