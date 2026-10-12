using System.Net;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// A follow the API server refused because its container had not started is followed
/// again once the container has, in both log panes (#309, #184). The multi-pod pane used
/// to wait only for the pod's next Modified event, which under load came before the
/// refusal did, so a running pod read "waiting to start" for good; pod detail did not wait
/// at all, so a pod whose scheduling gate was released sat on "is not available (400 Bad
/// Request)" while it ran (the 0.7.0 QA run's G-13). These drive the panes' own retry
/// methods on demo-mode panes (no client, nothing reaches a network); the live half is
/// <c>Live/WorkloadLogsLiveTests</c> in the Core tests.
/// </summary>
public class LogFollowRetryTests
{
    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static readonly ContainerRun Creating = new(ContainerRunState.Waiting, "ContainerCreating", null, null, 0);
    private static readonly ContainerRun Running = new(ContainerRunState.Running, null, null, "containerd://a", 0);

    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>A pod of the pane's selector: unscheduled, scheduled with no container status yet, creating, or running.</summary>
    private static DynamicResource Pod(string phase)
    {
        var node = phase == "unscheduled" ? "" : "\"nodeName\": \"worker-1\",";
        var statuses = phase switch
        {
            "creating" => """, "containerStatuses": [ { "name": "app", "ready": false, "restartCount": 0, "state": { "waiting": { "reason": "ContainerCreating" } } } ]""",
            "running" => """, "containerStatuses": [ { "name": "app", "ready": true, "restartCount": 0, "containerID": "containerd://a", "state": { "running": {} } } ]""",
            _ => "",
        };
        return Object($$"""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "api-1", "namespace": "nowhere", "labels": { "app": "api-retry" } },
              "spec": { {{node}} "containers": [ { "name": "app" } ] },
              "status": { "phase": "{{(phase == "running" ? "Running" : "Pending")}}"{{statuses}} }
            }
            """);
    }

    private static WorkloadLogsTabViewModel Pane()
    {
        TestObjects.RedirectStores();
        var workload = Object("""
            {
              "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "api", "namespace": "nowhere" },
              "spec": { "selector": { "matchLabels": { "app": "api-nothing-matches-this" } } }
            }
            """);
        return new WorkloadLogsTabViewModel(null, DeploymentDescriptor, workload, LabelSelector.ForPodsOf(workload)!);
    }

    private static HttpRequestException Refusal(HttpStatusCode code) => new("refused", null, code);

    // ------------------------------------------------------------ the rule (Core)

    [Test]
    public async Task Only_a_400_for_a_container_that_had_not_started_is_a_wait()
    {
        await Assert.That(LogFollowRetry.IsNotStartedRefusal(Refusal(HttpStatusCode.BadRequest), null)).IsTrue();
        await Assert.That(LogFollowRetry.IsNotStartedRefusal(Refusal(HttpStatusCode.BadRequest), Creating)).IsTrue();

        // A 400 about a container that had run is something else ("previous terminated
        // container not found"), and a 403 or a 500 is never a wait.
        await Assert.That(LogFollowRetry.IsNotStartedRefusal(Refusal(HttpStatusCode.BadRequest), Running)).IsFalse();
        await Assert.That(LogFollowRetry.IsNotStartedRefusal(Refusal(HttpStatusCode.Forbidden), null)).IsFalse();
        await Assert.That(LogFollowRetry.IsNotStartedRefusal(new InvalidOperationException("x"), null)).IsFalse();
    }

    [Test]
    public async Task A_container_has_started_once_it_has_a_run_whose_log_can_be_read()
    {
        await Assert.That(LogFollowRetry.HasStarted(null)).IsFalse();
        await Assert.That(LogFollowRetry.HasStarted(Creating)).IsFalse();
        await Assert.That(LogFollowRetry.HasStarted(Running)).IsTrue();
        await Assert.That(LogFollowRetry.HasStarted(new ContainerRun(ContainerRunState.Terminated, "Error", 1, "containerd://a", 0))).IsTrue();
        await Assert.That(LogFollowRetry.HasStarted(new ContainerRun(ContainerRunState.Waiting, "CrashLoopBackOff", null, "containerd://a", 3))).IsTrue();

        // It cannot loop: a follow asked of a started container is never retried this way.
        await Assert.That(LogFollowRetry.ShouldFollowAgain(Running, Running)).IsFalse();
        await Assert.That(LogFollowRetry.ShouldFollowAgain(Creating, Creating)).IsFalse();
        await Assert.That(LogFollowRetry.ShouldFollowAgain(Creating, Running)).IsTrue();
    }

    // ------------------------------------------------------ the multi-pod pane (#309)

