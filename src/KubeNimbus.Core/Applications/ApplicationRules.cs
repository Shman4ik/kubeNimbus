using System.Globalization;
using System.Text.Json;

namespace KubeNimbus.Core.Applications;

/// <summary>
/// The Applications mode's rules: pure functions from what the cluster holds to a verdict, a
/// one-line reason and a list of findings with their evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, and read from status.</b> Every rule reads fields the API server
/// already holds — container states, conditions, rollout counters, Argo CD's own status — and
/// nothing else: no model, no heuristics over log text, no history this app kept. The same
/// cluster produces the same page every time, which is the whole value under time pressure.
/// Events are optional input: the list is built without them, and the page adds them as
/// evidence where one corroborates a finding.
/// </para>
/// <para>
/// <b>A finding is a fact, not a diagnosis.</b> Its title says what the cluster reports, its
/// evidence quotes the field it was read from, and the page's subtitle says as much. Nothing
/// here guesses at a cause the object does not state.
/// </para>
/// <para>
/// <b>Status is the most urgent implication.</b> Each finding carries the status it pushes
/// the app toward; the app takes the earliest in <see cref="AppStatus"/>'s order, folded with
/// Argo CD's own health when an Application tracks it. A finding that implies
/// <see cref="AppStatus.Healthy"/> (an OOM-kill an hour ago on a container that is running
/// again) is shown on the page and moves nothing.
/// </para>
/// </remarks>
public static class ApplicationRules
{
    /// <summary>A past termination newer than this is news; older, it is history (Info).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    /// <summary>A pod that became un-Ready this recently is still warming up, not failing.</summary>
    public static readonly TimeSpan WarmUpGrace = TimeSpan.FromMinutes(2);

    private const int MaxMarks = 40;

    public static ApplicationAssessment Evaluate(ApplicationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<Finding>();
        var allPods = input.Pods.Select(PodFacts.Read).ToList();
        var ready = 0;
        var desired = 0;
        var anyReplicaCount = false;
        var marks = new List<PodMark>();
        var healthyNotes = new List<string>();

        foreach (var workload in input.Workloads)
        {
            var pods = PodsOf(workload, allPods, input.Jobs);
            var before = findings.Count;
            var counts = Replicas(workload, pods, input.Jobs);
            if (counts is { } c)
            {
                anyReplicaCount = true;
                ready += c.Ready;
                desired += c.Desired;
            }

            findings.AddRange(PodRules(workload, pods, input));
            findings.AddRange(WorkloadRules(workload, pods, input, counts, healthyNotes));

            // The fallback: a workload short of Ready pods that nothing above explains still
            // has to say so, or the list would call it healthy on the strength of silence.
            if (counts is { } k && k.Ready < k.Desired
                && !findings.Skip(before).Any(f => f.Severity <= FindingSeverity.Warning || f.Implies == AppStatus.Progressing))
            {
                findings.Add(new Finding(
                    "unavailable",
                    FindingSeverity.Warning,
                    $"{k.Ready} of {k.Desired} pods of {workload.Kind} {workload.Name} are Ready",
                    "The controller reports fewer Ready pods than it wants, and no container or condition names a reason.",
                    [new Evidence($"{workload.Kind} {workload.Name} {k.ReadyField}", k.Ready.ToString(CultureInfo.InvariantCulture)),
                     new Evidence($"{workload.Kind} {workload.Name} {k.DesiredField}", k.Desired.ToString(CultureInfo.InvariantCulture))],
                    $"{k.Ready} of {k.Desired} pods Ready",
                    AppStatus.Degraded));
            }

            marks.AddRange(Marks(pods, counts));
        }

        if (input.Argo is { } argo)
        {
            findings.AddRange(ArgoRules(argo, findings));
        }

        var status = findings.Count == 0 ? AppStatus.Healthy : findings.Min(f => f.Implies);
        if (input.Argo is { } app && MapArgoHealth(app.Health) is { } fromArgo && fromArgo < status)
        {
            status = fromArgo;
        }

        var ordered = findings
            .Select((f, i) => (f, i))
            .OrderBy(x => x.f.Severity)
            .ThenBy(x => x.f.Implies)
            .ThenBy(x => x.i)
            .Select(x => x.f)
            .ToList();

        var live = allPods.Where(p => p.IsLive).ToList();
        return new ApplicationAssessment(
            status,
            Reason(status, ordered, ready, desired, anyReplicaCount, healthyNotes, input.Argo),
            ordered,
            ready,
            anyReplicaCount ? desired : -1,
            live.Sum(p => p.Restarts),
            live.SelectMany(p => p.Containers)
                .Where(c => c.RestartCount > 0)
                .Select(c => c.LastTerminated?.FinishedAt)
                .Where(t => t is not null)
                .Max(),
            marks.Take(MaxMarks).ToList(),
            LastDeploy(input));
    }

    // ------------------------------------------------------------------ pods

    /// <summary>
    /// The pods a workload runs: the ones its own selector matches in its namespace (the same
    /// answer <c>WorkloadLogsTabViewModel</c> asks the API server for), or for a CronJob, the
    /// pods of its Jobs.
    /// </summary>
    public static IReadOnlyList<PodFacts> PodsOf(DynamicResource workload, IReadOnlyList<PodFacts> pods, IReadOnlyList<DynamicResource> jobs)
    {
        if (workload.Kind == "CronJob")
        {
            var owned = jobs.Where(j => IsOwnedBy(j, workload)).Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
            return [.. pods.Where(p => p.Namespace == workload.Namespace
                && p.Pod.OwnerReferences.Any(o => o.Kind == "Job" && owned.Contains(o.Name)))];
        }

        if (LabelSelector.ForPodsOf(workload) is not { } selector)
        {
            return [];
        }

        return [.. pods.Where(p => p.Namespace == workload.Namespace && selector.Matches(p.Pod.Labels))];
    }

