using KubeNimbus.Core.Applications;
using static KubeNimbus.Core.Tests.Applications.AppFixtures;

namespace KubeNimbus.Core.Tests.Applications;

/// <summary>
/// One test pair per rule: the state that fires it, and the nearest state that must not.
/// The negative half is the one that matters most — a rule that fires on a healthy app
/// puts it under "Needs attention", and a list that cries wolf is read once and then ignored.
/// </summary>
public class ApplicationRulesTests
{
    private static Finding? Rule(ApplicationAssessment a, string rule) => a.Findings.FirstOrDefault(f => f.Rule == rule);

    // ---------------------------------------------------------------- baseline

    [Test]
    public async Task A_deployment_with_every_pod_ready_is_healthy_and_says_so()
    {
        var a = Evaluate([Deployment("web", replicas: 2, ready: 2)], [Pod("web-1", "web"), Pod("web-2", "web")]);

        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Findings.Count).IsEqualTo(0);
        await Assert.That(a.Reason).IsEqualTo("2/2 pods ready");
        await Assert.That(a.PodsText).IsEqualTo("2/2");
        await Assert.That(a.Pods.All(p => p.State == PodDisplayState.Ready)).IsTrue();
    }

    [Test]
    public async Task A_deployment_scaled_to_zero_is_healthy()
    {
        var a = Evaluate([Deployment("web", replicas: 0, ready: 0)]);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Reason).IsEqualTo("Scaled to zero");
    }

    [Test]
    public async Task Pods_of_another_app_or_namespace_are_not_counted()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 1)],
            [Pod("web-1", "web"), Pod("api-1", "api", containerStatus: Waiting("CrashLoopBackOff")),
             Pod("web-x", "web", ns: "other", containerStatus: Waiting("CrashLoopBackOff"))]);

        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Pods.Count).IsEqualTo(1);
    }

    // -------------------------------------------------------------- crash loop

    [Test]
    public async Task CrashLoopBackOff_quotes_exit_code_reason_restarts_and_finish_time()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", ready: false,
                containerStatus: Waiting("CrashLoopBackOff", restarts: 6, last: Terminated(1, "Error", minutesAgo: 1)))],
            events: [Event("BackOff", "Pod", "web-1", "Back-off restarting failed container", count: 9)]);

        var f = Rule(a, "crash-loop")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(f.Severity).IsEqualTo(FindingSeverity.Error);
        await Assert.That(f.Title).IsEqualTo("1 pod is crash-looping");
        await Assert.That(f.Short).IsEqualTo("Crash-looping (exit 1)");
        await Assert.That(f.Pod).IsEqualTo("web-1");
        await Assert.That(f.Detail).Contains("exited with exit 1 (Error)");
        await Assert.That(f.Detail).Contains("restarted 6 times");
        var evidence = f.Evidence.Select(e => e.Text).ToList();
        await Assert.That(evidence).Contains("web-1 app state.waiting.reason: CrashLoopBackOff");
        await Assert.That(evidence).Contains($"lastState.terminated: exit 1 (Error), finishedAt {Ago(1)}");
        await Assert.That(evidence).Contains("restartCount: 6");
        await Assert.That(evidence.Any(e => e.StartsWith("Event BackOff ×9 on Pod web-1"))).IsTrue();
        await Assert.That(a.Reason).StartsWith("Crash-looping (exit 1)");
    }

    [Test]
    public async Task A_running_container_that_restarted_is_not_crash_looping()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 1)],
            [Pod("web-1", "web", containerStatus: Running(restarts: 3, last: Terminated(1, "Error", minutesAgo: 30)))]);

        await Assert.That(Rule(a, "crash-loop")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Restarts).IsEqualTo(3);
    }

    // --------------------------------------------------------------------- OOM

    [Test]
    public async Task OOMKilled_names_the_memory_limit_and_replaces_the_crash_loop_finding()
    {
        var spec = """{"containers":[{"name":"app","image":"app:1","resources":{"limits":{"memory":"512Mi"}}}]}""";
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", ready: false, spec: spec,
                containerStatus: Waiting("CrashLoopBackOff", restarts: 4, last: Terminated(137, "OOMKilled", minutesAgo: 1)))]);

        var f = Rule(a, "oom-killed")!;
        await Assert.That(Rule(a, "crash-loop")).IsNull();
        await Assert.That(f.Severity).IsEqualTo(FindingSeverity.Error);
        await Assert.That(f.Implies).IsEqualTo(AppStatus.Degraded);
        await Assert.That(f.Detail).Contains("Its memory limit is 512Mi");
        await Assert.That(f.Evidence.Select(e => e.Text)).Contains("app resources.limits.memory: 512Mi");
    }

    [Test]
    public async Task An_old_OOM_kill_on_a_running_container_is_history_not_attention()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 1)],
            [Pod("web-1", "web", containerStatus: Running(restarts: 1, last: Terminated(137, "OOMKilled", minutesAgo: 300)))]);

        var f = Rule(a, "oom-killed")!;
        await Assert.That(f.Severity).IsEqualTo(FindingSeverity.Info);
        await Assert.That(f.Detail).Contains("no memory limit");
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Reason).IsEqualTo("1/1 pods ready");
    }

    [Test]
    public async Task A_recent_OOM_kill_is_a_warning_in_the_reason_but_keeps_the_app_healthy()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 1)],
            [Pod("web-1", "web", containerStatus: Running(restarts: 1, last: Terminated(137, "OOMKilled", minutesAgo: 10)))]);

        await Assert.That(Rule(a, "oom-killed")!.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Reason).IsEqualTo("OOM-killed 10 min ago");
    }

    // -------------------------------------------------------------- image pull

    [Test]
    public async Task Image_pull_failures_quote_the_image_and_the_kubelet_message()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", phase: "Pending", ready: false,
                spec: """{"containers":[{"name":"app","image":"registry.example/web:9.9"}]}""",
                containerStatus: Waiting("ImagePullBackOff", "Back-off pulling image \"registry.example/web:9.9\""))]);

        var f = Rule(a, "image-pull")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(f.Short).IsEqualTo("Image pull failing: registry.example/web:9.9");
        await Assert.That(f.Evidence.Select(e => e.Text)).Contains("app image: registry.example/web:9.9");
    }

    [Test]
    public async Task ContainerCreating_is_not_an_image_pull_failure()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0, updated: 1)],
            [Pod("web-1", "web", phase: "Pending", ready: false, containerStatus: Waiting("ContainerCreating"))]);

        await Assert.That(Rule(a, "image-pull")).IsNull();
        await Assert.That(Rule(a, "container-config")).IsNull();
    }

    // -------------------------------------------------------- container config

    [Test]
    public async Task CreateContainerConfigError_quotes_the_missing_reference()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", phase: "Pending", ready: false,
                containerStatus: Waiting("CreateContainerConfigError", "secret \"db-creds\" not found"))]);

        var f = Rule(a, "container-config")!;
        await Assert.That(f.Short).IsEqualTo("CreateContainerConfigError: secret \"db-creds\" not found");
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
    }

    // ----------------------------------------------------------- unschedulable

    [Test]
    public async Task Unschedulable_pods_quote_the_scheduler_message_and_its_shortage()
    {
        var message = "0/3 nodes are available: 1 node(s) had untolerated taint {node.kubernetes.io/disk-pressure: }, 2 Insufficient cpu. preemption: 0/3 nodes are available: 3 No preemption victims found.";
        var conditions = $$"""[{"type":"PodScheduled","status":"False","reason":"Unschedulable","message":"{{message}}"}]""";
        var a = Evaluate(
            [Deployment("web", replicas: 2, ready: 0)],
            [Pod("web-1", "web", phase: "Pending", ready: false, conditions: conditions, containerStatus: Waiting("x")),
             Pod("web-2", "web", phase: "Pending", ready: false, conditions: conditions, containerStatus: Waiting("x"))],
            events: [Event("FailedScheduling", "Pod", "web-1", message, count: 5)]);

        var f = Rule(a, "unschedulable")!;
        await Assert.That(f.Title).IsEqualTo("None of 2 pods can be scheduled");
        await Assert.That(f.Short).IsEqualTo(
            "0 of 2 pods scheduled: 1 node(s) had untolerated taint {node.kubernetes.io/disk-pressure: }, 2 Insufficient cpu");
        await Assert.That(f.Evidence.Any(e => e.Field == "Event FailedScheduling ×5 on Pod web-1")).IsTrue();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
    }

    [Test]
    public async Task A_pending_pod_that_is_scheduled_is_not_unschedulable()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0, updated: 1)],
            [Pod("web-1", "web", phase: "Pending", ready: false, containerStatus: Waiting("ContainerCreating"))]);

        await Assert.That(Rule(a, "unschedulable")).IsNull();
    }

    // --------------------------------------------------------------- not ready

    [Test]
    public async Task A_pod_running_but_not_ready_for_a_while_is_degraded()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", ready: false)],
            events: [Event("Unhealthy", "Pod", "web-1", "Readiness probe failed: HTTP probe failed with statuscode: 503", count: 18)]);

        var f = Rule(a, "not-ready")!;
        await Assert.That(f.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(f.Evidence.Any(e => e.Value.Contains("statuscode: 503"))).IsTrue();
    }

    [Test]
    public async Task A_pod_that_just_started_is_warming_up_not_failing()
    {
        var conditions = $$"""[{"type":"PodScheduled","status":"True"},{"type":"Ready","status":"False","lastTransitionTime":"{{Iso(Now.AddSeconds(-30))}}"}]""";
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0, updated: 1)],
            [Pod("web-1", "web", ready: false, conditions: conditions)]);

        await Assert.That(Rule(a, "not-ready")!.Implies).IsEqualTo(AppStatus.Progressing);
        await Assert.That(a.Status).IsNotEqualTo(AppStatus.Degraded);
    }

    // --------------------------------------------------------- replica failure

    [Test]
    public async Task A_quota_refusal_counts_the_pods_that_were_never_created()
    {
        var conditions = """[{"type":"ReplicaFailure","status":"True","reason":"FailedCreate","message":"pods \"web-abc-x\" is forbidden: exceeded quota: shop-quota, requested: limits.memory=512Mi, used: limits.memory=3584Mi, limited: limits.memory=4Gi"}]""";
        var a = Evaluate(
            [Deployment("web", replicas: 4, ready: 2, total: 2, conditions: conditions)],
            [Pod("web-1", "web"), Pod("web-2", "web")],
            replicaSets: [ReplicaSet("web", "abc", 2, ready: 2, created: Ago(20))],
            events: [Event("FailedCreate", "ReplicaSet", "web-abc", "Error creating: pods \"web-abc-x\" is forbidden: exceeded quota", count: 3)]);

        var f = Rule(a, "replica-failure")!;
        await Assert.That(f.Title).IsEqualTo("2 of 4 pods were never created");
        await Assert.That(f.Short).IsEqualTo("2 pods not created: namespace quota");
        await Assert.That(f.Evidence.Any(e => e.Field == "Event FailedCreate ×3 on ReplicaSet web-abc")).IsTrue();
        await Assert.That(a.Pods.Count(p => p.IsMissing)).IsEqualTo(2);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
    }

    [Test]
    public async Task A_ReplicaFailure_condition_that_is_False_is_nothing()
    {
        var conditions = """[{"type":"ReplicaFailure","status":"False","reason":"FailedCreate"}]""";
        var a = Evaluate([Deployment("web", replicas: 1, ready: 1, conditions: conditions)], [Pod("web-1", "web")]);
        await Assert.That(Rule(a, "replica-failure")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
    }

    // ---------------------------------------------------- progress deadline

    [Test]
    public async Task ProgressDeadlineExceeded_is_stalled_and_says_for_how_long()
    {
        var conditions = $$"""[{"type":"Progressing","status":"False","reason":"ProgressDeadlineExceeded","message":"ReplicaSet \"web-new\" has timed out progressing.","lastTransitionTime":"{{Ago(120)}}"}]""";
        var a = Evaluate(
            [Deployment("web", replicas: 3, ready: 3, updated: 1, total: 4, conditions: conditions)],
            [Pod("web-1", "web"), Pod("web-2", "web"), Pod("web-3", "web")]);

        var f = Rule(a, "progress-deadline")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.Stalled);
        await Assert.That(f.Short).IsEqualTo("Rollout stalled for 2 h");
        await Assert.That(Rule(a, "rollout")).IsNull();
    }

    [Test]
    public async Task Progressing_True_is_not_stalled()
    {
        var conditions = """[{"type":"Progressing","status":"True","reason":"NewReplicaSetAvailable"}]""";
        var a = Evaluate([Deployment("web", replicas: 1, ready: 1, conditions: conditions)], [Pod("web-1", "web")]);
        await Assert.That(Rule(a, "progress-deadline")).IsNull();
    }

    // ---------------------------------------------------------------- rollout

    [Test]
    public async Task A_rollout_in_flight_is_progressing_and_names_the_new_revision()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 2, ready: 2, updated: 1, total: 3)],
            [Pod("web-1", "web"), Pod("web-2", "web"), Pod("web-3", "web")],
            replicaSets: [ReplicaSet("web", "old", 6, ready: 1, created: Ago(3000)), ReplicaSet("web", "new", 7, ready: 1, created: Ago(3))]);

        var f = Rule(a, "rollout")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.Progressing);
        await Assert.That(f.Short).IsEqualTo("Rolling out rev 7 · 1 of 2 pods updated");
        await Assert.That(Rule(a, "old-version-serving")).IsNull();
        await Assert.That(a.LastDeploy!.Revision).IsEqualTo("rev 7");
        await Assert.That(a.LastDeploy.At).IsEqualTo(Now.AddMinutes(-3));
    }

    [Test]
    public async Task When_the_new_version_has_no_ready_pod_the_old_one_is_said_to_serve_alone()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 2, ready: 1, updated: 1, total: 2)],
            [Pod("web-1", "web"), Pod("web-2", "web", ready: false)],
            replicaSets: [ReplicaSet("web", "old", 6, ready: 1, created: Ago(3000)), ReplicaSet("web", "new", 7, ready: 0, created: Ago(3))]);

        var f = Rule(a, "old-version-serving")!;
        await Assert.That(f.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(f.Evidence.Select(e => e.Text)).Contains("ReplicaSet web-new status.readyReplicas: 0");
    }

    [Test]
    public async Task A_settled_deployment_has_no_rollout_finding()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 2, ready: 2)],
            [Pod("web-1", "web"), Pod("web-2", "web")],
            replicaSets: [ReplicaSet("web", "old", 6, ready: 0, created: Ago(3000)), ReplicaSet("web", "new", 7, ready: 2, created: Ago(300))]);

        await Assert.That(Rule(a, "rollout")).IsNull();
        await Assert.That(Rule(a, "old-version-serving")).IsNull();
    }

    [Test]
    public async Task A_paused_rollout_is_suspended_not_progressing()
    {
        var a = Evaluate([Deployment("web", replicas: 2, ready: 2, updated: 1, paused: true)], [Pod("web-1", "web"), Pod("web-2", "web")]);
        await Assert.That(Rule(a, "rollout-paused")).IsNotNull();
        await Assert.That(Rule(a, "rollout")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Suspended);
    }

    // --------------------------------------------------- StatefulSet / DaemonSet

    [Test]
    public async Task A_StatefulSet_behind_its_update_revision_is_progressing()
    {
        var set = R("""
            {"apiVersion":"apps/v1","kind":"StatefulSet","metadata":{"name":"db","namespace":"shop"},
             "spec":{"replicas":3,"selector":{"matchLabels":{"app":"db"}}},
             "status":{"readyReplicas":3,"updatedReplicas":1,"currentRevision":"db-7c9","updateRevision":"db-8d1"}}
            """);
        var a = Evaluate([set], [Pod("db-0", "db"), Pod("db-1", "db"), Pod("db-2", "db")]);

        await Assert.That(Rule(a, "revision-lag")!.Title).IsEqualTo("Update in progress: 1 of 3 pods on the new revision");
        await Assert.That(a.Status).IsEqualTo(AppStatus.Progressing);
    }

    [Test]
    public async Task A_StatefulSet_on_one_revision_has_no_lag()
    {
        var set = R("""
            {"apiVersion":"apps/v1","kind":"StatefulSet","metadata":{"name":"db","namespace":"shop"},
             "spec":{"replicas":1,"selector":{"matchLabels":{"app":"db"}}},
             "status":{"readyReplicas":1,"updatedReplicas":1,"currentRevision":"db-8d1","updateRevision":"db-8d1"}}
            """);
        var a = Evaluate([set], [Pod("db-0", "db")]);
        await Assert.That(Rule(a, "revision-lag")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
    }

    [Test]
    public async Task A_DaemonSet_behind_on_some_nodes_is_progressing_and_one_that_is_not_is_healthy()
    {
        DynamicResource Set(int updated) => R(T("""
            {"apiVersion":"apps/v1","kind":"DaemonSet","metadata":{"name":"agent","namespace":"shop"},
             "spec":{"selector":{"matchLabels":{"app":"agent"}}},
             "status":{"desiredNumberScheduled":3,"numberReady":3,"updatedNumberScheduled":%u%}}
            """, ("u", updated)));
        var pods = new[] { Pod("a-1", "agent"), Pod("a-2", "agent"), Pod("a-3", "agent") };

        await Assert.That(Evaluate([Set(1)], pods).Status).IsEqualTo(AppStatus.Progressing);
        await Assert.That(Evaluate([Set(3)], pods).Status).IsEqualTo(AppStatus.Healthy);
    }

    // ------------------------------------------------------------ Jobs, CronJobs

    private static DynamicResource Job(string name, string conditions, string owner = "", int active = 0) => R(T("""
        {"apiVersion":"batch/v1","kind":"Job",
         "metadata":{"name":"%name%","namespace":"shop","creationTimestamp":"%created%"%owner%},
         "spec":{"selector":{"matchLabels":{"job-name":"%name%"}}},
         "status":{"active":%active%,"failed":7,"completionTime":"%done%","conditions":%conditions%}}
        """,
        ("name", name), ("created", Ago(40)), ("active", active), ("done", Ago(35)), ("conditions", conditions),
        ("owner", owner.Length > 0 ? $",\"ownerReferences\":[{{\"apiVersion\":\"batch/v1\",\"kind\":\"CronJob\",\"name\":\"{owner}\"}}]" : "")));

    private static DynamicResource CronJob(string name, bool suspend = false) => R(T("""
        {"apiVersion":"batch/v1","kind":"CronJob","metadata":{"name":"%name%","namespace":"shop"},
         "spec":{"schedule":"0 2 * * *","suspend":%suspend%},
         "status":{"lastScheduleTime":"%t%"}}
        """, ("name", name), ("suspend", suspend), ("t", Ago(40))));

    [Test]
    public async Task A_Job_that_hit_its_backoff_limit_is_degraded()
    {
        var failed = """[{"type":"Failed","status":"True","reason":"BackoffLimitExceeded","message":"Job has reached the specified backoff limit"}]""";
        var a = Evaluate([Job("migrate", failed)]);

        var f = Rule(a, "job-failed")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(f.Title).IsEqualTo("Job migrate failed (BackoffLimitExceeded)");
        await Assert.That(f.Short).IsEqualTo("Job failed: BackoffLimitExceeded");
    }

    [Test]
    public async Task A_Job_that_hit_its_deadline_says_so()
    {
        var failed = """[{"type":"Failed","status":"True","reason":"DeadlineExceeded"}]""";
        var f = Rule(Evaluate([Job("migrate", failed)]), "job-failed")!;
        await Assert.That(f.Detail).Contains("activeDeadlineSeconds");
    }

    [Test]
    public async Task A_completed_Job_is_healthy()
    {
        var complete = """[{"type":"Complete","status":"True"}]""";
        var a = Evaluate([Job("migrate", complete)]);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(a.Reason).IsEqualTo("Completed 35 min ago");
    }

    [Test]
    public async Task A_CronJob_whose_last_run_failed_is_degraded_and_an_older_failure_is_not_counted()
    {
        var failed = """[{"type":"Failed","status":"True","reason":"BackoffLimitExceeded"}]""";
        var complete = """[{"type":"Complete","status":"True"}]""";
        var older = R(Job("nightly-1", failed, owner: "nightly").Raw.GetRawText().Replace(Ago(40), Ago(1440)));

        var failing = Evaluate([CronJob("nightly")], jobs: [older, Job("nightly-2", failed, owner: "nightly")]);
        await Assert.That(failing.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(Rule(failing, "job-failed")!.Title).StartsWith("The last run of CronJob nightly (nightly-2) failed");

        var recovered = Evaluate([CronJob("nightly")], jobs: [older, Job("nightly-2", complete, owner: "nightly")]);
        await Assert.That(recovered.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(recovered.Reason).IsEqualTo("Last run succeeded 35 min ago");
    }

    [Test]
    public async Task A_suspended_CronJob_is_suspended_and_an_active_one_is_not()
    {
        var suspended = Evaluate([CronJob("report", suspend: true)]);
        await Assert.That(suspended.Status).IsEqualTo(AppStatus.Suspended);
        await Assert.That(Rule(suspended, "cronjob-suspended")).IsNotNull();

        var active = Evaluate([CronJob("report")]);
        await Assert.That(Rule(active, "cronjob-suspended")).IsNull();
        await Assert.That(active.Status).IsEqualTo(AppStatus.Healthy);
        await Assert.That(active.PodsText).IsEqualTo("—");
    }

    // -------------------------------------------------------------------- Argo

    [Test]
    public async Task A_failed_Argo_sync_is_sync_failed_with_its_message()
    {
        var argo = Argo("shop", sync: "OutOfSync", health: "Healthy",
            operation: """{"phase":"Failed","message":"one or more objects failed to apply: CronJob batch/v1beta1 is not served"}""");
        var a = Evaluate(argo: argo);

        await Assert.That(a.Status).IsEqualTo(AppStatus.SyncFailed);
        await Assert.That(Rule(a, "argo-sync-failed")!.Short)
            .IsEqualTo("Sync failed: one or more objects failed to apply: CronJob batch/v1beta1 is not served");
    }

    [Test]
    public async Task A_succeeded_Argo_sync_raises_nothing()
    {
        var a = Evaluate(argo: Argo("shop", operation: """{"phase":"Succeeded","message":"successfully synced"}"""));
        await Assert.That(Rule(a, "argo-sync-failed")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
    }

    [Test]
    public async Task An_Argo_ComparisonError_makes_the_app_unknown()
    {
        var argo = Argo("risk", sync: "Unknown", health: "Unknown",
            conditions: """[{"type":"ComparisonError","message":"rpc error: repository not accessible"}]""");
        var a = Evaluate(argo: argo);

        await Assert.That(a.Status).IsEqualTo(AppStatus.Unknown);
        await Assert.That(a.Reason).IsEqualTo("Argo CD cannot compare: rpc error: repository not accessible");
        await Assert.That(Rule(a, "argo-health")).IsNull();
    }

    [Test]
    public async Task A_harmless_Argo_condition_is_not_a_comparison_error()
    {
        var a = Evaluate(argo: Argo("x", conditions: """[{"type":"SharedResourceWarning","message":"x"}]"""));
        await Assert.That(Rule(a, "argo-comparison-error")).IsNull();
        await Assert.That(a.Status).IsEqualTo(AppStatus.Healthy);
    }

    [Test]
    public async Task OutOfSync_lists_the_drifted_resources_and_says_auto_sync_is_off()
    {
        var argo = Argo("ledger", sync: "OutOfSync", automated: false, resources: """
            [{"group":"apps","version":"v1","kind":"Deployment","namespace":"shop","name":"ledger","status":"OutOfSync"},
             {"version":"v1","kind":"Service","namespace":"shop","name":"ledger","status":"Synced"}]
            """);
        var a = Evaluate(argo: argo);

        var f = Rule(a, "argo-out-of-sync")!;
        await Assert.That(a.Status).IsEqualTo(AppStatus.OutOfSync);
        await Assert.That(f.Title).IsEqualTo("1 resource differs from Git");
        await Assert.That(f.Short).IsEqualTo("Differs from Git · auto-sync is off");
        await Assert.That(f.Evidence.Select(e => e.Text)).Contains("status.resources Deployment/ledger status: OutOfSync");
    }

    [Test]
    public async Task OutOfSync_while_a_sync_runs_is_progressing_not_attention()
    {
        var argo = Argo("n", sync: "OutOfSync", health: "Progressing", operation: """{"phase":"Running"}""");
        var a = Evaluate(argo: argo);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Progressing);
        await Assert.That(a.Status.NeedsAttention()).IsFalse();
    }

    [Test]
    public async Task Synced_is_not_out_of_sync()
    {
        await Assert.That(Rule(Evaluate(argo: Argo("x")), "argo-out-of-sync")).IsNull();
    }

    [Test]
    public async Task Argo_Missing_names_the_missing_resource()
    {
        var argo = Argo("settle", sync: "OutOfSync", health: "Missing", resources: """
            [{"group":"batch","version":"v1","kind":"CronJob","namespace":"shop","name":"settle","status":"OutOfSync","health":{"status":"Missing"}}]
            """);
        var a = Evaluate(argo: argo);

        await Assert.That(a.Status).IsEqualTo(AppStatus.Missing);
        await Assert.That(Rule(a, "argo-missing")!.Title).IsEqualTo("CronJob settle is declared in Git but absent from the cluster");
    }

    [Test]
    public async Task A_healthy_Argo_app_has_no_missing_finding()
    {
        await Assert.That(Rule(Evaluate(argo: Argo("x")), "argo-missing")).IsNull();
    }

    [Test]
    public async Task Pod_facts_outrank_a_healthy_Argo_verdict()
    {
        var a = Evaluate(
            [Deployment("web", replicas: 1, ready: 0)],
            [Pod("web-1", "web", ready: false, containerStatus: Waiting("CrashLoopBackOff", restarts: 2, last: Terminated(1, "Error", 1)))],
            argo: Argo("web"));
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
    }

    [Test]
    public async Task Argo_Degraded_with_nothing_else_to_say_is_stated_as_Argos_verdict()
    {
        var argo = Argo("x", health: "Degraded", resources: """
            [{"group":"apps","version":"v1","kind":"Deployment","namespace":"shop","name":"x","status":"Synced","health":{"status":"Degraded"}}]
            """);
        var a = Evaluate(argo: argo);
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
        await Assert.That(Rule(a, "argo-health")!.Detail).Contains("Deployment x Degraded");
        await Assert.That(a.Reason).IsEqualTo("Degraded per Argo CD");
    }

    [Test]
    public async Task The_Argo_deploy_is_the_last_deploy_and_a_chart_source_prints_its_version()
    {
        var history = $$"""[{"id":1,"revision":"1a0b7cf9e6d4835201ac9b3f7e15d240c6a83b19","deployedAt":"{{Ago(5000)}}"},{"id":2,"revision":"8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f","deployedAt":"{{Ago(12)}}"}]""";
        var git = Evaluate(argo: Argo("x", history: history));
        await Assert.That(git.LastDeploy!.Revision).IsEqualTo("8f3c1d9");
        await Assert.That(git.LastDeploy.At).IsEqualTo(Now.AddMinutes(-12));

        var chartHistory = $$"""[{"id":1,"revision":"62.3.0","deployedAt":"{{Ago(5000)}}"}]""";
        var chart = Evaluate(argo: Argo("x", history: chartHistory,
            source: """{"repoURL":"https://prometheus-community.github.io/helm-charts","chart":"kube-prometheus-stack","targetRevision":"62.3.0"}"""));
        await Assert.That(chart.LastDeploy!.Revision).IsEqualTo("chart 62.3.0");
    }

    // ----------------------------------------------------------------- ordering

    [Test]
    public async Task Status_order_puts_attention_first_and_progressing_after_it()
    {
        AppStatus[] expected =
        [
            AppStatus.Degraded, AppStatus.Missing, AppStatus.SyncFailed, AppStatus.Unknown,
            AppStatus.Stalled, AppStatus.OutOfSync, AppStatus.Progressing, AppStatus.Suspended, AppStatus.Healthy,
        ];
        await Assert.That(Enum.GetValues<AppStatus>().Order()).IsEquivalentTo(expected);
        await Assert.That(AppStatus.OutOfSync.NeedsAttention()).IsTrue();
        await Assert.That(AppStatus.Progressing.NeedsAttention()).IsFalse();
    }

    [Test]
    public async Task The_reason_joins_the_two_most_urgent_distinct_findings()
    {
        var conditions = """[{"type":"ReplicaFailure","status":"True","reason":"FailedCreate","message":"exceeded quota: q"}]""";
        var a = Evaluate(
            [Deployment("web", replicas: 4, ready: 0, conditions: conditions)],
            [Pod("web-1", "web", ready: false, containerStatus: Waiting("CrashLoopBackOff", restarts: 3, last: Terminated(1, "Error", 1)))]);

        await Assert.That(a.Reason).IsEqualTo("Crash-looping (exit 1) · 3 pods not created: namespace quota");
    }

    [Test]
    public async Task A_workload_short_of_ready_pods_with_no_other_explanation_still_says_so()
    {
        var a = Evaluate([Deployment("web", replicas: 3, ready: 1)], [Pod("web-1", "web"), Pod("web-2", "web", phase: "Succeeded")]);
        var f = Rule(a, "unavailable")!;
        await Assert.That(f.Title).IsEqualTo("1 of 3 pods of Deployment web are Ready");
        await Assert.That(a.Status).IsEqualTo(AppStatus.Degraded);
    }
}
