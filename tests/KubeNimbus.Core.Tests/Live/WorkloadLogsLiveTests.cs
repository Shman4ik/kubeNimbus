using System.Collections.Concurrent;
using System.Globalization;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-26: the Core half of FEAT-3's multi-pod log pane across a real
/// <c>kubectl rollout restart</c>. The pane is a <c>labelSelector</c> list+watch over the
/// workload's own <c>spec.selector</c> plus one follow per pod; what a real server can
/// show, and a stand-in cannot, is that the list half and the watch half agree on the
/// population through a rollout (the selector escaped the same way on both), that a
/// draining pod's stream ends by itself with its lines intact, and that the draining and
/// starting replicas overlap in time — the case the feature exists for.
/// </summary>
public class WorkloadLogsLiveTests
{
    [Test]
    [Timeout(240_000)]
    public async Task A_rollout_restart_reads_as_one_stream_across_old_and_new_pods(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("logger");

        // A selector with a match expression, so the query the pane sends is more than one
        // equality — and a decoy pod that matches the label but not the expression, which
        // must never appear in either half.
        var deployment = await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(
                name, 2,
                LiveCluster.Loop("echo \"pod=$HOSTNAME seq=$i\"; i=$((i+1))", "0.2"),
                selector: $"matchLabels:\n  app: {name}\nmatchExpressions:\n  - key: tier\n    operator: In\n    values: [web, api]",
                podLabels: "tier: web",
                strategy: "type: RollingUpdate\nrollingUpdate:\n  maxSurge: 1\n  maxUnavailable: 0"), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 2, ct);
        var decoy = LiveCluster.Named("logger-decoy");
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, decoy, $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{decoy}}
              namespace: {{LiveCluster.Namespace}}
              labels:
                app: {{name}}
                tier: batch
            spec:
              terminationGracePeriodSeconds: 1
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "trap 'exit 0' TERM; while true; do echo decoy; sleep 1 & wait $!; done"]
            """, ct);
        var selector = LabelSelector.ForPodsOf(deployment)!;
        await Assert.That(selector.ToQuery()).Contains("tier in (");

        var lines = new ConcurrentDictionary<string, ConcurrentQueue<(DateTimeOffset At, string Text)>>();
        var streams = new ConcurrentDictionary<string, Task<string>>();
        var added = new ConcurrentBag<string>();
        var refusals = new ConcurrentBag<string>();
        var earlyEnds = new ConcurrentBag<string>();
        var deleted = new ConcurrentBag<string>();
        var initial = new List<string>();
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var paneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // One follow per pod, started as the watch reports it — the pane's own shape,
        // including its retry: a pod reported while its container is still being created
        // refuses the log request in the server's words, and the pane starts the stream
        // again on the pod's next Modified event, once that attempt has failed.
        void Follow(string pod)
        {
            if (streams.TryGetValue(pod, out var existing)
                && !(existing.IsCompleted && existing.Result.StartsWith("threw", StringComparison.Ordinal)))
            {
                return;
            }

            if (existing is not null)
            {
                refusals.Add(existing.Result);
            }

            streams[pod] = StartFollow(pod);
        }

        // And the pane's other retry (LogStreamEnd.StartedAfterRequest): a follow that
        // reaches the container between "created" and "started" is answered with an empty
        // body that closes at once. When a follow ends having carried nothing, and the pod
        // it was opened on before its container ran is running now, it is followed again.
        Task<string> StartFollow(string p) => Task.Run(async () =>
        {
            var queue = lines.GetOrAdd(p, _ => new());
            while (true)
            {
                var atStart = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, p, paneCts.Token);
                var wasRunning = atStart is not null
                    && PodDetails.ContainerRunOf(atStart.Raw, "app") is { State: ContainerRunState.Running };
                var before = queue.Count;
                try
                {
                    await foreach (var line in client.StreamPodLogsAsync(
                                       LiveCluster.Namespace, p, follow: true, tailLines: 50, timestamps: true,
                                       cancellationToken: paneCts.Token))
                    {
                        var space = line.IndexOf(' ');
                        if (space > 0 && DateTimeOffset.TryParse(line[..space], CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var at))
                        {
                            queue.Enqueue((at, line[(space + 1)..]));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return "cancelled";
                }
                catch (Exception ex)
                {
                    return $"threw {ex.GetType().Name}: {ex.Message}";
                }

                if (queue.Count > before || wasRunning)
                {
                    return "ended";
                }

                await Task.Delay(1000, paneCts.Token);
                var now = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, p, paneCts.Token);
                if (now is null || PodDetails.ContainerRunOf(now.Raw, "app") is not { State: ContainerRunState.Running })
                {
                    return "ended";
                }

                earlyEnds.Add(p);
            }
        });

        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   ResourceDescriptor.Pods, LiveCluster.Namespace, cancellationToken: paneCts.Token,
                                   labelSelector: selector))
                {
                    switch (evt.Type)
                    {
                        case ResourceEventType.Synced:
                            synced.TrySetResult();
                            break;
                        case ResourceEventType.Added when evt.Resource is { } pod:
                            if (!synced.Task.IsCompleted)
                            {
                                initial.Add(pod.Name);
                            }

                            added.Add(pod.Name);
                            Follow(pod.Name);
                            break;
                        case ResourceEventType.Modified when evt.Resource is { } pod:
                            Follow(pod.Name);
                            break;
                        case ResourceEventType.Deleted when evt.Resource is { } pod:
                            deleted.Add(pod.Name);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ended by the test
            }
        }, paneCts.Token);

        await synced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await Assert.That(initial.Count).IsEqualTo(2);
        await Assert.That(initial).DoesNotContain(decoy);

        // Let the original pods log for a moment, then restart as kubectl would.
        await Task.Delay(1500, ct);
        var at = DateTimeOffset.UtcNow;
        await client.RestartWorkloadAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, at, ct);

        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pods = (await LiveCluster.PodsOfAsync(client, name, ct)).Where(p => p.Name != decoy).ToList();
            return pods.Count == 2 && pods.All(p => !initial.Contains(p.Name) && LiveCluster.IsReady(p));
        }, TimeSpan.FromSeconds(150), "the restart to replace both pods", ct);

        // Old pods' streams end on their own when their containers stop — they are not
        // cancelled by the test and they do not throw.
        await LiveCluster.WaitUntilAsync(
            () => Task.FromResult(initial.All(p => streams[p].IsCompleted) && initial.All(p => deleted.Contains(p))),
            TimeSpan.FromSeconds(60), "the old pods' log streams to end and their pods to be deleted", ct);
        await Task.Delay(1000, ct);
        await paneCts.CancelAsync();
        await watch;

        foreach (var old in initial)
        {
            await Assert.That(await streams[old]).IsEqualTo("ended");
            await Assert.That(lines[old].Count).IsGreaterThan(5);
        }

        var replacements = added.Distinct().Except(initial).ToList();
        await Assert.That(replacements).DoesNotContain(decoy);
        await Assert.That(replacements.Count).IsGreaterThanOrEqualTo(2);
        var report = string.Join("; ", replacements.Select(p =>
            $"{p}: {(streams.TryGetValue(p, out var s) ? (s.IsCompleted ? s.Result : "running") : "never started")}, "
            + $"{(lines.TryGetValue(p, out var q) ? q.Count : 0)} lines, refusals [{string.Join(" | ", refusals)}], "
            + $"followed again after an empty early end: [{string.Join(", ", earlyEnds)}]"));
        Console.WriteLine(report);
        foreach (var fresh in replacements)
        {
            await Assert.That(lines.TryGetValue(fresh, out var q) && !q.IsEmpty).IsTrue().Because(report);
        }

        // A stream refused because its pod was still starting is refused in the server's
        // own sentence, the one the pane prints until the retry succeeds.
        foreach (var refusal in refusals)
        {
            await Assert.That(refusal.Contains("is waiting to start", StringComparison.Ordinal)
                || refusal.Contains("is not available", StringComparison.Ordinal)).IsTrue().Because(refusal);
        }

        // One stream: sorted by the server's own timestamps, the old pods were still
        // logging after the first new pod had started (maxUnavailable: 0 keeps them
        // running until a replacement is Ready), so the merged output interleaves.
        var firstNew = replacements.Min(p => lines[p].Min(l => l.At));
        var lastOld = initial.Max(p => lines[p].Max(l => l.At));
        await Assert.That(lastOld).IsGreaterThan(firstNew);

        // Every line carries its own pod's name — nothing was misattributed across streams.
        foreach (var (pod, queue) in lines)
        {
            await Assert.That(queue.All(l => l.Text.StartsWith($"pod={pod} ", StringComparison.Ordinal))).IsTrue();
        }
    }
}