    internal static bool IsOwnedBy(DynamicResource child, DynamicResource owner) =>
        child.Namespace == owner.Namespace
        && child.OwnerReferences.Any(o => o.Kind == owner.Kind && o.Name == owner.Name
            && (o.Uid is null || owner.Uid is null || o.Uid == owner.Uid));

    private static IEnumerable<Finding> PodRules(DynamicResource workload, IReadOnlyList<PodFacts> pods, ApplicationInput input)
    {
        var now = input.Now;
        var live = pods.Where(p => p.IsLive).ToList();

        // OOM first: a container crash-looping *because* it is OOM-killed is reported once,
        // as the more specific of the two.
        var oomKilled = new HashSet<(string Pod, string Container)>();
        foreach (var group in live
            .SelectMany(p => p.Containers.Select(c => (Pod: p, Container: c)))
            .Where(x => x.Container.LatestTermination is { Reason: "OOMKilled" })
            .GroupBy(x => x.Container.Name))
        {
            var example = group.OrderByDescending(x => x.Container.LatestTermination!.FinishedAt).First();
            var ended = example.Container.LatestTermination!;
            var looping = group.Any(x => x.Container.IsCrashLooping);
            var recent = ended.FinishedAt is { } f && now - f <= RecentWindow;
            foreach (var x in group)
            {
                oomKilled.Add((x.Pod.Name, x.Container.Name));
            }

            var limit = example.Container.MemoryLimit;
            var count = group.Select(x => x.Pod.Name).Distinct().Count();
            var when = ended.FinishedAt is { } at ? AppTime.Ago(now, at) : "at an unrecorded time";
            yield return new Finding(
                "oom-killed",
                looping ? FindingSeverity.Error : recent ? FindingSeverity.Warning : FindingSeverity.Info,
                looping
                    ? $"Container {group.Key} is being killed for running out of memory"
                    : $"Container {group.Key} was killed for running out of memory {when}",
                (count == 1 ? $"In pod {example.Pod.Name}" : $"In {count} pods, most recently {example.Pod.Name}")
                    + $", the kernel OOM-killed it {when}. "
                    + (limit is null
                        ? "It declares no memory limit, so it was the node itself that ran out."
                        : $"Its memory limit is {limit}.")
                    + (looping ? " The kubelet is now backing off before each restart." : ""),
                [new Evidence($"{example.Pod.Name} {ContainerPath(example.Container)}.reason", "OOMKilled"),
                 new Evidence("finishedAt", J.Iso(ended.FinishedAt)),
                 new Evidence($"{group.Key} resources.limits.memory", limit ?? "not set"),
                 new Evidence("restartCount", example.Container.RestartCount.ToString(CultureInfo.InvariantCulture))],
                looping ? "OOM-killed, crash-looping" : $"OOM-killed {when}",
                looping ? AppStatus.Degraded : AppStatus.Healthy,
                example.Pod.Name,
                group.Key);
        }

        var crashing = live
            .SelectMany(p => p.Containers.Select(c => (Pod: p, Container: c)))
            .Where(x => x.Container.IsCrashLooping && !oomKilled.Contains((x.Pod.Name, x.Container.Name)))
            .OrderByDescending(x => x.Container.RestartCount)
            .ToList();
        if (crashing.Count > 0)
        {
            var (pod, container) = crashing[0];
            var pods2 = crashing.Select(x => x.Pod.Name).Distinct().Count();
            var ended = container.LastTerminated;
            var evidence = new List<Evidence>
            {
                new($"{pod.Name} {container.Name} state.waiting.reason", "CrashLoopBackOff"),
            };
            if (ended is not null)
            {
                evidence.Add(new Evidence("lastState.terminated", $"{ended.ExitText}, finishedAt {J.Iso(ended.FinishedAt)}"));
            }

            evidence.Add(new Evidence("restartCount", container.RestartCount.ToString(CultureInfo.InvariantCulture)));
            if (EventFor(input.Events, "Pod", pod.Name, pod.Namespace, "BackOff") is { } backOff)
            {
                evidence.Add(EventEvidence(backOff));
            }

            var lived = ended is { StartedAt: { } s, FinishedAt: { } f } ? f - s : (TimeSpan?)null;
            yield return new Finding(
                "crash-loop",
                FindingSeverity.Error,
                pods2 == 1 ? "1 pod is crash-looping" : $"{pods2} pods are crash-looping",
                $"Container {container.Name} in {pod.Name} "
                    + (ended is null
                        ? "keeps exiting"
                        : $"exited with {ended.ExitText}"
                          + (lived is { } l ? $" {AppTime.Span(l)} after it started" : "")
                          + (ended.FinishedAt is { } at ? $", {AppTime.Ago(now, at)}" : ""))
                    + $". It has restarted {container.RestartCount} time{(container.RestartCount == 1 ? "" : "s")}, and the kubelet is backing off before the next start.",
                evidence,
                ended?.ExitCode is { } code ? $"Crash-looping (exit {code})" : "Crash-looping",
                AppStatus.Degraded,
                pod.Name,
                container.Name);
        }

        foreach (var group in live
            .SelectMany(p => p.Containers.Concat(p.InitContainers).Select(c => (Pod: p, Container: c)))
            .Where(x => x.Container.State == ContainerStateKind.Waiting
                && x.Container.WaitingReason is "ErrImagePull" or "ImagePullBackOff" or "InvalidImageName" or "ErrImageNeverPull")
            .GroupBy(x => x.Container.Image))
        {
            var (pod, container) = group.First();
            var count = group.Select(x => x.Pod.Name).Distinct().Count();
            var evidence = new List<Evidence>
            {
                new($"{pod.Name} {container.Name} state.waiting.reason", container.WaitingReason),
                new($"{container.Name} image", container.Image),
            };
            if (container.WaitingMessage.Length > 0)
            {
                evidence.Add(new Evidence("state.waiting.message", container.WaitingMessage));
            }

            yield return new Finding(
                "image-pull",
                FindingSeverity.Error,
                count == 1 ? "The image cannot be pulled" : $"The image cannot be pulled for {count} pods",
                $"Container {container.Name} in {pod.Name} is waiting for {container.Image} and has never started."
                    + (container.WaitingMessage.Length > 0 ? $" The kubelet says: {container.WaitingMessage}" : ""),
                evidence,
                $"Image pull failing: {container.Image}",
                AppStatus.Degraded,
                pod.Name,
                container.Name);
        }

        foreach (var group in live
            .SelectMany(p => p.Containers.Concat(p.InitContainers).Select(c => (Pod: p, Container: c)))
            .Where(x => x.Container.State == ContainerStateKind.Waiting
                && x.Container.WaitingReason is "CreateContainerConfigError" or "CreateContainerError" or "RunContainerError")
            .GroupBy(x => (x.Container.WaitingReason, x.Container.WaitingMessage)))
        {
            var (pod, container) = group.First();
            var count = group.Select(x => x.Pod.Name).Distinct().Count();
            yield return new Finding(
                "container-config",
                FindingSeverity.Error,
                count == 1 ? "A container cannot be created" : $"Containers cannot be created in {count} pods",
                $"Container {container.Name} in {pod.Name} is waiting with {container.WaitingReason}."
                    + (container.WaitingMessage.Length > 0 ? $" The kubelet says: {container.WaitingMessage}" : ""),
                [new Evidence($"{pod.Name} {container.Name} state.waiting.reason", container.WaitingReason),
                 new Evidence("state.waiting.message", container.WaitingMessage)],
                container.WaitingMessage.Length > 0 ? $"{container.WaitingReason}: {container.WaitingMessage}" : container.WaitingReason,
                AppStatus.Degraded,
                pod.Name,
                container.Name);
        }

        var unscheduled = live.Where(p => p.Unschedulable).ToList();
        if (unscheduled.Count > 0)
        {
            var example = unscheduled[0];
            var evidence = new List<Evidence>
            {
                new($"{example.Name} condition PodScheduled", "False (Unschedulable)"),
            };
            if (example.ScheduledMessage.Length > 0)
            {
                evidence.Add(new Evidence("message", example.ScheduledMessage));
            }

            if (EventFor(input.Events, "Pod", example.Name, example.Namespace, "FailedScheduling") is { } failed)
            {
                evidence.Add(EventEvidence(failed));
            }

            var scheduled = live.Count - unscheduled.Count;
            yield return new Finding(
                "unschedulable",
                scheduled == 0 ? FindingSeverity.Error : FindingSeverity.Warning,
                scheduled == 0
                    ? (unscheduled.Count == 1 ? "The pod cannot be scheduled" : $"None of {unscheduled.Count} pods can be scheduled")
                    : $"{unscheduled.Count} of {live.Count} pods cannot be scheduled",
                "The scheduler found no node for "
                    + (unscheduled.Count == 1 ? example.Name : $"{unscheduled.Count} pods, e.g. {example.Name}")
                    + (example.ScheduledMessage.Length > 0 ? $". It reports: {example.ScheduledMessage}" : "."),
                evidence,
                $"{scheduled} of {live.Count} pods scheduled" + (SchedulerTail(example.ScheduledMessage) is { Length: > 0 } tail ? $": {tail}" : ""),
                AppStatus.Degraded,
                example.Name);
        }

        var notReady = live
            .Where(p => p.Phase == "Running" && !p.Ready
                && p.Containers.Count > 0
                && p.Containers.All(c => c.State == ContainerStateKind.Running))
            .ToList();
        if (notReady.Count > 0)
        {
            var example = notReady[0];
            var container = example.Containers.FirstOrDefault(c => !c.Ready) ?? example.Containers[0];
            var since = example.ReadySince ?? container.RunningSince;
            var warming = since is { } s && now - s < WarmUpGrace;
            var evidence = new List<Evidence>
            {
                new($"{example.Name} condition Ready", "False"),
                new($"{container.Name} ready", "false"),
            };
            if (EventFor(input.Events, "Pod", example.Name, example.Namespace, "Unhealthy") is { } unhealthy)
            {
                evidence.Add(EventEvidence(unhealthy));
            }

            yield return new Finding(
                "not-ready",
                warming ? FindingSeverity.Info : FindingSeverity.Warning,
                notReady.Count == 1 ? "1 pod is running but not Ready" : $"{notReady.Count} pods are running but not Ready",
                $"Every container in {example.Name} is running, but {container.Name} is not Ready"
                    + (container.HasReadinessProbe ? ", so its readiness probe is not passing" : "")
                    + (since is { } t ? $" (since {AppTime.Ago(now, t)})" : "")
                    + ". A pod that is not Ready receives no traffic from its Services.",
                evidence,
                notReady.Count == 1 ? "1 pod not Ready" : $"{notReady.Count} pods not Ready",
                warming ? AppStatus.Progressing : AppStatus.Degraded,
                example.Name,
                container.Name);
        }
    }

