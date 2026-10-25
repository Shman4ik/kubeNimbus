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
    private static DynamicResource Pod(string phase, string name = "api-1")
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
              "metadata": { "name": "{{name}}", "namespace": "nowhere", "labels": { "app": "api-retry" } },
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

    /// <summary>
    /// #331: the reader's Follow choice is theirs. With Follow off, a pod that comes up after
    /// a refusal — through the pod read or the watch — is loaded as a range, not followed.
    /// </summary>
    [Test]
    public async Task With_Follow_off_a_pod_that_comes_up_is_loaded_as_a_range_not_followed()
    {
        var pane = Pane();
        pane.IsFollowing = false;

        var read = pane.RegisterSource("api-1", "app");
        read.State = LogSourceState.NotStarted;
        await Assert.That(pane.FollowIfStarted(read, Creating, Running, CancellationToken.None)).IsTrue();
        await Assert.That(read.Follows).IsFalse();

        var watched = pane.RegisterSource("api-2", "app");
        watched.State = LogSourceState.NotStarted;
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("running", "api-2")));
        await Assert.That(watched.State).IsEqualTo(LogSourceState.Starting);
        await Assert.That(watched.Follows).IsFalse();

        // With Follow on, the same retry follows.
        pane.IsFollowing = true;
        var followed = pane.RegisterSource("api-3", "app");
        followed.State = LogSourceState.NotStarted;
        await Assert.That(pane.FollowIfStarted(followed, Creating, Running, CancellationToken.None)).IsTrue();
        await Assert.That(followed.Follows).IsTrue();
    }

    // --------------------------------------------- a 404 for a pod going away (#332)

    private static DynamicResource Deleting() => Object("""
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "api-1", "namespace": "nowhere", "deletionTimestamp": "2026-10-12T10:00:00Z" },
          "spec": { "nodeName": "worker-1", "containers": [ { "name": "app" } ] },
          "status": { "phase": "Running" }
        }
        """);

    [Test]
    public async Task A_404_for_a_pod_that_is_gone_or_being_deleted_ends_the_source_as_deleted()
    {
        var notFound = Refusal(HttpStatusCode.NotFound);

        await Assert.That(WorkloadLogsTabViewModel.NotFoundVerdict(notFound, null))
            .IsEqualTo((LogSourceState.Gone, WorkloadLogsTabViewModel.PodDeletedMessage));
        await Assert.That(WorkloadLogsTabViewModel.NotFoundVerdict(notFound, Deleting()))
            .IsEqualTo((LogSourceState.Gone, WorkloadLogsTabViewModel.PodDeletedMessage));

        // A pod that is still there and not being deleted: the 404 is a failure, said.
        await Assert.That(WorkloadLogsTabViewModel.NotFoundVerdict(notFound, Pod("running")).State)
            .IsEqualTo(LogSourceState.Failed);

        // Only a 404 is a pod going away; a 400 or a 403 for a deleted pod is not.
        await Assert.That(LogFollowRetry.IsPodGone(Refusal(HttpStatusCode.BadRequest), null)).IsFalse();
        await Assert.That(LogFollowRetry.IsPodGone(Refusal(HttpStatusCode.Forbidden), null)).IsFalse();
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

    /// <summary>
    /// #331: a Follow-off range load refused because the container had not started is
    /// loaded as a range once it runs — the refused request asked again, not a live follow.
    /// </summary>
    [Test]
    public async Task Pod_detail_loads_a_refused_range_as_a_range_once_the_container_runs()
    {
        var detail = Detail();
        var generation = detail.LogGeneration;
        detail.ArmFollowWhenStarted(generation, "model-server", follow: false);

        await Assert.That(detail.FollowIfStarted(generation, "model-server", null, Running)).IsTrue();
        await Assert.That(detail.IsFollowingLogs).IsFalse();
    }

    /// <summary>A gated pod as the watch shows it: no node, no container status, PodScheduled False.</summary>
    private static DynamicResource Gated() => Object("""
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "qa-gated", "namespace": "payments" },
          "spec": { "containers": [ { "name": "model-server" } ] },
          "status": { "phase": "Pending",
            "conditions": [ { "type": "PodScheduled", "status": "False", "reason": "SchedulingGated" } ] }
        }
        """);

    private static DynamicResource GatedReleasedAndRunning() => Object("""
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "qa-gated", "namespace": "payments" },
          "spec": { "nodeName": "worker-1", "containers": [ { "name": "model-server" } ] },
          "status": { "phase": "Running",
            "containerStatuses": [ { "name": "model-server", "ready": true, "restartCount": 0,
              "containerID": "containerd://a", "state": { "running": {} } } ] }
        }
        """);

    /// <summary>
    /// The 1.0 QA run's G-17: a range load with Follow off on a pod that is not scheduled.
    /// The API server answers it as it answers a follow, an empty 204, so the read ends
    /// cleanly with no lines; the pane said "No lines in the last 5 minutes." and never read
    /// the pod again, so it kept saying so after the gate was released and the pod logged.
    /// It now says the pod has not started, as a wait, and loads the range once it runs —
    /// as a range, Follow staying off (#331).
    /// </summary>
    [Test]
    public async Task Pod_detail_waits_out_an_empty_range_of_a_pod_that_has_not_started_and_loads_it_once_it_runs()
    {
        TestObjects.RedirectStores();
        var row = new ResourceRowViewModel(Gated());
        var detail = new PodDetailTabViewModel(null, row, _ => { }, (_, _) => Task.CompletedTask);
        detail.IsFollowingLogs = false;
        var generation = detail.LogGeneration;

        detail.EndRangeLoad(generation, "model-server", atStart: null);
        await Assert.That(detail.LogStatus!).Contains("has not started");
        await Assert.That(detail.LogStatus!).Contains("SchedulingGated");
        await Assert.That(detail.IsLogStatusProblem).IsFalse();

        // The gate released and the container running: the watch tick loads the range again.
        row.Update(GatedReleasedAndRunning());
        detail.RefreshFromRow();
        await Assert.That(detail.LogGeneration).IsGreaterThan(generation);
        await Assert.That(detail.IsFollowingLogs).IsFalse();
    }

    /// <summary>An empty range of a pod that has run is the range being empty, said as before.</summary>
    [Test]
    public async Task Pod_detail_says_an_empty_range_of_a_running_pod_is_empty()
    {
        TestObjects.RedirectStores();
        var row = new ResourceRowViewModel(GatedReleasedAndRunning());
        var detail = new PodDetailTabViewModel(null, row, _ => { }, (_, _) => Task.CompletedTask);
        detail.IsFollowingLogs = false;
        var generation = detail.LogGeneration;

        // The snapshot sentence (on a real cluster EndLogStream turns it into the range's
        // empty sentence; this demo-mode pane keeps it as given).
        detail.EndRangeLoad(generation, "model-server", atStart: GatedReleasedAndRunningRun);
        await Assert.That(detail.LogStatus).IsEqualTo("Selected range loaded — this is a snapshot.");
        await Assert.That(detail.IsLogStatusProblem).IsFalse();

        // Nothing armed: a later tick does not load it again.
        detail.RefreshFromRow();
        await Assert.That(detail.LogGeneration).IsEqualTo(generation);
    }

    private static readonly ContainerRun GatedReleasedAndRunningRun =
        new(ContainerRunState.Running, null, null, "containerd://a", 0);

    /// <summary>G-17 in the multi-pod pane: an empty range of an unscheduled pod reads "not started", so the watch picks it up.</summary>
    [Test]
    public async Task An_empty_range_of_a_pod_that_has_not_started_ends_the_source_not_started()
    {
        var pane = Pane();
        pane.IsFollowing = false;
        var source = pane.RegisterSource("api-1", "app");
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("unscheduled")));

        var (state, message) = pane.RangeEndVerdict(source, receivedLines: false);
        await Assert.That(state).IsEqualTo(LogSourceState.NotStarted);
        await Assert.That(message).Contains("has not started");

        // Lines, or a pod that has run: the range was loaded.
        await Assert.That(pane.RangeEndVerdict(source, receivedLines: true).State).IsEqualTo(LogSourceState.Loaded);
        pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Pod("running")));
        await Assert.That(pane.RangeEndVerdict(source, receivedLines: false).State).IsEqualTo(LogSourceState.Loaded);
    }

    /// <summary>
    /// #341: pod detail reads a 404 the way the multi-pod pane does (#332). A pod deleted, or
    /// being deleted, while its logs are open ends the pane with "Pod deleted" and no problem;
    /// it used to show "could not find the requested resource" as an error.
    /// </summary>
    [Test]
    public async Task Pod_detail_ends_a_404_for_a_deleted_pod_as_deleted_not_as_a_problem()
    {
        var notFound = Refusal(HttpStatusCode.NotFound);

        foreach (var podNow in new[] { null, Deleting() })
        {
            var detail = Detail();
            detail.EndNotFound(detail.LogGeneration, notFound, podNow);
            await Assert.That(detail.LogStatus).IsEqualTo(WorkloadLogsTabViewModel.PodDeletedMessage);
            await Assert.That(detail.IsLogStatusProblem).IsFalse();
        }

        // Still there and not being deleted: the 404 is a failure, said as one.
        var running = Detail();
        running.EndNotFound(running.LogGeneration, notFound, Pod("running"));
        await Assert.That(running.IsLogStatusProblem).IsTrue();
        await Assert.That(running.LogStatus).IsNotEqualTo(WorkloadLogsTabViewModel.PodDeletedMessage);

        // A verdict for a stream the reader has since replaced changes nothing.
        var moved = Detail();
        moved.EndNotFound(moved.LogGeneration - 1, notFound, null);
        await Assert.That(moved.LogStatus).IsNotEqualTo(WorkloadLogsTabViewModel.PodDeletedMessage);
    }
}
