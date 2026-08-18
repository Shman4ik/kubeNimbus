using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// Node operations: cordon/uncordon, the pods scheduled on a node, and drain.
/// </summary>
/// <remarks>
/// <para>
/// <c>KubernetesClient.Aot</c> ships the eviction primitive and no drain helper, and
/// there is no Go <c>k8s.io/kubectl/pkg/drain</c> to import here, so the loop is ours —
/// which is exactly why the decisions it makes live in <see cref="NodeActions"/> as pure,
/// tested functions and only the HTTP is here. See CLAUDE.md's "Node operations" section
/// for the four constraints this design is under, in particular the one that cannot be
/// engineered away: this drain runs in the desktop app's own process, so quitting stops
/// it partway.
/// </para>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>
    /// How long the drain waits between passes while pods terminate. The one place this
    /// app polls other than the metrics API, and the exception is argued rather than
    /// assumed: the loop's decision is "is this specific set of pods gone yet", it has a
    /// natural end (the set empties), it is scoped to the drain's own
    /// <see cref="CancellationToken"/>, and it is what <c>kubectl drain</c>'s own
    /// <c>waitForDelete</c> does. A watch would report the deletions but not the
    /// question — and re-listing is also how the drain notices a pod that appeared
    /// <em>after</em> it started, which a watch seeded once would not.
    /// </summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cordons or uncordons a node — a one-field merge patch of <c>spec.unschedulable</c>,
    /// which is exactly what <c>kubectl cordon</c> sends.
    /// </summary>
    public async Task SetNodeSchedulableAsync(
        ResourceDescriptor nodeDescriptor,
        string name,
        bool schedulable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeDescriptor);

        using var content = new StringContent(
            NodeActions.CordonPatch(!schedulable), Encoding.UTF8, MergePatchContentType);

        using var response = await SendRequestAsync(
            HttpMethod.Patch,
            nodeDescriptor.ItemPath(null, name),
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        // Not EnsureSuccessStatusCode: the failure that happens here is a 403 naming the
        // subject and the verb, and that sentence is the whole diagnosis.
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every pod scheduled on one node, across all namespaces. A server-side
    /// <c>fieldSelector</c>, not a client-side filter over every pod in the cluster:
    /// on a large cluster the difference is a few kilobytes against a few megabytes,
    /// and the API server indexes <c>spec.nodeName</c> precisely for this.
    /// </summary>
    public Task<IReadOnlyList<DynamicResource>> ListPodsOnNodeAsync(
        ResourceDescriptor podDescriptor,
        string nodeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podDescriptor);

        return ListResourceOnceAsync(
            podDescriptor,
            @namespace: null,
            fieldSelector: $"spec.nodeName={nodeName}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Posts one Eviction. Never throws for an outcome the drain has a plan for — a
    /// PodDisruptionBudget's 429 is <em>correct behaviour</em>, not an error, and a pod
    /// that has already gone is the outcome the caller wanted.
    /// </summary>
    public async Task<EvictionResult> EvictPodAsync(
        ResourceDescriptor podDescriptor,
        string @namespace,
        string name,
        int? gracePeriodSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podDescriptor);

        using var content = new StringContent(
            NodeActions.EvictionBody(@namespace, name, gracePeriodSeconds), Encoding.UTF8, "application/json");

        using var response = await SendRequestAsync(
            HttpMethod.Post,
            podDescriptor.SubresourcePath(@namespace, name, NodeActions.EvictionSubresource),
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return new EvictionResult(EvictionOutcome.Accepted, "");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = KubernetesApiException.ReadStatusMessage(body);

        return response.StatusCode switch
        {
            // The drain only offers itself when discovery reports pods/eviction, so a
            // 404 here is about the pod, not about the endpoint: it is already gone,
            // which is the outcome asked for.
            HttpStatusCode.NotFound => new EvictionResult(EvictionOutcome.AlreadyGone, ""),

            // The eviction API's documented "not now": a PodDisruptionBudget would be
            // violated. Retrying is the correct response and the API server expects it.
            HttpStatusCode.TooManyRequests => new EvictionResult(
                EvictionOutcome.Blocked,
                message ?? "a PodDisruptionBudget currently forbids this eviction"),

            _ => new EvictionResult(
                EvictionOutcome.Failed,
                message ?? $"{(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}"),
        };
    }

    /// <summary>
    /// Drains a node: cordon, then evict every pod that may be evicted, retrying the
    /// ones a PodDisruptionBudget holds back, until the node is empty of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole of it streams — one <see cref="DrainProgress"/> per thing that happens —
    /// because a drain's duration is not bounded by anything this app controls and a
    /// progress bar that cannot say <em>what</em> it is waiting for is indistinguishable
    /// from a hang. A 429 from a PodDisruptionBudget is the specific case: it can last
    /// minutes or forever, it is correct, and it must read as "blocked by a
    /// PodDisruptionBudget, still retrying" rather than as a frozen window.
    /// </para>
    /// <para>
    /// Cancelling stops it where it is. That is a real state, not an error — the node
    /// stays cordoned and some pods are gone — and the caller is expected to say so
    /// rather than silently unwinding.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<DrainProgress> DrainNodeAsync(
        ResourceDescriptor nodeDescriptor,
        ResourceDescriptor podDescriptor,
        string nodeName,
        DrainOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeDescriptor);
        ArgumentNullException.ThrowIfNull(podDescriptor);
        ArgumentNullException.ThrowIfNull(options);

        // Cordon first, always. Evicting a pod from a node that still accepts work is a
        // way to make the scheduler put it back on the same node.
        await SetNodeSchedulableAsync(nodeDescriptor, nodeName, schedulable: false, cancellationToken)
            .ConfigureAwait(false);
        yield return DrainProgress.At(DrainStage.Cordoned, $"{nodeName} is cordoned — nothing new will schedule here.");

        var pods = await ListPodsOnNodeAsync(podDescriptor, nodeName, cancellationToken).ConfigureAwait(false);
        var plan = NodeActions.Plan(pods, options);
        yield return DrainProgress.At(DrainStage.Planned, plan.Summary()) with { Plan = plan };

        if (plan.IsBlocked)
        {
            // Refuse before evicting anything at all. Half a drain that then stops on a
            // question is worse than a question asked first, and kubectl refuses the
            // same way — it names every problem pod before it touches one.
            yield return DrainProgress.At(
                DrainStage.Refused,
                $"Nothing was evicted. {plan.BlockedCount} pod(s) need an option that was not given; "
                + "the node stays cordoned.") with { Plan = plan };
            yield break;
        }

        // Permanent failures: a 403 on the eviction subresource will not become a 200 by
        // being asked again, and a drain that retried it forever would look identical to
        // one blocked by a PodDisruptionBudget, which is the one distinction that matters
        // here.
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        var evicted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targets = plan.Pods
                .Where(p => p.Disposition is DrainDisposition.Evict or DrainDisposition.AlreadyTerminating)
                .Where(p => !failed.ContainsKey(p.Key))
                .ToList();

            if (targets.Count == 0)
            {
                break;
            }

            foreach (var pod in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (accepted.Contains(pod.Key) || pod.Disposition == DrainDisposition.AlreadyTerminating)
                {
                    // Already on its way out; the re-list below is what confirms it.
                    continue;
                }

                var result = await EvictPodAsync(
                    podDescriptor, pod.Namespace, pod.Name, options.GracePeriodSeconds, cancellationToken)
                    .ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case EvictionOutcome.Accepted:
                        accepted.Add(pod.Key);
                        evicted++;
                        yield return DrainProgress.At(DrainStage.PodEvicted, "eviction accepted", pod.Key);
                        break;

                    case EvictionOutcome.AlreadyGone:
                        accepted.Add(pod.Key);
                        yield return DrainProgress.At(DrainStage.PodGone, "already gone", pod.Key);
                        break;

                    case EvictionOutcome.Blocked:
                        yield return DrainProgress.At(DrainStage.PodBlocked, result.Message, pod.Key);
                        break;

                    default:
                        failed[pod.Key] = result.Message;
                        yield return DrainProgress.At(DrainStage.PodFailed, result.Message, pod.Key);
                        break;
                }
            }

            // Re-list rather than assume. It answers two questions at once — which of the
            // evictions have actually completed, and whether anything new landed here
            // (a DaemonSet controller and the kubelet both ignore cordon).
            await Task.Delay(DrainPollInterval, cancellationToken).ConfigureAwait(false);
            pods = await ListPodsOnNodeAsync(podDescriptor, nodeName, cancellationToken).ConfigureAwait(false);
            plan = NodeActions.Plan(pods, options);

            var remaining = plan.Pods.Count(p =>
                p.Disposition is DrainDisposition.Evict or DrainDisposition.AlreadyTerminating
                && !failed.ContainsKey(p.Key));

            if (remaining == 0)
            {
                break;
            }

            yield return DrainProgress.At(
                DrainStage.Waiting,
                remaining == 1
                    ? "1 pod still on the node."
                    : $"{remaining} pods still on the node.") with { Remaining = remaining, Evicted = evicted, Failed = failed.Count };
        }

        yield return DrainProgress.At(
            failed.Count == 0 ? DrainStage.Completed : DrainStage.CompletedWithFailures,
            failed.Count == 0
                ? $"{nodeName} is drained. {evicted} pod(s) evicted; the node stays cordoned until you uncordon it."
                : $"{nodeName} is not fully drained: {failed.Count} pod(s) could not be evicted. "
                  + $"{evicted} pod(s) were. The node stays cordoned.")
            with { Evicted = evicted, Failed = failed.Count, Plan = plan };
    }
}