    private static string ContainerPath(ContainerFacts container) =>
        container.State == ContainerStateKind.Terminated && container.LatestTermination == container.Terminated
            ? $"{container.Name} state.terminated"
            : $"{container.Name} lastState.terminated";

    /// <summary>
    /// The part of a scheduler message after "N/M nodes are available:" and before the
    /// "preemption:" clause — the half that names the shortage.
    /// </summary>
    internal static string SchedulerTail(string message)
    {
        var text = message;
        var colon = text.IndexOf("available:", StringComparison.Ordinal);
        if (colon >= 0)
        {
            text = text[(colon + "available:".Length)..];
        }

        var preemption = text.IndexOf("preemption:", StringComparison.Ordinal);
        if (preemption >= 0)
        {
            text = text[..preemption];
        }

        return text.Trim().TrimEnd('.').Trim();
    }

    // ------------------------------------------------------------- workloads

    private sealed record ReplicaCounts(int Ready, int Desired, string ReadyField, string DesiredField);

    private static ReplicaCounts? Replicas(DynamicResource workload, IReadOnlyList<PodFacts> pods, IReadOnlyList<DynamicResource> jobs)
    {
        var spec = J.Obj(workload.Raw, "spec");
        var status = J.Obj(workload.Raw, "status");
        return workload.Kind switch
        {
            "Deployment" or "StatefulSet" or "ReplicaSet" => new ReplicaCounts(
                J.Int(status, "readyReplicas") ?? 0, J.Int(spec, "replicas") ?? 1,
                "status.readyReplicas", "spec.replicas"),
            "DaemonSet" => new ReplicaCounts(
                J.Int(status, "numberReady") ?? 0, J.Int(status, "desiredNumberScheduled") ?? 0,
                "status.numberReady", "status.desiredNumberScheduled"),
            _ => null,
        };
    }

