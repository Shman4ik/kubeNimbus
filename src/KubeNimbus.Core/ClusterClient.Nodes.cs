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
    /// How long the drain waits before asking again for an eviction a
    /// PodDisruptionBudget refused. That is the Eviction API's documented contract — a
    /// 429 means "not now, try again later" — and it is the only timer left in the drain:
    /// what the pods on the node are doing (terminating, gone, newly scheduled here) is
    /// observed through a field-selected watch rather than by re-listing. Five seconds is
    /// <c>kubectl drain</c>'s own retry interval after a 429.
    /// </summary>
    internal static TimeSpan EvictionRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

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
            fieldSelector: NodeActions.PodsOnNodeSelector(nodeName),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The pods on one node, live: the same field-selected population as
    /// <see cref="ListPodsOnNodeAsync"/>, through the informer loop every other live list
    /// uses. A pod the scheduler binds here arrives as an Added (it has just started
    /// matching <c>spec.nodeName</c>) and one that is deleted or evicted leaves as a
    /// Deleted, so neither the node pane nor the drain has to re-list to find out.
    /// </summary>
    public IAsyncEnumerable<ResourceEvent<DynamicResource>> WatchPodsOnNodeAsync(
        ResourceDescriptor podDescriptor,
        string nodeName,
        Action<Exception>? connectionLost = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podDescriptor);

        return WatchResourceAsync(
            podDescriptor,
            @namespace: null,
            connectionLost: connectionLost,
            cancellationToken: cancellationToken,
            fieldSelector: NodeActions.PodsOnNodeSelector(nodeName));
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

        // The pods on the node, kept current for the whole drain by one field-selected
        // watch: its initial list is the plan's input, and after that every eviction that
        // completes (a Deleted) and every pod that lands here anyway (an Added — a
        // DaemonSet controller and the kubelet both ignore cordon) arrives as it happens.
        await using var onNode = PodsOnNodeWatch.Start(this, podDescriptor, nodeName, cancellationToken);
        await onNode.Synced.WaitAsync(cancellationToken).ConfigureAwait(false);

        var (pods, version) = onNode.Snapshot();
        var plan = NodeActions.Plan(pods, options);
        yield return DrainProgress.At(DrainStage.Planned, plan.Summary) with { Plan = plan };

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

            var blocked = false;
            foreach (var pod in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (accepted.Contains(pod.Key) || pod.Disposition == DrainDisposition.AlreadyTerminating)
                {
                    // Already on its way out; the watch's Deleted is what confirms it.
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
                        blocked = true;
                        yield return DrainProgress.At(DrainStage.PodBlocked, result.Message, pod.Key);
                        break;

                    default:
                        failed[pod.Key] = result.Message;
                        yield return DrainProgress.At(DrainStage.PodFailed, result.Message, pod.Key);
                        break;
                }
            }

            // Wait for the watch to report something — an eviction completing, a pod
            // terminating, a pod landing here — rather than re-listing on a timer. The one
            // exception is a PodDisruptionBudget's 429, whose contract is "ask again later":
            // while a pod is held back, the wait also ends after the retry interval so it
            // is asked again even if nothing else on the node moves.
            var remaining = Remaining(plan, failed);
            var retryAt = DateTimeOffset.UtcNow + EvictionRetryInterval;
            while (remaining > 0)
            {
                // A deadline rather than a fresh interval per wake-up: a pod whose status
                // keeps ticking over must not postpone the retry of a blocked one forever.
                var wait = blocked ? Max(retryAt - DateTimeOffset.UtcNow, TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
                var changed = await onNode.WaitForChangeAsync(version, wait, cancellationToken).ConfigureAwait(false);
                (pods, version) = onNode.Snapshot();
                plan = NodeActions.Plan(pods, options);
                var now = Remaining(plan, failed);

                // A change that leaves the count where it was (a pod's status ticking
                // over while it terminates) is not worth a line on the strip, and neither
                // is it worth another eviction pass: only a count that moved, a timer
                // that expired, or a pod the drain has not asked about yet re-runs it.
                var hasUnaskedPod = plan.Pods.Any(p => p.Disposition == DrainDisposition.Evict
                    && !accepted.Contains(p.Key) && !failed.ContainsKey(p.Key));
                if (now != remaining || !changed || hasUnaskedPod)
                {
                    remaining = now;
                    break;
                }
            }

            if (remaining == 0)
            {
                break;
            }

            var problem = onNode.ConnectionProblem is { } lost ? $" {lost}" : "";
            yield return DrainProgress.At(
                DrainStage.Waiting,
                (remaining == 1
                    ? "1 pod still on the node."
                    : $"{remaining} pods still on the node.") + problem)
                with { Remaining = remaining, Evicted = evicted, Failed = failed.Count };
        }

        yield return DrainProgress.At(
            failed.Count == 0 ? DrainStage.Completed : DrainStage.CompletedWithFailures,
            failed.Count == 0
                ? $"{nodeName} is drained. {evicted} pod(s) evicted; the node stays cordoned until you uncordon it."
                : $"{nodeName} is not fully drained: {failed.Count} pod(s) could not be evicted. "
                  + $"{evicted} pod(s) were. The node stays cordoned.")
            with { Evicted = evicted, Failed = failed.Count, Plan = plan };
    }

    /// <summary>Pods the drain is still responsible for: to evict, or evicted and not gone yet.</summary>
    private static int Remaining(DrainPlan plan, Dictionary<string, string> failed) =>
        plan.Pods.Count(p =>
            p.Disposition is DrainDisposition.Evict or DrainDisposition.AlreadyTerminating
            && !failed.ContainsKey(p.Key));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// The drain's live view of the pods on its node: a field-selected watch folded into a
    /// dictionary, with a version counter the eviction loop waits on. Its own type rather
    /// than inline state in the iterator, because the watch has to be consumed on its own
    /// task — the drain spends most of its life awaiting an eviction or a change, not
    /// reading frames.
    /// </summary>
    private sealed class PodsOnNodeWatch : IAsyncDisposable
    {
        private readonly Dictionary<string, DynamicResource> _pods = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts;
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _version;
        private Task _pump = Task.CompletedTask;

        private PodsOnNodeWatch(CancellationToken cancellationToken) =>
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        /// <summary>Completes when the initial list has landed; faults if it never can.</summary>
        public Task Synced => _synced.Task;

        /// <summary>
        /// The watch's last connection failure after the initial list, until it recovers —
        /// appended to the drain's waiting line so a stalled count says why.
        /// </summary>
        public string? ConnectionProblem { get; private set; }

        public static PodsOnNodeWatch Start(
            ClusterClient client, ResourceDescriptor podDescriptor, string nodeName, CancellationToken cancellationToken)
        {
            var watch = new PodsOnNodeWatch(cancellationToken);
            watch._pump = Task.Run(() => watch.PumpAsync(client, podDescriptor, nodeName));
            return watch;
        }

        private async Task PumpAsync(ClusterClient client, ResourceDescriptor podDescriptor, string nodeName)
        {
            var token = _cts.Token;
            try
            {
                await foreach (var evt in client.WatchPodsOnNodeAsync(
                    podDescriptor, nodeName, ConnectionLost, token).ConfigureAwait(false))
                {
                    Apply(evt);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The drain finished, was stopped, or failed.
            }
            catch (Exception ex)
            {
                _synced.TrySetException(ex);
            }
        }

        /// <summary>
        /// Before the initial list has landed a lost connection is the drain's answer — a
        /// 403 on listing pods, or a server that is not there — and it fails the drain the
        /// way the one-shot list it replaces did. After that the watch reconnects on its
        /// own, and the failure is only worth saying while it lasts.
        /// </summary>
        private void ConnectionLost(Exception ex)
        {
            if (!_synced.Task.IsCompleted)
            {
                _synced.TrySetException(ex.InnerException ?? ex);
                _cts.Cancel();
                return;
            }

            ConnectionProblem = ex.Message;
            Changed();
        }

        /// <summary>
        /// Folds one frame in. A relist (the initial one, or after a 410) is collected
        /// aside and swapped in whole on Synced: the drain reads "no pods left" as
        /// "drained", so it must never be shown the half-filled dictionary a relist passes
        /// through on its way to complete.
        /// </summary>
        private void Apply(ResourceEvent<DynamicResource> evt)
        {
            lock (_gate)
            {
                switch (evt.Type)
                {
                    case ResourceEventType.Reset:
                        _relist = new(StringComparer.Ordinal);
                        return;
                    case ResourceEventType.Synced:
                        if (_relist is { } complete)
                        {
                            _pods.Clear();
                            foreach (var (key, pod) in complete)
                            {
                                _pods[key] = pod;
                            }

                            _relist = null;
                        }

                        ConnectionProblem = null;
                        _synced.TrySetResult();
                        break;
                    case ResourceEventType.Added when _relist is { } listing && evt.Resource is { } listed:
                        listing[Key(listed)] = listed;
                        return;
                    case ResourceEventType.Deleted when evt.Resource is { } gone:
                        _pods.Remove(Key(gone));
                        break;
                    case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } pod:
                        _pods[Key(pod)] = pod;
                        ConnectionProblem = null;
                        break;
                    default:
                        return;
                }
            }

            Changed();
        }

        private Dictionary<string, DynamicResource>? _relist;

        private void Changed()
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                _version++;
                previous = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }

        /// <summary>The pods on the node now, and the version they are as of.</summary>
        public (IReadOnlyList<DynamicResource> Pods, long Version) Snapshot()
        {
            lock (_gate)
            {
                return ([.. _pods.Values], _version);
            }
        }

        /// <summary>
        /// Waits until something has changed since <paramref name="since"/>, or until
        /// <paramref name="timeout"/> passes. True when something changed.
        /// </summary>
        public async Task<bool> WaitForChangeAsync(long since, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task changed;
            lock (_gate)
            {
                if (_version != since)
                {
                    return true;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private static string Key(DynamicResource pod) => $"{pod.Namespace}/{pod.Name}";

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
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