    /// <summary>
    /// The order #309 is about: the pod's last Modified event (Running and Ready) arrives
    /// while the request that will be refused is still in flight, so that event re-opens
    /// nothing; the refusal comes after it. The pod read the refusal triggers is what
    /// follows the pod — and without it the source stayed "not started".
    /// </summary>
    [Test]
    public async Task A_refusal_delivered_after_the_last_Modified_event_is_followed_once_the_pod_is_read()
    {
        var pane = Pane();
        var source = pane.RegisterSource("api-1", "app");
        source.State = LogSourceState.Starting;

        // The last Modified event: the stream is still in flight, so nothing is re-opened.
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("running")));
        await Assert.That(source.State).IsEqualTo(LogSourceState.Starting);

        // The refusal arrives: the chip reads not started, with the server's sentence.
        source.State = LogSourceState.NotStarted;
        source.StatusMessage = "container \"app\" in pod \"api-1\" is waiting to start: ContainerCreating (400 Bad Request)";

        // The pod read after it finds the container running: followed again.
        await Assert.That(pane.FollowIfStarted(source, Creating, Running, CancellationToken.None)).IsTrue();
        await Assert.That(source.State).IsEqualTo(LogSourceState.Starting);
    }

    [Test]
    public async Task A_refusal_whose_pod_has_still_not_started_waits_for_the_watch()
    {
        var pane = Pane();
        var source = pane.RegisterSource("api-1", "app");
        source.State = LogSourceState.NotStarted;

        await Assert.That(pane.FollowIfStarted(source, Creating, Creating, CancellationToken.None)).IsFalse();
        await Assert.That(pane.FollowIfStarted(source, null, null, CancellationToken.None)).IsFalse();
        await Assert.That(source.State).IsEqualTo(LogSourceState.NotStarted);

        // Scheduled, but the kubelet has not reported the container: asking now would only
        // be refused again ("is not available"), so the watch waits for a run.
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("scheduled")));
        await Assert.That(source.State).IsEqualTo(LogSourceState.NotStarted);
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("creating")));
        await Assert.That(source.State).IsEqualTo(LogSourceState.NotStarted);

        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("running")));
        await Assert.That(source.State).IsEqualTo(LogSourceState.Starting);
    }

    /// <summary>A Modified event that re-opened the source first wins; the late pod read does not open a second stream.</summary>
    [Test]
    public async Task A_pod_read_that_lands_after_the_watch_reopened_the_source_does_nothing()
    {
        var pane = Pane();
        var source = pane.RegisterSource("api-1", "app");
        source.State = LogSourceState.NotStarted;
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("running")));
        await Assert.That(source.State).IsEqualTo(LogSourceState.Starting);

        await Assert.That(pane.FollowIfStarted(source, Creating, Running, CancellationToken.None)).IsFalse();
    }

    // -------------------------------------------------------------- pod detail (#184)

    private static PodDetailTabViewModel Detail()
    {
        TestObjects.RedirectStores();
        var pod = Object("""
            {
              "apiVersion": "v1", "kind": "Pod",
              "metadata": { "name": "qa-gated", "namespace": "payments" },
              "spec": { "nodeName": "worker-1", "containers": [ { "name": "model-server" } ] },
              "status": { "phase": "Pending" }
            }
            """);
        var detail = new PodDetailTabViewModel(null, new ResourceRowViewModel(pod), _ => { }, (_, _) => Task.CompletedTask);

        // As the refused follow leaves it: not streaming, Follow off.
        detail.IsFollowingLogs = false;
        return detail;
    }

    /// <summary>
    /// G-13: the gate released, the follow refused with "is not available" while the
    /// kubelet had not reported the container, then the pod read finds it running. The pane
    /// follows it — it used to keep the 400 for good.
    /// </summary>
    [Test]
    public async Task Pod_detail_follows_a_refused_container_once_the_pod_read_finds_it_running()
    {
        var detail = Detail();
        var generation = detail.LogGeneration;
        detail.ArmFollowWhenStarted(generation, "model-server");

        await Assert.That(detail.FollowIfStarted(generation, "model-server", null, Running)).IsTrue();
        await Assert.That(detail.IsFollowingLogs).IsTrue();
    }

    [Test]
    public async Task Pod_detail_keeps_waiting_while_the_container_has_not_started()
    {
        var detail = Detail();
        var generation = detail.LogGeneration;
        detail.ArmFollowWhenStarted(generation, "model-server");

        await Assert.That(detail.FollowIfStarted(generation, "model-server", null, Creating)).IsFalse();
        await Assert.That(detail.IsFollowingLogs).IsFalse();

        // Not armed (the reader chose something else since): never followed behind their back.
        var other = Detail();
        await Assert.That(other.FollowIfStarted(other.LogGeneration, "model-server", null, Running)).IsFalse();
    }
}