    private static IEnumerable<Finding> WorkloadRules(
        DynamicResource workload, IReadOnlyList<PodFacts> pods, ApplicationInput input, ReplicaCounts? counts, List<string> healthyNotes)
    {
        return workload.Kind switch
        {
            "Deployment" => DeploymentRules(workload, pods, input, counts!),
            "StatefulSet" => StatefulSetRules(workload),
            "DaemonSet" => DaemonSetRules(workload),
            "Job" => JobRules(workload, input.Now, healthyNotes, owner: null),
            "CronJob" => CronJobRules(workload, input, healthyNotes),
            _ => [],
        };
    }

    private static IEnumerable<Finding> DeploymentRules(
        DynamicResource deployment, IReadOnlyList<PodFacts> pods, ApplicationInput input, ReplicaCounts counts)
    {
        var now = input.Now;
        var spec = J.Obj(deployment.Raw, "spec");
        var status = J.Obj(deployment.Raw, "status");
        var name = deployment.Name;
        var replicaSets = input.ReplicaSets.Where(rs => IsOwnedBy(rs, deployment)).ToList();
        var newest = replicaSets.OrderByDescending(Revision).FirstOrDefault();

        // Pods the controller could not create — nearly always a ResourceQuota.
        var failure = J.Condition(status, "ReplicaFailure");
        var failureSource = $"Deployment {name}";
        if (J.Str(failure, "status") != "True")
        {
            foreach (var rs in replicaSets)
            {
                var c = J.Condition(J.Obj(rs.Raw, "status"), "ReplicaFailure");
                if (J.Str(c, "status") == "True")
                {
                    failure = c;
                    failureSource = $"ReplicaSet {rs.Name}";
                    break;
                }
            }
        }

        if (J.Str(failure, "status") == "True")
        {
            var message = J.Str(failure, "message");
            var reason = J.Str(failure, "reason");
            var missing = Math.Max(0, counts.Desired - pods.Count(p => p.IsLive));
            var quota = message.Contains("exceeded quota", StringComparison.OrdinalIgnoreCase);
            var evidence = new List<Evidence>
            {
                new($"{failureSource} condition ReplicaFailure", $"True ({reason})"),
            };
            if (message.Length > 0)
            {
                evidence.Add(new Evidence("message", message));
            }

            foreach (var rs in replicaSets)
            {
                if (EventFor(input.Events, "ReplicaSet", rs.Name, rs.Namespace, "FailedCreate") is { } e)
                {
                    evidence.Add(EventEvidence(e));
                    break;
                }
            }

            yield return new Finding(
                "replica-failure",
                FindingSeverity.Error,
                missing > 0 ? $"{missing} of {counts.Desired} pods were never created" : "Pods cannot be created",
                quota
                    ? "The namespace's ResourceQuota is used up, so the ReplicaSet cannot create the rest of its pods."
                    : "The ReplicaSet's requests to create pods are being refused.",
                evidence,
                (missing > 0 ? $"{missing} pods not created" : "Pods not created") + (quota ? ": namespace quota" : reason.Length > 0 ? $": {reason}" : ""),
                AppStatus.Degraded);
        }

        var progressing = J.Condition(status, "Progressing");
        var stalled = J.Str(progressing, "status") == "False" && J.Str(progressing, "reason") == "ProgressDeadlineExceeded";
        if (stalled)
        {
            var since = J.Time(progressing, "lastTransitionTime") ?? J.Time(progressing, "lastUpdateTime");
            var deadline = J.Int(spec, "progressDeadlineSeconds") ?? 600;
            yield return new Finding(
                "progress-deadline",
                FindingSeverity.Error,
                "The rollout has stalled",
                $"Deployment {name} made no progress for longer than its progress deadline ({deadline}s)"
                    + (since is { } s ? $" and gave up {AppTime.Ago(now, s)}" : "")
                    + ". Kubernetes does not retry on its own; the rollout stays where it stopped.",
                [new Evidence($"Deployment {name} condition Progressing", "False (ProgressDeadlineExceeded)"),
                 new Evidence("message", J.Str(progressing, "message")),
                 new Evidence("lastTransitionTime", J.Iso(since))],
                since is { } t ? $"Rollout stalled for {AppTime.Span(now - t)}" : "Rollout stalled",
                AppStatus.Stalled);
        }

        if (J.Bool(spec, "paused"))
        {
            yield return new Finding(
                "rollout-paused",
                FindingSeverity.Info,
                "The rollout is paused",
                $"Deployment {name} has spec.paused set, so changes to its pod template are not rolled out until it is resumed.",
                [new Evidence($"Deployment {name} spec.paused", "true")],
                "Rollout paused",
                AppStatus.Suspended);
        }

        var desired = counts.Desired;
        var updated = J.Int(status, "updatedReplicas") ?? 0;
        var total = J.Int(status, "replicas") ?? 0;
        var generation = J.Int(J.Obj(deployment.Raw, "metadata"), "generation");
        var observed = J.Int(status, "observedGeneration");
        var rolling = (generation is { } g && observed is { } o && o < g)
            || updated < desired
            || total > updated;
        if (!rolling || J.Bool(spec, "paused"))
        {
            yield break;
        }

        var revision = input.Argo is { IsOperationRunning: true } running && running.ShortRevision.Length > 0
            ? running.ShortRevision
            : newest is not null ? $"rev {Revision(newest)}" : "";
        if (!stalled)
        {
            yield return new Finding(
                "rollout",
                FindingSeverity.Info,
                $"Rollout in progress: {updated} of {desired} pods updated",
                $"Deployment {name} is replacing its pods"
                    + (newest is not null ? $" with ReplicaSet {newest.Name} (revision {Revision(newest)})" : "")
                    + $"; {updated} of {desired} run the new template.",
                [new Evidence($"Deployment {name} status.updatedReplicas", updated.ToString(CultureInfo.InvariantCulture)),
                 new Evidence("spec.replicas", desired.ToString(CultureInfo.InvariantCulture)),
                 new Evidence("status.replicas", total.ToString(CultureInfo.InvariantCulture))],
                $"Rolling out {revision}".TrimEnd() + $" · {updated} of {desired} pods updated",
                AppStatus.Progressing);
        }

        if (newest is not null)
        {
            var newReady = J.Int(J.Obj(newest.Raw, "status"), "readyReplicas") ?? 0;
            var old = replicaSets.Where(rs => rs != newest)
                .Select(rs => (Rs: rs, Ready: J.Int(J.Obj(rs.Raw, "status"), "readyReplicas") ?? 0))
                .Where(x => x.Ready > 0)
                .ToList();
            var oldReady = old.Sum(x => x.Ready);
            if (newReady == 0 && oldReady > 0)
            {
                var evidence = new List<Evidence>
                {
                    new($"ReplicaSet {newest.Name} status.readyReplicas", "0"),
                };
                evidence.AddRange(old.Select(x => new Evidence(
                    $"ReplicaSet {x.Rs.Name} (rev {Revision(x.Rs)}) status.readyReplicas",
                    x.Ready.ToString(CultureInfo.InvariantCulture))));
                yield return new Finding(
                    "old-version-serving",
                    FindingSeverity.Warning,
                    "The old version still serves traffic alone",
                    $"The new ReplicaSet {newest.Name} (revision {Revision(newest)}) has no Ready pod; "
                        + $"all {oldReady} Ready pod{(oldReady == 1 ? "" : "s")} run the previous version.",
                    evidence,
                    "Old version still serving",
                    AppStatus.Progressing);
            }
        }
    }

