namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// #184 (the 0.7.0 QA run's G-13): a log pane opened on a pod held by a scheduling gate.
/// What a real API server does at each step, and that <see cref="LogFollowRetry"/> — the
/// rule both log panes run — reads every step the way the panes need: the gated pod's
/// follow ends at once with nothing (an empty answer, not an error); once the gate is
/// released, every refusal while the container comes up is a not-started refusal (the 400
/// the pane used to keep for good); and once the container runs, following again reads its
/// lines. The panes' own reaction to each verdict is pinned by <c>LogFollowRetryTests</c>
/// in the App tests.
/// </summary>
public class GatedPodLogsLiveTests
{
    [Test]
    [Timeout(150_000)]
    public async Task A_gated_pod_is_refused_as_not_started_until_its_container_runs_then_read(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("gated");
        string Manifest(bool gated) => $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              terminationGracePeriodSeconds: 1
            {{(gated ? "  schedulingGates:\n    - name: kubenimbus.io/qa-hold" : "")}}
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "trap 'exit 0' TERM; i=0; while true; do echo \"gated pod line $i\"; i=$((i+1)); sleep 1 & wait $!; done"]
            """;

        var gated = await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, Manifest(gated: true), ct);
        var atStart = PodDetails.ContainerRunOf(gated.Raw, "app");
        await Assert.That(LogFollowRetry.HasStarted(atStart)).IsFalse();

        // Gated: the follow is answered and ends with no line, rather than refused.
        var early = new List<string>();
        await foreach (var line in client.StreamPodLogsAsync(LiveCluster.Namespace, name, "app", follow: true, cancellationToken: ct))
        {
            early.Add(line);
        }

        await Assert.That(early).IsEmpty();

        // Release the gate (the field manager that set it applies the pod without it), then
        // ask as a pane would while the pod comes up, until the container has run.
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, Manifest(gated: false), ct);
        var refusals = new List<string>();
        ContainerRun? now = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await foreach (var _ in client.StreamPodLogsAsync(
                                   LiveCluster.Namespace, name, "app", follow: false, cancellationToken: ct))
                {
                }
            }
            catch (HttpRequestException ex)
            {
                // Every refusal on the way is the server saying "not started yet", which the
                // panes wait out instead of showing as the end (#184, #309).
                await Assert.That(LogFollowRetry.IsNotStartedRefusal(ex, atStart)).IsTrue().Because(ex.Message);
                refusals.Add(ex.Message);
            }

            var pod = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
            now = pod is null ? null : PodDetails.ContainerRunOf(pod.Raw, "app");
            if (now is { State: ContainerRunState.Running })
            {
                break;
            }

            await Task.Delay(200, ct);
        }

        Console.WriteLine($"refusals while the pod came up: [{string.Join(" | ", refusals)}]");
        await Assert.That(LogFollowRetry.ShouldFollowAgain(atStart, now)).IsTrue();
        foreach (var refusal in refusals)
        {
            await Assert.That(refusal.Contains("is not available", StringComparison.Ordinal)
                || refusal.Contains("is waiting to start", StringComparison.Ordinal)).IsTrue().Because(refusal);
        }

        // Following again reads the container's lines.
        using var followCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        followCts.CancelAfter(TimeSpan.FromSeconds(20));
        string? first = null;
        try
        {
            await foreach (var line in client.StreamPodLogsAsync(
                               LiveCluster.Namespace, name, "app", follow: true, cancellationToken: followCts.Token))
            {
                first = line;
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        await Assert.That(first).IsNotNull();
        await Assert.That(first!).Contains("gated pod line");

        await client.DeleteResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
    }
}
