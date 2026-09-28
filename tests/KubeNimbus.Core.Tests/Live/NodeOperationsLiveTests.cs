using System.Net;
using System.Text.Json;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-29: FEAT-4's node operations against a real API server — as much of them as is safe
/// on a <b>shared, single-node</b> sandbox.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not done here, and why.</b> A drain that evicts is not run: on a one-node
/// cluster it would evict CoreDNS, Traefik, metrics-server and every other test run's pods,
/// with nowhere for them to go until it is undone. What is run instead is every part of a
/// drain that can be observed without that: the plan over the node's real pods, a cordon
/// that is undone within about a second, the drain's own refusal path (cordon → list → plan
/// → refuse, with nothing evicted), and eviction itself — PodDisruptionBudget 429 included —
/// against pods this test owns, one at a time.
/// </para>
/// <para>
/// The cordon tests are <c>[NotInParallel]</c> with each other and give up without touching
/// the node when it is already cordoned (somebody else is using it). The node is uncordoned
/// in a <c>finally</c>; only a killed process could leave it cordoned, and the window in
/// which that could happen is the second or so between the two patches.
/// </para>
/// </remarks>
public class NodeOperationsLiveTests
{
    private const string NodeLock = "sandbox-node";

    private static async Task<string> NodeNameAsync(ClusterClient client, CancellationToken ct)
    {
        var nodes = await client.ListResourceOnceAsync(LiveCluster.Nodes, cancellationToken: ct);
        return nodes.First().Name;
    }

    /// <summary>
    /// Four pods of this test's own, one of each shape a drain has to judge, planned from
    /// the node's real pod list. The plan must classify each as kubectl would — and must
    /// also leave the cluster's own DaemonSet pods (k3s's service load balancer) in place.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task The_drain_plan_over_the_real_node_classifies_every_shape_of_pod(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var shapes = await CreateDrainShapesAsync(client, ct);
        var node = await NodeNameAsync(client, ct);

        var pods = await client.ListPodsOnNodeAsync(ResourceDescriptor.Pods, node, ct);
        var plan = NodeActions.Plan(pods, new DrainOptions());

        DrainDisposition Of(string pod) => plan.Pods.Single(p => p.Key == $"{LiveCluster.Namespace}/{pod}").Disposition;
        await Assert.That(Of(shapes.Managed)).IsEqualTo(DrainDisposition.Evict);
        await Assert.That(Of(shapes.DaemonSet)).IsEqualTo(DrainDisposition.SkippedDaemonSet);
        await Assert.That(Of(shapes.Bare)).IsEqualTo(DrainDisposition.BlockedUnmanaged);
        await Assert.That(Of(shapes.EmptyDir)).IsEqualTo(DrainDisposition.BlockedLocalData);
        await Assert.That(plan.IsBlocked).IsTrue();

        // The cluster's own DaemonSet pods are left alone, whoever owns them.
        var systemDaemonSetPods = pods.Where(p => p.Namespace == "kube-system"
            && p.OwnerReferences.Any(o => o.Controller && o.Kind == "DaemonSet")).Select(p => p.Key).ToList();
        foreach (var key in systemDaemonSetPods)
        {
            await Assert.That(plan.Pods.Single(p => p.Key == key).Disposition).IsEqualTo(DrainDisposition.SkippedDaemonSet);
        }