    private static IEnumerable<Finding> StatefulSetRules(DynamicResource set)
    {
        var spec = J.Obj(set.Raw, "spec");
        var status = J.Obj(set.Raw, "status");
        var current = J.Str(status, "currentRevision");
        var update = J.Str(status, "updateRevision");
        var desired = J.Int(spec, "replicas") ?? 1;
        var updated = J.Int(status, "updatedReplicas") ?? 0;
        if (current.Length == 0 || update.Length == 0 || current == update || updated >= desired)
        {
            yield break;
        }

        var strategy = J.Obj(spec, "updateStrategy");
        var onDelete = J.Str(strategy, "type") == "OnDelete";
        var partition = J.Int(J.Obj(strategy, "rollingUpdate"), "partition") ?? 0;
        yield return new Finding(
            "revision-lag",
            FindingSeverity.Info,
            $"Update in progress: {updated} of {desired} pods on the new revision",
            $"StatefulSet {set.Name} is moving from revision {current} to {update}."
                + (onDelete ? " Its update strategy is OnDelete, so a pod moves only when it is deleted." : "")
                + (partition > 0 ? $" Partition {partition} keeps ordinals below it on the old revision." : ""),
            [new Evidence($"StatefulSet {set.Name} status.currentRevision", current),
             new Evidence("status.updateRevision", update),
             new Evidence("status.updatedReplicas", updated.ToString(CultureInfo.InvariantCulture))],
            $"Updating · {updated} of {desired} pods on {update}",
            onDelete || partition > 0 ? AppStatus.Healthy : AppStatus.Progressing);
    }

