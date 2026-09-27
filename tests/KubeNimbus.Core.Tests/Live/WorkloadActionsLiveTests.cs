using System.Net;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-13: FEAT-1's three mutating actions against a real API server. Every patch shape
/// here was argued from the wire format and pinned by <c>WorkloadActionsTests</c> without
/// ever reaching a server — and each of them fails <em>silently</em> when wrong (a restart
/// patch with the wrong key is a 200 that rolls nothing), which is exactly why "the server
/// accepted it" is not what these assert. They assert what the cluster did next.
/// </summary>
public class WorkloadActionsLiveTests
{
    /// <summary>
    /// A scale through the <c>scale</c> subresource is reported back by the server, and
    /// the Deployment list the app watches follows it — the replica counts change on a
    /// watch event, not only on a re-read.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task Scale_is_reported_back_and_the_watched_list_follows(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("scale");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, LiveCluster.Loop("true", "5")), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);

        // The list the app renders is a watch over the kind; watch the namespace's
        // Deployments and wait for this row to report three ready replicas.
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchCts.CancelAfter(TimeSpan.FromSeconds(120));
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   LiveCluster.Deployments, LiveCluster.Namespace, cancellationToken: watchCts.Token))
                {
                    if (evt.Type == ResourceEventType.Synced)
                    {
                        synced.TrySetResult();
                    }

                    if (evt is { Type: ResourceEventType.Modified, Resource: { } d } && d.Name == name
                        && ReadyReplicas(d) == 3)
                    {
                        followed.TrySetResult(3);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // the watch is ended by the test
            }
        }, watchCts.Token);

        await synced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var before = await client.GetScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        var reported = await client.ScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, 3, ct);

        await Assert.That(before.Replicas).IsEqualTo(1);
        await Assert.That(reported.Replicas).IsEqualTo(3);

        var seen = await followed.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);
        await Assert.That(seen).IsEqualTo(3);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 3, ct);

        // And back down, which is the direction that deletes pods.
        var down = await client.ScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, 1, ct);
        await Assert.That(down.Replicas).IsEqualTo(1);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);

        await watchCts.CancelAsync();
        await watch;
    }

    /// <summary>
    /// A rollout restart <b>rolls</b>: the <c>restartedAt</c> annotation lands on the pod
    /// template (and so on every new pod), a new ReplicaSet brings pods up, and the old
    /// pods are not all deleted at once. With <c>maxUnavailable: 0</c> the controller may
    /// not take an old pod down until a new one is Ready, so the moment the first new pod
    /// appears every old pod must still be running — the observable difference between a
    /// rollout and the delete-every-pod loop this action deliberately is not.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task Rollout_restart_stamps_restartedAt_and_rolls_the_pods_one_at_a_time(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("restart");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 3, LiveCluster.Loop("true", "5"),
                strategy: "type: RollingUpdate\nrollingUpdate:\n  maxSurge: 1\n  maxUnavailable: 0"), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 3, ct);

        var original = (await LiveCluster.PodsOfAsync(client, name, ct)).Select(p => p.Name).ToHashSet();

        // Watch the Deployment's own pods, the population the app's pod list shows.
        var selector = new LabelSelector([new LabelRequirement("app", LabelOperator.In, [name])]);
        var liveOldAtFirstNewPod = -1;
        var newPods = new HashSet<string>();
        var deletedOld = new HashSet<string>();
        var terminatingOld = new HashSet<string>();
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   ResourceDescriptor.Pods, LiveCluster.Namespace, cancellationToken: watchCts.Token,
                                   labelSelector: selector))
                {
                    if (evt.Type == ResourceEventType.Synced)
                    {
                        synced.TrySetResult();
                        continue;
                    }

                    if (evt.Resource is not { } pod)
                    {
                        continue;
                    }

                    lock (newPods)
                    {
                        if (original.Contains(pod.Name))
                        {
                            if (evt.Type == ResourceEventType.Deleted)
                            {
                                deletedOld.Add(pod.Name);
                            }
                            else if (LiveCluster.IsTerminating(pod))
                            {
                                terminatingOld.Add(pod.Name);
                            }
                        }
                        else if (evt.Type == ResourceEventType.Added && newPods.Add(pod.Name) && newPods.Count == 1)
                        {
                            liveOldAtFirstNewPod = original.Count - deletedOld.Count
                                - terminatingOld.Count(n => !deletedOld.Contains(n));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // the watch is ended by the test
            }
        }, watchCts.Token);
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var at = DateTimeOffset.UtcNow;
        await client.RestartWorkloadAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, at, ct);

        // The annotation is on the object, in kubectl's own key and format.
        var deployment = await client.ReadResourceAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        var stamped = deployment!.Raw.GetProperty("spec").GetProperty("template").GetProperty("metadata")
            .GetProperty("annotations").GetProperty(WorkloadActions.RestartedAtAnnotation).GetString();
        await Assert.That(stamped).IsEqualTo(WorkloadActions.FormatRestartedAt(at));

        // The rollout finishes: three ready pods, none of them an original.
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pods = await LiveCluster.PodsOfAsync(client, name, ct);
            return pods.Count == 3 && pods.All(p => !original.Contains(p.Name) && LiveCluster.IsReady(p));
        }, TimeSpan.FromSeconds(150), "the restart to replace every pod", ct);

        await watchCts.CancelAsync();
        await watch;

        // It rolled rather than deleting everything: all three originals were still live
        // when the first replacement was created.
        await Assert.That(liveOldAtFirstNewPod).IsEqualTo(3);
        await Assert.That(newPods.Count).IsGreaterThanOrEqualTo(3);

        // Every new pod carries the stamp — the template change is what made them.
        foreach (var pod in await LiveCluster.PodsOfAsync(client, name, ct))
        {
            await Assert.That(pod.Annotations.TryGetValue(WorkloadActions.RestartedAtAnnotation, out var value)
                ? value : null).IsEqualTo(WorkloadActions.FormatRestartedAt(at));
        }
    }

    /// <summary>A pod deleted through the app is recreated by its ReplicaSet under a new name.</summary>
    [Test]
    [Timeout(180_000)]
    public async Task Deleting_a_managed_pod_recreates_it(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("delete");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, LiveCluster.Loop("true", "5")), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        var victim = (await LiveCluster.PodsOfAsync(client, name, ct)).Single();

        await client.DeleteResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim.Name, ct);

        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pods = await LiveCluster.PodsOfAsync(client, name, ct);
            return pods.Any(p => p.Name != victim.Name && LiveCluster.IsReady(p));
        }, TimeSpan.FromSeconds(90), "a replacement pod", ct);

        await LiveCluster.WaitUntilAsync(
            async () => await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim.Name, ct) is null,
            TimeSpan.FromSeconds(60), "the deleted pod to be gone", ct);

        // Deleting what is already gone is success, not an error — the app's delete of a
        // row that vanished under it must not report a failure.
        await client.DeleteResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim.Name, ct);
    }

    /// <summary>
    /// A user whose RBAC does not cover the action gets the API server's own sentence —
    /// naming the user, the verb, the resource and the namespace — for all three actions,
    /// and nothing changes. The read the same user is allowed still works, which is what
    /// makes the 403 a statement about the verb rather than about the connection.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task A_narrow_user_gets_the_servers_own_403_for_scale_restart_and_delete(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("rbac");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, LiveCluster.Loop("true", "5")), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        var pod = (await LiveCluster.PodsOfAsync(client, name, ct)).Single();

        using var narrow = await LiveCluster.CreateNarrowUserAsync(client, LiveCluster.Named("viewer"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [get, list, watch]
            - apiGroups: [apps]
              resources: [deployments]
              verbs: [get, list, watch]
            """, ct);

        // The narrow user can read (RBAC propagation is not instant on every server, so
        // wait for the grant rather than asserting on the first request).
        await LiveCluster.WaitUntilAsync(async () =>
        {
            try
            {
                return await narrow.Client.ReadResourceAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct) is not null;
            }
            catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(30), "the narrow user's read grant", ct);

        var scale = await CaptureAsync(() => narrow.Client.ScaleAsync(
            LiveCluster.Deployments, LiveCluster.Namespace, name, 2, ct));
        var restart = await CaptureAsync(() => narrow.Client.RestartWorkloadAsync(
            LiveCluster.Deployments, LiveCluster.Namespace, name, cancellationToken: ct));
        var delete = await CaptureAsync(() => narrow.Client.DeleteResourceAsync(
            ResourceDescriptor.Pods, LiveCluster.Namespace, pod.Name, ct));

        foreach (var (failure, verb, resource) in new[]
                 {
                     (scale, "patch", "deployments/scale"),
                     (restart, "patch", "deployments"),
                     (delete, "delete", "pods"),
                 })
        {
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

            // The server's own sentence, not "403 (Forbidden)": it names who, what and where.
            await Assert.That(failure.ServerMessage).IsNotNull();
            await Assert.That(failure.ServerMessage!).Contains(narrow.UserName);
            await Assert.That(failure.ServerMessage!).Contains($"cannot {verb} resource \"{resource}\"");
            await Assert.That(failure.ServerMessage!).Contains($"in the namespace \"{LiveCluster.Namespace}\"");
            await Assert.That(failure.Message).StartsWith(failure.ServerMessage!);
        }

        // Nothing moved.
        var after = await client.GetScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        await Assert.That(after.Replicas).IsEqualTo(1);
        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, pod.Name, ct)).IsNotNull();
    }

    private static async Task<KubernetesApiException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (KubernetesApiException ex)
        {
            return ex;
        }
    }

    private static int ReadyReplicas(DynamicResource deployment) =>
        deployment.Raw.TryGetProperty("status", out var status)
        && status.TryGetProperty("readyReplicas", out var ready) && ready.TryGetInt32(out var n)
            ? n
            : 0;
}