/// <summary>What the API server said about one eviction.</summary>
public enum EvictionOutcome
{
    /// <summary>The eviction was accepted; the pod is terminating.</summary>
    Accepted,

    /// <summary>The pod was not there any more — the outcome asked for.</summary>
    AlreadyGone,

    /// <summary>A PodDisruptionBudget forbids it right now (HTTP 429). Correct, and worth retrying.</summary>
    Blocked,

    /// <summary>Anything else — RBAC, a webhook, a broken API server. Not worth retrying.</summary>
    Failed,
}

/// <summary>One eviction's outcome and, when it is not a success, the server's own sentence.</summary>
public sealed record EvictionResult(EvictionOutcome Outcome, string Message);

/// <summary>The kinds of thing a running drain reports.</summary>
public enum DrainStage
{
    /// <summary>The node has been made unschedulable.</summary>
    Cordoned,

    /// <summary>The plan, freshly computed from the pods actually on the node.</summary>
    Planned,

    /// <summary>The plan needs an option that was not given; nothing was evicted.</summary>
    Refused,

    /// <summary>The API server accepted this pod's eviction.</summary>
    PodEvicted,

    /// <summary>A PodDisruptionBudget forbids this pod's eviction for now; it will be retried.</summary>
    PodBlocked,

    /// <summary>This pod's eviction failed in a way retrying will not fix.</summary>
    PodFailed,

    /// <summary>This pod was already gone.</summary>
    PodGone,

    /// <summary>A pass finished with pods still on the node.</summary>
    Waiting,

    /// <summary>Every pod the drain was responsible for is gone.</summary>
    Completed,

    /// <summary>The drain finished, but some pods could not be evicted.</summary>
    CompletedWithFailures,
}

/// <summary>
/// One thing that happened during a drain. <see cref="PodKey"/> is set for the
/// per-pod stages and null for the ones about the node as a whole.
/// </summary>
public sealed record DrainProgress(DrainStage Stage, string Message, string? PodKey = null)
{
    /// <summary>Set on <see cref="DrainStage.Planned"/>, <see cref="DrainStage.Refused"/> and the terminal stages.</summary>
    public DrainPlan? Plan { get; init; }

    /// <summary>Pods still on the node at the last check.</summary>
    public int Remaining { get; init; }

    /// <summary>How many evictions have been accepted so far.</summary>
    public int Evicted { get; init; }

    /// <summary>How many pods have failed permanently so far.</summary>
    public int Failed { get; init; }

    internal static DrainProgress At(DrainStage stage, string message, string? podKey = null) =>
        new(stage, message, podKey);
}