    private static IEnumerable<Finding> DaemonSetRules(DynamicResource set)
    {
        var status = J.Obj(set.Raw, "status");
        var desired = J.Int(status, "desiredNumberScheduled") ?? 0;
        var updated = J.Int(status, "updatedNumberScheduled") ?? desired;
        if (updated >= desired)
        {
            yield break;
        }

        var onDelete = J.Str(J.Obj(J.Obj(set.Raw, "spec"), "updateStrategy"), "type") == "OnDelete";
        yield return new Finding(
            "revision-lag",
            FindingSeverity.Info,
            $"Update in progress: {updated} of {desired} nodes on the new revision",
            $"DaemonSet {set.Name} runs the updated template on {updated} of the {desired} nodes it is scheduled to."
                + (onDelete ? " Its update strategy is OnDelete, so a pod moves only when it is deleted." : ""),
            [new Evidence($"DaemonSet {set.Name} status.updatedNumberScheduled", updated.ToString(CultureInfo.InvariantCulture)),
             new Evidence("status.desiredNumberScheduled", desired.ToString(CultureInfo.InvariantCulture))],
            $"Updating · {updated} of {desired} nodes",
            onDelete ? AppStatus.Healthy : AppStatus.Progressing);
    }

    private static IEnumerable<Finding> JobRules(DynamicResource job, DateTimeOffset now, List<string> healthyNotes, DynamicResource? owner)
    {
        var status = J.Obj(job.Raw, "status");
        var failed = J.Condition(status, "Failed");
        var label = owner is null ? $"Job {job.Name}" : $"The last run of CronJob {owner.Name} ({job.Name})";
        if (J.Str(failed, "status") == "True")
        {
            var reason = J.Str(failed, "reason");
            var at = J.Time(failed, "lastTransitionTime");
            yield return new Finding(
                "job-failed",
                FindingSeverity.Error,
                $"{label} failed" + (reason.Length > 0 ? $" ({reason})" : ""),
                reason switch
                {
                    "BackoffLimitExceeded" => "Its pods failed more times than spec.backoffLimit allows, so the Job stopped retrying.",
                    "DeadlineExceeded" => "It ran longer than spec.activeDeadlineSeconds, so its pods were stopped.",
                    _ => "The Job controller marked it failed.",
                } + (at is { } t ? $" That was {AppTime.Ago(now, t)}." : ""),
                [new Evidence($"Job {job.Name} condition Failed", $"True ({reason})"),
                 new Evidence("message", J.Str(failed, "message")),
                 new Evidence("status.failed", (J.Int(status, "failed") ?? 0).ToString(CultureInfo.InvariantCulture))],
                $"{(owner is null ? "Job" : "Last run")} failed" + (reason.Length > 0 ? $": {reason}" : ""),
                AppStatus.Degraded);
            yield break;
        }

        if (J.Str(J.Condition(status, "Complete"), "status") == "True")
        {
            var done = J.Time(status, "completionTime");
            healthyNotes.Add(owner is null
                ? "Completed" + (done is { } d ? $" {AppTime.Ago(now, d)}" : "")
                : "Last run succeeded" + (done is { } d2 ? $" {AppTime.Ago(now, d2)}" : ""));
            yield break;
        }

        if (J.Bool(J.Obj(job.Raw, "spec"), "suspend"))
        {
            yield return new Finding(
                "job-suspended",
                FindingSeverity.Info,
                $"{label} is suspended",
                "spec.suspend is set, so the Job creates no pods until it is resumed.",
                [new Evidence($"Job {job.Name} spec.suspend", "true")],
                "Suspended",
                AppStatus.Suspended);
            yield break;
        }

        var active = J.Int(status, "active") ?? 0;
        if (active > 0)
        {
            var started = J.Time(status, "startTime");
            yield return new Finding(
                "job-running",
                FindingSeverity.Info,
                $"{label} is running",
                $"{active} pod{(active == 1 ? " is" : "s are")} active" + (started is { } s ? $"; it started {AppTime.Ago(now, s)}." : "."),
                [new Evidence($"Job {job.Name} status.active", active.ToString(CultureInfo.InvariantCulture))],
                "Running now",
                AppStatus.Progressing);
        }
    }