        // The options kubectl calls --force and --delete-emptydir-data unblock exactly the two they name.
        var forced = NodeActions.Plan(pods, new DrainOptions(Force: true, DeleteEmptyDirData: true));
        await Assert.That(forced.Pods.Single(p => p.Name == shapes.Bare).Disposition).IsEqualTo(DrainDisposition.Evict);
        await Assert.That(forced.Pods.Single(p => p.Name == shapes.EmptyDir).Disposition).IsEqualTo(DrainDisposition.Evict);
    }

    /// <summary>
    /// A real cordon: the patch sets <c>spec.unschedulable</c>, and the server's own table —
    /// what <c>kubectl get node</c> prints — reads <c>Ready,SchedulingDisabled</c>. Uncordon
    /// sends an explicit <c>false</c>, the server stores the field as absent, and the status
    /// goes back to <c>Ready</c>.
    /// </summary>
    [Test]
    [NotInParallel(NodeLock)]
    [Timeout(60_000)]
    public async Task Cordon_shows_SchedulingDisabled_and_uncordon_clears_it(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var node = await NodeNameAsync(client, ct);
        if (NodeActions.IsCordoned((await client.ReadResourceAsync(LiveCluster.Nodes, null, node, ct))!))
        {
            Skip.Test($"{node} is already cordoned — someone else is using it; not touching it");
        }

        string cordonedStatus;
        bool cordonedFlag;
        try
        {
            await client.SetNodeSchedulableAsync(LiveCluster.Nodes, node, schedulable: false, ct);
            cordonedFlag = NodeActions.IsCordoned((await client.ReadResourceAsync(LiveCluster.Nodes, null, node, ct))!);
            cordonedStatus = await NodeStatusAsync(client, node, ct);
        }
        finally
        {
            await client.SetNodeSchedulableAsync(LiveCluster.Nodes, node, schedulable: true, CancellationToken.None);
        }

        await Assert.That(cordonedFlag).IsTrue();
        await Assert.That(cordonedStatus).IsEqualTo("Ready,SchedulingDisabled");

        var after = (await client.ReadResourceAsync(LiveCluster.Nodes, null, node, ct))!;
        await Assert.That(NodeActions.IsCordoned(after)).IsFalse();
        // The explicit `false` the patch sent is not stored: the field is omitempty, so an
        // uncordoned node has no spec.unschedulable at all.
        await Assert.That(after.Raw.GetProperty("spec").TryGetProperty("unschedulable", out _)).IsFalse();
        await Assert.That(await NodeStatusAsync(client, node, ct)).IsEqualTo("Ready");
    }

    /// <summary>
    /// The drain's own path up to the point where it would start evicting: it cordons, lists
    /// the node's pods, plans, and <b>refuses</b> because a pod this test owns has no
    /// controller — naming it, and evicting nothing at all. Should it ever evict anything
    /// the test stops the drain on the spot and fails.
    /// </summary>
    [Test]
    [NotInParallel(NodeLock)]
    [Timeout(180_000)]
    public async Task A_drain_refuses_before_evicting_anything_when_a_pod_needs_an_option(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var shapes = await CreateDrainShapesAsync(client, ct);
        var node = await NodeNameAsync(client, ct);
        if (NodeActions.IsCordoned((await client.ReadResourceAsync(LiveCluster.Nodes, null, node, ct))!))
        {
            Skip.Test($"{node} is already cordoned — someone else is using it; not touching it");
        }

        // Only drain when the refusal is certain: the unmanaged pod must be on this node now.
        var before = NodeActions.Plan(await client.ListPodsOnNodeAsync(ResourceDescriptor.Pods, node, ct), new DrainOptions());
        if (before.Pods.SingleOrDefault(p => p.Name == shapes.Bare)?.Disposition != DrainDisposition.BlockedUnmanaged)
        {
            throw new InvalidOperationException("the unmanaged pod is not on the node; not starting a drain");
        }

        var stages = new List<DrainProgress>();
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await foreach (var progress in client.DrainNodeAsync(
                               LiveCluster.Nodes, ResourceDescriptor.Pods, node, new DrainOptions(), drainCts.Token))
            {
                stages.Add(progress);
                if (progress.Stage is DrainStage.PodEvicted or DrainStage.PodBlocked or DrainStage.Waiting)
                {
                    await drainCts.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped by the safety net above; asserted below
        }
        finally
        {
            await client.SetNodeSchedulableAsync(LiveCluster.Nodes, node, schedulable: true, CancellationToken.None);
        }

        await Assert.That(stages.Select(s => s.Stage).ToArray())
            .IsEquivalentTo(new[] { DrainStage.Cordoned, DrainStage.Planned, DrainStage.Refused });
        var refused = stages[^1];
        await Assert.That(refused.Message).StartsWith("Nothing was evicted.");
        await Assert.That(refused.Plan!.Blocked.Select(p => p.Name)).Contains(shapes.Bare);
        await Assert.That(refused.Plan!.Blocked.Select(p => p.Name)).Contains(shapes.EmptyDir);

        // And the pods are all still there.
        foreach (var pod in new[] { shapes.Bare, shapes.Managed, shapes.EmptyDir })
        {
            var live = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, pod, ct);
            await Assert.That(live is not null && !LiveCluster.IsTerminating(live)).IsTrue();
        }
    }

    /// <summary>
    /// A real PodDisruptionBudget's 429, read as "blocked, retry" rather than a failure, in
    /// the server's own words. Once the budget allows it the eviction is accepted, the pod
    /// carries the <c>DisruptionTarget</c> condition while it goes (the one pod condition
    /// whose True is the bad news — VER-35 asked for a real one, and only an eviction makes
    /// it), and the ReplicaSet replaces it.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task A_pdb_blocks_an_eviction_with_429_and_an_allowed_one_marks_the_pod_as_a_disruption_target(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("guarded");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 2, LiveCluster.Loop("true", "5")), ct);
        await LiveCluster.ApplyAsync(client, LiveCluster.PodDisruptionBudgets, name, Pdb(name, minAvailable: 2), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 2, ct);
        await WaitForPdbAsync(client, name, expectedHealthy: 2, ct);

        var victim = (await LiveCluster.PodsOfAsync(client, name, ct)).First().Name;

        var blocked = await client.EvictPodAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim, cancellationToken: ct);
        await Assert.That(blocked.Outcome).IsEqualTo(EvictionOutcome.Blocked);
        await Assert.That(blocked.Message).Contains("disruption budget");

        // Loosen the budget, and watch the pod as it is evicted.
        await LiveCluster.ApplyAsync(client, LiveCluster.PodDisruptionBudgets, name, Pdb(name, minAvailable: 1), ct);
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pdb = await client.ReadResourceAsync(LiveCluster.PodDisruptionBudgets, LiveCluster.Namespace, name, ct);
            return pdb!.Raw.TryGetProperty("status", out var s) && s.TryGetProperty("disruptionsAllowed", out var d)
                && d.GetInt32() >= 1;
        }, TimeSpan.FromSeconds(60), "the loosened budget to allow a disruption", ct);

        var disruption = new TaskCompletionSource<PodCondition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   ResourceDescriptor.Pods, LiveCluster.Namespace, cancellationToken: watchCts.Token,
                                   labelSelector: new LabelSelector([new LabelRequirement("app", LabelOperator.In, [name])])))
                {
                    if (evt.Type == ResourceEventType.Synced)
                    {
                        synced.TrySetResult();
                    }

                    if (evt.Resource is { } pod && pod.Name == victim
                        && PodDetails.Conditions(pod).FirstOrDefault(c => c.Type == "DisruptionTarget") is { } condition)
                    {
                        disruption.TrySetResult(condition);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ended by the test
            }
        }, watchCts.Token);
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var accepted = await client.EvictPodAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim, cancellationToken: ct);
        await Assert.That(accepted.Outcome).IsEqualTo(EvictionOutcome.Accepted);

        var target = await disruption.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await watchCts.CancelAsync();
        await watch;

        await Assert.That(target.Status).IsEqualTo("True");
        await Assert.That(target.Reason).IsEqualTo("EvictionByEvictionAPI");
        await Assert.That(target.Polarity).IsEqualTo(PodConditionPolarity.Negative);
        await Assert.That(target.IsProblem).IsTrue();
        await Assert.That(target.LastTransition).IsNotNull();

        // Replaced, and the evicted one is gone.
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pods = await LiveCluster.PodsOfAsync(client, name, ct);
            return pods.Count == 2 && pods.All(p => p.Name != victim && LiveCluster.IsReady(p));
        }, TimeSpan.FromSeconds(90), "the evicted pod to be replaced", ct);

        // Evicting what is already gone is the outcome asked for, not a failure.
        var again = await client.EvictPodAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, victim, cancellationToken: ct);
        await Assert.That(again.Outcome).IsEqualTo(EvictionOutcome.AlreadyGone);
    }

    /// <summary>A user without <c>create</c> on <c>pods/eviction</c> gets the server's own sentence, as a permanent failure.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_narrow_users_eviction_fails_with_the_servers_own_403(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("evictee");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, LiveCluster.Loop("true", "5")), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        var pod = (await LiveCluster.PodsOfAsync(client, name, ct)).Single().Name;

        using var narrow = await LiveCluster.CreateNarrowUserAsync(client, LiveCluster.Named("no-evict"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [get, list, watch]
            """, ct);

        EvictionResult result = new(EvictionOutcome.Accepted, "");
        await LiveCluster.WaitUntilAsync(async () =>
        {
            result = await narrow.Client.EvictPodAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, pod, cancellationToken: ct);
            return result.Outcome == EvictionOutcome.Failed;
        }, TimeSpan.FromSeconds(30), "the narrow user's eviction to be refused", ct);

        await Assert.That(result.Message).Contains(narrow.UserName);
        await Assert.That(result.Message).Contains("cannot create resource \"pods/eviction\"");
        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, pod, ct)).IsNotNull();
    }

    private sealed record DrainShapes(string Managed, string DaemonSet, string Bare, string EmptyDir);

    private static readonly SemaphoreSlim ShapesGate = new(1, 1);
    private static DrainShapes? _shapes;

    /// <summary>One pod of each shape, created once per run and shared by the two tests that plan over them.</summary>
    private static async Task<DrainShapes> CreateDrainShapesAsync(ClusterClient client, CancellationToken ct)
    {
        await ShapesGate.WaitAsync(ct);
        try
        {
            if (_shapes is { } existing)
            {
                return existing;
            }

            var managed = LiveCluster.Named("drain-managed");
            await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, managed,
                LiveCluster.DeploymentYaml(managed, 1, LiveCluster.Loop("true", "5")), ct);

            var local = LiveCluster.Named("drain-emptydir");
            await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, local,
                LiveCluster.DeploymentYaml(local, 1, LiveCluster.Loop("true", "5"))
                    .Replace("      readinessProbe:", "      volumeMounts:\n            - name: scratch\n              mountPath: /scratch\n          readinessProbe:")
                    + "      volumes:\n        - name: scratch\n          emptyDir: {}\n", ct);

            var daemon = LiveCluster.Named("drain-daemon");
            await LiveCluster.ApplyAsync(client, LiveCluster.DaemonSets, daemon, $$"""
                apiVersion: apps/v1
                kind: DaemonSet
                metadata:
                  name: {{daemon}}
                  namespace: {{LiveCluster.Namespace}}
                spec:
                  selector:
                    matchLabels:
                      app: {{daemon}}
                  template:
                    metadata:
                      labels:
                        app: {{daemon}}
                    spec:
                      terminationGracePeriodSeconds: 1
                      containers:
                        - name: app
                          image: {{LiveCluster.Image}}
                          imagePullPolicy: IfNotPresent
                          command: ["sh", "-c", "trap 'exit 0' TERM; sleep 3600 & wait $!"]
                """, ct);

            var bare = LiveCluster.Named("drain-bare");
            await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, bare, $$"""
                apiVersion: v1
                kind: Pod
                metadata:
                  name: {{bare}}
                  namespace: {{LiveCluster.Namespace}}
                spec:
                  terminationGracePeriodSeconds: 1
                  containers:
                    - name: app
                      image: {{LiveCluster.Image}}
                      imagePullPolicy: IfNotPresent
                      command: ["sh", "-c", "trap 'exit 0' TERM; sleep 3600 & wait $!"]
                """, ct);

            await LiveCluster.WaitForReadyPodsAsync(client, managed, 1, ct);
            await LiveCluster.WaitForReadyPodsAsync(client, local, 1, ct);
            await LiveCluster.WaitForReadyPodsAsync(client, daemon, 1, ct);
            await LiveCluster.WaitUntilAsync(async () =>
                    await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, bare, ct) is { } p
                    && LiveCluster.IsReady(p),
                TimeSpan.FromSeconds(90), "the bare pod", ct);

            return _shapes = new DrainShapes(
                (await LiveCluster.PodsOfAsync(client, managed, ct)).Single().Name,
                (await LiveCluster.PodsOfAsync(client, daemon, ct)).Single().Name,
                bare,
                (await LiveCluster.PodsOfAsync(client, local, ct)).Single().Name);
        }
        finally
        {
            ShapesGate.Release();
        }
    }

    private static string Pdb(string name, int minAvailable) => $$"""
        apiVersion: policy/v1
        kind: PodDisruptionBudget
        metadata:
          name: {{name}}
          namespace: {{LiveCluster.Namespace}}
        spec:
          minAvailable: {{minAvailable}}
          selector:
            matchLabels:
              app: {{name}}
        """;

    private static Task WaitForPdbAsync(ClusterClient client, string name, int expectedHealthy, CancellationToken ct) =>
        LiveCluster.WaitUntilAsync(async () =>
        {
            var pdb = await client.ReadResourceAsync(LiveCluster.PodDisruptionBudgets, LiveCluster.Namespace, name, ct);
            return pdb!.Raw.TryGetProperty("status", out var s) && s.TryGetProperty("currentHealthy", out var h)
                && h.GetInt32() == expectedHealthy;
        }, TimeSpan.FromSeconds(60), "the budget to count its pods", ct);

    /// <summary>The node's STATUS cell as the server renders it for <c>kubectl get node</c>.</summary>
    private static async Task<string> NodeStatusAsync(ClusterClient client, string node, CancellationToken ct)
    {
        using var table = await LiveCluster.GetTableAsync(client, LiveCluster.Nodes.BasePath, ct);
        return ServerTable.Text(table.Row($"/{node}")[table.IndexOf("Status")]);
    }
}