    private static IEnumerable<Finding> CronJobRules(DynamicResource cron, ApplicationInput input, List<string> healthyNotes)
    {
        var spec = J.Obj(cron.Raw, "spec");
        if (J.Bool(spec, "suspend"))
        {
            yield return new Finding(
                "cronjob-suspended",
                FindingSeverity.Info,
                $"CronJob {cron.Name} is suspended",
                $"spec.suspend is set, so no new runs are scheduled (schedule \"{J.Str(spec, "schedule")}\").",
                [new Evidence($"CronJob {cron.Name} spec.suspend", "true")],
                "CronJob suspended",
                AppStatus.Suspended);
        }

        var last = input.Jobs.Where(j => IsOwnedBy(j, cron))
            .OrderByDescending(j => j.CreationTimestamp ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (last is null)
        {
            var scheduled = J.Time(J.Obj(cron.Raw, "status"), "lastScheduleTime");
            healthyNotes.Add(scheduled is { } s ? $"Last scheduled {AppTime.Ago(input.Now, s)}" : "No run yet");
            yield break;
        }

        foreach (var finding in JobRules(last, input.Now, healthyNotes, cron))
        {
            yield return finding;
        }
    }

    // ------------------------------------------------------------------ Argo

    private static AppStatus? MapArgoHealth(ArgoHealthState health) => health switch
    {
        ArgoHealthState.Degraded => AppStatus.Degraded,
        ArgoHealthState.Missing => AppStatus.Missing,
        ArgoHealthState.Progressing => AppStatus.Progressing,
        ArgoHealthState.Suspended => AppStatus.Suspended,
        ArgoHealthState.Unknown => AppStatus.Unknown,
        _ => null,
    };

    private static IEnumerable<Finding> ArgoRules(ArgoApplication argo, IReadOnlyList<Finding> workloadFindings)
    {
        if (argo.OperationPhase is "Failed" or "Error")
        {
            yield return new Finding(
                "argo-sync-failed",
                FindingSeverity.Error,
                "The last sync failed",
                "Argo CD's last sync operation ended in " + argo.OperationPhase
                    + (argo.OperationMessage.Length > 0 ? $": {argo.OperationMessage}" : "."),
                [new Evidence("status.operationState.phase", argo.OperationPhase),
                 new Evidence("status.operationState.message", argo.OperationMessage),
                 new Evidence("status.operationState.finishedAt", J.Iso(argo.OperationFinishedAt))],
                "Sync failed" + (argo.OperationMessage.Length > 0 ? $": {FirstLine(argo.OperationMessage)}" : ""),
                AppStatus.SyncFailed);
        }

        foreach (var condition in argo.Conditions.Where(c => c.Type == "ComparisonError"))
        {
            yield return new Finding(
                "argo-comparison-error",
                FindingSeverity.Error,
                "Argo CD cannot compare this app with Git",
                "Until it can, neither its sync status nor its health can be trusted: " + condition.Message,
                [new Evidence("status.conditions[type=ComparisonError].message", condition.Message)],
                "Argo CD cannot compare: " + FirstLine(condition.Message),
                AppStatus.Unknown);
        }

        foreach (var condition in argo.Conditions.Where(c => c.IsProblem && c.Type != "ComparisonError"))
        {
            yield return new Finding(
                "argo-condition",
                FindingSeverity.Warning,
                $"Argo CD reports {condition.Type}",
                condition.Message,
                [new Evidence($"status.conditions[type={condition.Type}].message", condition.Message)],
                $"{condition.Type}: {FirstLine(condition.Message)}",
                AppStatus.Healthy);
        }

        if (argo.Health == ArgoHealthState.Missing || argo.Resources.Any(r => r.Health == ArgoHealthState.Missing))
        {
            var missing = argo.Resources.Where(r => r.Health == ArgoHealthState.Missing).ToList();
            yield return new Finding(
                "argo-missing",
                FindingSeverity.Error,
                missing.Count switch
                {
                    0 => "Declared in Git, absent from the cluster",
                    1 => $"{missing[0].Kind} {missing[0].Name} is declared in Git but absent from the cluster",
                    _ => $"{missing.Count} resources are declared in Git but absent from the cluster",
                },
                "Argo CD reports the app's health as Missing: what Git declares has not been created here.",
                [new Evidence("status.health.status", argo.Health.ToString()),
                 .. missing.Take(5).Select(r => new Evidence($"status.resources {r.Kind}/{r.Name} health", "Missing"))],
                missing.Count > 0 ? $"Missing: {missing[0].Kind} {missing[0].Name}" : "Missing from the cluster",
                AppStatus.Missing);
        }

        if (argo.Sync == ArgoSyncState.OutOfSync)
        {
            var drift = argo.Resources.Where(r => r.Sync == ArgoSyncState.OutOfSync).ToList();
            var syncing = argo.IsOperationRunning;
            var evidence = new List<Evidence> { new("status.sync.status", "OutOfSync") };
            evidence.AddRange(drift.Take(5).Select(r => new Evidence($"status.resources {r.Kind}/{r.Name} status", "OutOfSync")));
            if (drift.Count > 5)
            {
                evidence.Add(new Evidence($"+{drift.Count - 5} more OutOfSync", ""));
            }

            yield return new Finding(
                "argo-out-of-sync",
                syncing ? FindingSeverity.Info : FindingSeverity.Warning,
                drift.Count switch
                {
                    0 => "The cluster differs from Git",
                    1 => "1 resource differs from Git",
                    _ => $"{drift.Count} resources differ from Git",
                },
                syncing
                    ? "A sync is running now, so the difference is expected to close."
                    : argo.AutoSync
                        ? "Auto-sync is on, so Argo CD should reconcile it; if this persists, the sync itself is not succeeding."
                        : "Auto-sync is off for this app, so it stays this way until someone syncs it.",
                evidence,
                syncing ? "Sync in progress" : argo.AutoSync ? "Differs from Git" : "Differs from Git · auto-sync is off",
                syncing ? AppStatus.Progressing : AppStatus.OutOfSync);
        }

        // Argo's own verdict, stated when nothing read from the workloads already explains it.
        if (argo.Health is ArgoHealthState.Degraded or ArgoHealthState.Unknown or ArgoHealthState.Progressing or ArgoHealthState.Suspended
            && !workloadFindings.Any(f => f.Implies == MapArgoHealth(argo.Health)))
        {
            var degraded = argo.Resources.Where(r => r.Health == argo.Health).ToList();
            var status = MapArgoHealth(argo.Health)!.Value;
            var evidence = new List<Evidence> { new("status.health.status", argo.Health.ToString()) };
            if (argo.HealthMessage.Length > 0)
            {
                evidence.Add(new Evidence("status.health.message", argo.HealthMessage));
            }

            evidence.AddRange(degraded.Take(5).Select(r => new Evidence($"status.resources {r.Kind}/{r.Name} health", r.Health.ToString())));
            yield return new Finding(
                "argo-health",
                argo.Health is ArgoHealthState.Degraded or ArgoHealthState.Unknown ? FindingSeverity.Warning : FindingSeverity.Info,
                $"Argo CD reports this app {argo.Health}",
                degraded.Count > 0
                    ? $"Argo CD's health check marks {string.Join(", ", degraded.Take(3).Select(r => $"{r.Kind} {r.Name}"))} {argo.Health}."
                    : argo.HealthMessage.Length > 0 ? argo.HealthMessage : "Argo CD gives no further detail.",
                evidence,
                $"{argo.Health} per Argo CD",
                status);
        }
    }

    // ------------------------------------------------------------- summaries

    private static string Reason(
        AppStatus status, IReadOnlyList<Finding> ordered, int ready, int desired, bool anyReplicaCount,
        IReadOnlyList<string> healthyNotes, ArgoApplication? argo)
    {
        var news = ordered
            .Where(f => f.Implies != AppStatus.Healthy || f.Severity <= FindingSeverity.Warning)
            .OrderBy(f => f.Implies)
            .ThenBy(f => f.Severity)
            .ToList();
        if (news.Count > 0)
        {
            var first = news[0];
            var second = news.Skip(1).FirstOrDefault(f => f.Short != first.Short
                && (f.Severity <= FindingSeverity.Warning || f.Implies.NeedsAttention()));
            return second is null ? first.Short : $"{first.Short} · {second.Short}";
        }

        if (status != AppStatus.Healthy && argo is not null)
        {
            return $"{argo.Health} per Argo CD";
        }

        if (anyReplicaCount)
        {
            return desired == 0 ? "Scaled to zero" : $"{ready}/{desired} pods ready";
        }

        if (healthyNotes.Count > 0)
        {
            return healthyNotes[0];
        }

        return argo is not null ? $"{argo.Sync} · {argo.Health} per Argo CD · no workloads here" : "Nothing running";
    }

    private static IEnumerable<PodMark> Marks(IReadOnlyList<PodFacts> pods, ReplicaCounts? counts)
    {
        var shown = pods.Where(p => p.IsLive || p.Terminating).ToList();
        foreach (var pod in shown.OrderBy(p => p.DisplayState).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            yield return new PodMark(pod.Name, pod.DisplayState, $"{pod.Name} — {pod.StateText}");
        }

        if (counts is { } c)
        {
            var missing = c.Desired - pods.Count(p => p.IsLive);
            for (var i = 0; i < missing; i++)
            {
                yield return new PodMark("", null, "A desired replica with no pod — it was never created");
            }
        }
    }

    private static DeployMark? LastDeploy(ApplicationInput input)
    {
        if (input.Argo is { History.Count: > 0 } argo && argo.History[0] is { } latest)
        {
            return new DeployMark(RevisionLabel(latest.Revision, IsChartSource(argo)), latest.DeployedAt, "Argo CD");
        }

        var newest = input.Workloads
            .Where(w => w.Kind == "Deployment")
            .SelectMany(d => input.ReplicaSets.Where(rs => IsOwnedBy(rs, d)).OrderByDescending(Revision).Take(1))
            .OrderByDescending(rs => rs.CreationTimestamp ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (newest is not null)
        {
            return new DeployMark($"rev {Revision(newest)}", newest.CreationTimestamp, "ReplicaSet");
        }

        return input.Workloads.FirstOrDefault(w => w.Kind == "Job") is { } job
            ? new DeployMark("", job.CreationTimestamp, "Job")
            : null;
    }

    /// <summary>A short SHA, or a chart version prefixed "chart" — never a 40-character hash in a column.</summary>
    public static string RevisionLabel(string revision, bool chart)
    {
        if (chart)
        {
            return $"chart {revision}";
        }

        return revision.Length >= 7 && revision.All(Uri.IsHexDigit) ? revision[..7] : revision;
    }

    /// <summary>Whether the Application's source is a Helm chart (<c>spec.source.chart</c>) rather than a Git path.</summary>
    public static bool IsChartSource(ArgoApplication argo)
    {
        var spec = J.Obj(argo.Resource.Raw, "spec");
        var source = J.Obj(spec, "source");
        if (source.ValueKind != JsonValueKind.Object)
        {
            source = J.Arr(spec, "sources").FirstOrDefault();
        }

        return J.Str(source, "chart").Length > 0;
    }

    /// <summary>A ReplicaSet's <c>deployment.kubernetes.io/revision</c>, 0 when absent.</summary>
    public static long Revision(DynamicResource replicaSet) =>
        replicaSet.Annotations.TryGetValue("deployment.kubernetes.io/revision", out var value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : 0;

    // ---------------------------------------------------------------- events

    private static DynamicResource? EventFor(
        IReadOnlyList<DynamicResource> events, string kind, string name, string? @namespace, string reason) =>
        events
            .Where(e => e.Reason() == reason
                && e.InvolvedObject() is { } io && io.Kind == kind && io.Name == name
                && (e.InvolvedObjectNamespace() ?? e.Namespace) == @namespace)
            .OrderByDescending(e => e.LastSeen() ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static Evidence EventEvidence(DynamicResource e)
    {
        var io = e.InvolvedObject();
        return new Evidence(
            $"Event {e.Reason()} ×{e.Occurrences()}" + (io is null ? "" : $" on {io.Kind} {io.Name}"),
            e.Message());
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
