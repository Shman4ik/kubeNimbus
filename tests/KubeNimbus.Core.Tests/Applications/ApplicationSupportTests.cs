using KubeNimbus.Core.Applications;
using static KubeNimbus.Core.Tests.Applications.AppFixtures;

namespace KubeNimbus.Core.Tests.Applications;

/// <summary>Grouping into applications, and which namespaces the fallback watches.</summary>
public class ApplicationCatalogTests
{
    private static ClusterSnapshot Snapshot(
        IReadOnlyList<ArgoApplication>? argo = null, IReadOnlyList<DynamicResource>? workloads = null,
        IReadOnlyList<DynamicResource>? pods = null, IReadOnlyList<DynamicResource>? jobs = null) =>
        new(argo ?? [], workloads ?? [], [], jobs ?? [], pods ?? [], Now);

    private const string WebResource =
        """[{"group":"apps","version":"v1","kind":"Deployment","namespace":"shop","name":"web","status":"Synced"}]""";

    [Test]
    public async Task An_Argo_app_claims_the_workloads_its_status_lists_and_they_get_no_row_of_their_own()
    {
        var entries = ApplicationCatalog.Build(Snapshot(
            argo: [Argo("storefront", resources: WebResource)],
            workloads: [Deployment("web"), Deployment("api")],
            pods: [Pod("web-1", "web"), Pod("api-1", "api")]));

        await Assert.That(entries.Select(e => e.Key)).IsEquivalentTo(["argo:argocd/storefront", "Deployment:shop/api"]);
        var storefront = entries.Single(e => e.IsArgo);
        await Assert.That(storefront.Input.Workloads.Select(w => w.Name)).IsEquivalentTo(["web"]);
        await Assert.That(storefront.Input.Pods.Select(p => p.Name)).IsEquivalentTo(["web-1"]);
        await Assert.That(storefront.Source).IsEqualTo("Argo CD");
        await Assert.That(storefront.Namespace).IsEqualTo("shop");
    }

    [Test]
    public async Task A_status_entry_in_another_namespace_or_group_does_not_claim()
    {
        var elsewhere = """[{"group":"apps","version":"v1","kind":"Deployment","namespace":"other","name":"web"},{"group":"example.io","version":"v1","kind":"Deployment","namespace":"shop","name":"web"}]""";
        var entries = ApplicationCatalog.Build(Snapshot(argo: [Argo("x", resources: elsewhere)], workloads: [Deployment("web")]));
        await Assert.That(entries.Any(e => e.Key == "Deployment:shop/web")).IsTrue();
    }

    [Test]
    public async Task The_instance_label_and_the_tracking_id_claim_what_status_does_not_list()
    {
        var byLabel = Deployment("web", labels: """{"app.kubernetes.io/instance":"storefront"}""");
        var byAnnotation = Deployment("api", annotations: """{"argocd.argoproj.io/tracking-id":"storefront:apps/Deployment:shop/api"}""");
        var entries = ApplicationCatalog.Build(Snapshot(argo: [Argo("storefront")], workloads: [byLabel, byAnnotation]));

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Input.Workloads.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_prefix_of_an_app_name_does_not_claim_and_a_tracking_id_outranks_the_label()
    {
        var other = Deployment("web", annotations: """{"argocd.argoproj.io/tracking-id":"storefront-v2:apps/Deployment:shop/web"}""");
        var mismatched = Deployment("api",
            labels: """{"app.kubernetes.io/instance":"storefront"}""",
            annotations: """{"argocd.argoproj.io/tracking-id":"billing:apps/Deployment:shop/api"}""");
        var entries = ApplicationCatalog.Build(Snapshot(argo: [Argo("storefront")], workloads: [other, mismatched]));

        await Assert.That(entries.Single(e => e.IsArgo).Input.Workloads.Count).IsEqualTo(0);
        await Assert.That(entries.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Standalone_jobs_are_rows_only_while_running_or_failed_and_cronjob_jobs_never()
    {
        DynamicResource Job(string name, string status, string owner = "") => R(T("""
            {"apiVersion":"batch/v1","kind":"Job","metadata":{"name":"%name%","namespace":"shop"%owner%},
             "spec":{"selector":{"matchLabels":{"job-name":"%name%"}}},"status":%status%}
            """, ("name", name), ("status", status),
            ("owner", owner.Length > 0 ? $",\"ownerReferences\":[{{\"kind\":\"CronJob\",\"name\":\"{owner}\"}}]" : "")));

        var workloads = new[]
        {
            Job("running", """{"active":1}"""),
            Job("failed", """{"conditions":[{"type":"Failed","status":"True"}]}"""),
            Job("done", """{"conditions":[{"type":"Complete","status":"True"}]}"""),
            Job("nightly-1", """{"active":1}""", owner: "nightly"),
        };
        var entries = ApplicationCatalog.Build(Snapshot(workloads: workloads));
        await Assert.That(entries.Select(e => e.Name)).IsEquivalentTo(["running", "failed"]);
    }

    [Test]
    public async Task ReplicaSets_and_pods_are_never_rows()
    {
        var entries = ApplicationCatalog.Build(Snapshot(workloads: [ReplicaSet("web", "abc", 1, 1, Ago(5))], pods: [Pod("p", "web")]));
        await Assert.That(entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Fallback_namespaces_come_from_context_selection_recents_and_argo_destinations_once_each()
    {
        var namespaces = ApplicationScope.CandidateNamespaces(
            "team-a", ["team-b", "team-a", ""], [Argo("x", destinationNamespace: "payments"), Argo("y", destinationNamespace: "team-b")], "team-c");
        await Assert.That(namespaces).IsEquivalentTo(["team-a", "team-c", "team-b", "payments"]);
    }

    [Test]
    public async Task Only_kube_prefixed_namespaces_are_system()
    {
        await Assert.That(ApplicationScope.IsSystemNamespace("kube-system")).IsTrue();
        await Assert.That(ApplicationScope.IsSystemNamespace("kube-node-lease")).IsTrue();
        await Assert.That(ApplicationScope.IsSystemNamespace("kubeflow")).IsFalse();
        await Assert.That(ApplicationScope.IsSystemNamespace("default")).IsFalse();
    }

    [Test]
    public async Task Argo_ui_url_comes_from_argocd_cm_and_is_absent_without_it()
    {
        var cm = R("""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"argocd-cm","namespace":"argocd"},"data":{"url":"https://argo.example.com/"}}""");
        var baseUrl = ArgoUi.BaseUrl(cm)!;
        await Assert.That(ArgoUi.ApplicationUrl(baseUrl, Argo("checkout")).AbsoluteUri)
            .IsEqualTo("https://argo.example.com/applications/argocd/checkout");

        var empty = R("""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"argocd-cm"},"data":{}}""");
        await Assert.That(ArgoUi.BaseUrl(empty)).IsNull();
        await Assert.That(ArgoUi.BaseUrl(null)).IsNull();
    }
}

/// <summary>The kubelet's log rules, as <c>validateContainerLogStatus</c> applies them.</summary>
public class PodLogRunsTests
{
    private static ContainerFacts Container(DynamicResource pod) => PodFacts.Read(pod).Containers[0];

    [Test]
    public async Task A_container_waiting_in_CrashLoopBackOff_has_one_run_not_two()
    {
        var pod = Pod("p", "a", ready: false, containerStatus: Waiting("CrashLoopBackOff", restarts: 6, last: Terminated(1, "Error", 1)));
        var runs = PodLogRuns.For(Container(pod));

        await Assert.That(runs.Count).IsEqualTo(1);
        await Assert.That(runs[0].Previous).IsFalse();
        await Assert.That(runs[0].Label).IsEqualTo("Last run");
        await Assert.That(runs[0].EndedWith!.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task A_running_container_with_a_last_termination_has_a_distinct_previous_run()
    {
        var pod = Pod("p", "a", containerStatus: Running(restarts: 1, last: Terminated(137, "OOMKilled", 30)));
        var runs = PodLogRuns.For(Container(pod));

        await Assert.That(runs.Select(r => r.Previous)).IsEquivalentTo([false, true]);
        await Assert.That(runs[1].EndedWith!.Reason).IsEqualTo("OOMKilled");
    }

    [Test]
    public async Task A_termination_the_kubelet_no_longer_holds_is_not_offered()
    {
        var pod = Pod("p", "a", ready: false, containerStatus: Waiting("CrashLoopBackOff", last: Terminated(1, "Error", 1, id: "")));
        await Assert.That(PodLogRuns.For(Container(pod)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task No_logs_says_why_from_the_scheduling_condition_the_image_and_the_phase()
    {
        var unscheduled = Pod("p", "a", phase: "Pending", ready: false, containerStatus: Waiting("x"),
            conditions: """[{"type":"PodScheduled","status":"False","reason":"Unschedulable","message":"0/3 nodes are available"}]""");
        await Assert.That(PodLogRuns.NoLogsReason(PodFacts.Read(unscheduled)))
            .IsEqualTo("No logs: the pod has not been scheduled to a node, so no container has started. The scheduler says: 0/3 nodes are available");

        var pulling = Pod("p", "a", phase: "Pending", ready: false, containerStatus: Waiting("ImagePullBackOff", "not found"));
        await Assert.That(PodLogRuns.NoLogsReason(PodFacts.Read(pulling))).StartsWith("No logs: the image for app cannot be pulled");

        var creating = Pod("p", "a", phase: "Pending", ready: false, containerStatus: Waiting("ContainerCreating"));
        await Assert.That(PodLogRuns.NoLogsReason(PodFacts.Read(creating))).IsEqualTo("No logs yet: container app is being created.");

        var running = Pod("p", "a");
        await Assert.That(PodLogRuns.NoLogsReason(PodFacts.Read(running))).IsNull();
    }

    [Test]
    public async Task Display_state_reads_failing_pending_ready_and_terminating()
    {
        await Assert.That(PodFacts.Read(Pod("p", "a")).DisplayState).IsEqualTo(PodDisplayState.Ready);
        await Assert.That(PodFacts.Read(Pod("p", "a", ready: false)).DisplayState).IsEqualTo(PodDisplayState.NotReady);
        await Assert.That(PodFacts.Read(Pod("p", "a", ready: false, containerStatus: Waiting("CrashLoopBackOff"))).DisplayState)
            .IsEqualTo(PodDisplayState.Failing);
        await Assert.That(PodFacts.Read(Pod("p", "a", phase: "Pending", ready: false, containerStatus: Waiting("ContainerCreating"))).DisplayState)
            .IsEqualTo(PodDisplayState.Pending);
        await Assert.That(PodFacts.Read(Pod("p", "a", deletion: Ago(1))).DisplayState).IsEqualTo(PodDisplayState.Terminating);
    }
}

/// <summary>What changed between the previous and the current ReplicaSet.</summary>
public class PodTemplateDiffTests
{
    private static string Template(string containers, string annotations = "{}", string volumes = "[]") =>
        T("""{"metadata":{"annotations":%a%},"spec":{"containers":%c%,"volumes":%v%}}""",
            ("a", annotations), ("c", containers), ("v", volumes));

    [Test]
    public async Task Image_resources_and_env_references_are_compared()
    {
        var before = ReplicaSet("web", "old", 6, 0, Ago(3000), template: Template("""
            [{"name":"app","image":"web:9.0.0","resources":{"requests":{"cpu":"500m"},"limits":{"memory":"256Mi"}},
              "env":[{"name":"GATEWAY_URL","valueFrom":{"configMapKeyRef":{"name":"web-config","key":"url"}}},
                     {"name":"MODE","value":"blue"}]}]
            """));
        var after = ReplicaSet("web", "new", 7, 0, Ago(12), template: Template("""
            [{"name":"app","image":"web:9.0.1","resources":{"requests":{"cpu":"2"},"limits":{"memory":"256Mi"}},
              "env":[{"name":"MODE","value":"green"},{"name":"TOKEN","valueFrom":{"secretKeyRef":{"name":"web-token","key":"t"}}}]}]
            """));

        var diff = PodTemplateDiff.Compare(before, after);
        var text = diff.Changes.Select(c => $"{c.Field}|{c.Before}|{c.After}").ToList();

        await Assert.That(diff.PreviousRevision).IsEqualTo(6);
        await Assert.That(diff.CurrentRevision).IsEqualTo(7);
        await Assert.That(text).Contains("image (app)|web:9.0.0|web:9.0.1");
        await Assert.That(text).Contains("requests.cpu (app)|500m|2");
        await Assert.That(text).Contains("env GATEWAY_URL (app)|from ConfigMap web-config key url|");
        await Assert.That(text).Contains("env MODE (app)|value|value changed");
        await Assert.That(text).Contains("env TOKEN (app)||from Secret web-token key t");
        await Assert.That(text.Any(t => t.StartsWith("limits.memory"))).IsFalse();
    }

    /// <summary>The rule the page is under: a literal env value never reaches it, in either direction.</summary>
    [Test]
    public async Task No_env_value_is_ever_printed()
    {
        var before = ReplicaSet("web", "old", 1, 0, Ago(30), template: Template("""[{"name":"app","image":"a","env":[{"name":"PASSWORD","value":"hunter2"}]}]"""));
        var after = ReplicaSet("web", "new", 2, 0, Ago(3), template: Template("""[{"name":"app","image":"a","env":[{"name":"PASSWORD","value":"correct-horse"},{"name":"NEW","value":"s3cr3t"}]}]"""));

        var all = string.Join("\n", PodTemplateDiff.Compare(before, after).Changes.Select(c => $"{c.Field} {c.Before} {c.After}"));
        await Assert.That(all).DoesNotContain("hunter2");
        await Assert.That(all).DoesNotContain("correct-horse");
        await Assert.That(all).DoesNotContain("s3cr3t");
        await Assert.That(all).Contains("env NEW (app)  set");
    }

    [Test]
    public async Task Identical_templates_have_no_changes_and_a_restart_is_named()
    {
        var t = Template("""[{"name":"app","image":"a","command":["run"],"args":["--x"]}]""");
        var same = PodTemplateDiff.Compare(ReplicaSet("web", "a", 1, 0, Ago(9), template: t), ReplicaSet("web", "b", 2, 0, Ago(3), template: t));
        await Assert.That(same.Changes.Count).IsEqualTo(0);

        var restarted = Template("""[{"name":"app","image":"a","command":["run"],"args":["--x"]}]""",
            annotations: """{"kubectl.kubernetes.io/restartedAt":"2026-07-30T08:40:00Z"}""");
        var restart = PodTemplateDiff.Compare(ReplicaSet("web", "a", 1, 0, Ago(9), template: t), ReplicaSet("web", "b", 2, 0, Ago(3), template: restarted));
        await Assert.That(restart.Changes.Single().Field).IsEqualTo("restartedAt (rollout restart)");
    }

    [Test]
    public async Task Command_args_envFrom_and_mounted_configmaps_are_compared()
    {
        var before = ReplicaSet("web", "a", 1, 0, Ago(9), template: Template(
            """[{"name":"app","image":"a","args":["--port=80"],"envFrom":[{"configMapRef":{"name":"old-env"}}]}]""",
            volumes: """[{"name":"cfg","configMap":{"name":"web-v1"}}]"""));
        var after = ReplicaSet("web", "b", 2, 0, Ago(3), template: Template(
            """[{"name":"app","image":"a","args":["--port=8080"],"envFrom":[{"secretRef":{"name":"new-env"}}]}]""",
            volumes: """[{"name":"cfg","configMap":{"name":"web-v2"}}]"""));

        var text = PodTemplateDiff.Compare(before, after).Changes.Select(c => $"{c.Field}|{c.Before}|{c.After}").ToList();
        await Assert.That(text).Contains("args (app)|--port=80|--port=8080");
        await Assert.That(text).Contains("envFrom (app)|ConfigMap old-env|");
        await Assert.That(text).Contains("envFrom (app)||Secret new-env");
        await Assert.That(text).Contains("volume|cfg: ConfigMap web-v1|");
        await Assert.That(text).Contains("volume||cfg: ConfigMap web-v2");
    }

    [Test]
    public async Task The_two_newest_replicasets_are_picked_by_revision_not_by_age()
    {
        var deployment = Deployment("web");
        var picked = PodTemplateDiff.PickReplicaSets(deployment,
            [ReplicaSet("web", "r5", 5, 0, Ago(1)), ReplicaSet("web", "r7", 7, 0, Ago(100)), ReplicaSet("web", "r6", 6, 0, Ago(200)),
             ReplicaSet("other", "r9", 9, 0, Ago(1))]);

        await Assert.That(picked!.Value.Current.Name).IsEqualTo("web-r7");
        await Assert.That(picked.Value.Previous.Name).IsEqualTo("web-r6");
        await Assert.That(PodTemplateDiff.PickReplicaSets(deployment, [ReplicaSet("web", "r1", 1, 0, Ago(1))])).IsNull();
    }
}

public class ApplicationTimelineTests
{
    [Test]
    public async Task The_window_is_the_smallest_of_15_30_45_60_minutes_that_holds_the_last_hour()
    {
        TimelineItem At(int minutes) => new(Now.AddMinutes(-minutes), TimelineKind.WarningEvent, "x", "");

        await Assert.That(ApplicationTimeline.Build([At(3)], Now).Span).IsEqualTo(TimeSpan.FromMinutes(15));
        await Assert.That(ApplicationTimeline.Build([At(3), At(22)], Now).Span).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(ApplicationTimeline.Build([At(44)], Now).Span).IsEqualTo(TimeSpan.FromMinutes(45));
        await Assert.That(ApplicationTimeline.Build([At(59), At(300)], Now).Span).IsEqualTo(TimeSpan.FromMinutes(60));
        await Assert.That(ApplicationTimeline.Build([], Now).Span).IsEqualTo(TimeSpan.FromMinutes(15));
    }

    [Test]
    public async Task The_deploy_before_the_window_becomes_the_caption()
    {
        var items = new[]
        {
            new TimelineItem(Now.AddDays(-4), TimelineKind.Deploy, "5d27ea0", ""),
            new TimelineItem(Now.AddDays(-9), TimelineKind.Deploy, "1a0b7cf", ""),
            new TimelineItem(Now.AddMinutes(-12), TimelineKind.Deploy, "8f3c1d9", ""),
        };
        var window = ApplicationTimeline.Build(items, Now);

        await Assert.That(window.Items.Select(i => i.Label)).IsEquivalentTo(["8f3c1d9"]);
        await Assert.That(window.DeployBefore!.Label).IsEqualTo("5d27ea0");
        await Assert.That(window.Position(Now)).IsEqualTo(1d);
    }

    /// <summary>One termination per container, because that is all the kubelet keeps.</summary>
    [Test]
    public async Task Collect_draws_one_dated_termination_per_container_not_one_per_restart()
    {
        var input = Input(
            pods: [Pod("web-1", "web", ready: false, containerStatus: Waiting("CrashLoopBackOff", restarts: 14, last: Terminated(1, "Error", 2)))],
            replicaSets: [ReplicaSet("web", "new", 7, 0, Ago(12)), ReplicaSet("web", "restart", 8, 0, Ago(5))],
            events: [Event("BackOff", "Pod", "web-1", "Back-off", count: 42), Event("Pulled", "Pod", "web-1", "ok", type: "Normal")],
            argo: Argo("web", history: $$"""[{"id":3,"revision":"8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f","deployedAt":"{{Ago(12)}}"}]"""));

        var items = ApplicationTimeline.Collect(input);
        await Assert.That(items.Count(i => i.Kind == TimelineKind.Termination)).IsEqualTo(1);
        await Assert.That(items.Count(i => i.Kind == TimelineKind.WarningEvent)).IsEqualTo(1);
        await Assert.That(items.Single(i => i.Kind == TimelineKind.WarningEvent).Label).IsEqualTo("BackOff ×42");
        // The sync and the ReplicaSet it created are one deploy; a restart seven minutes later is its own.
        await Assert.That(items.Where(i => i.Kind == TimelineKind.Deploy).Select(i => i.Label)).IsEquivalentTo(["8f3c1d9 · rev 7", "rev 8"]);
    }
}

public class GitCompareLinkTests
{
    [Test]
    [Arguments("https://github.com/acme/deploy.git", "https://github.com/acme/deploy/compare/aaa...bbb")]
    [Arguments("git@github.com:acme/deploy.git", "https://github.com/acme/deploy/compare/aaa...bbb")]
    [Arguments("https://gitlab.com/acme/platform/deploy.git", "https://gitlab.com/acme/platform/deploy/-/compare/aaa...bbb")]
    [Arguments("https://gitlab.internal.example/acme/deploy", "https://gitlab.internal.example/acme/deploy/-/compare/aaa...bbb")]
    [Arguments("https://dev.azure.com/acme/Platform/_git/deploy", "https://dev.azure.com/acme/Platform/_git/deploy/branchCompare?baseVersion=GCaaa&targetVersion=GCbbb")]
    [Arguments("https://acme@dev.azure.com/acme/Platform/_git/deploy", "https://dev.azure.com/acme/Platform/_git/deploy/branchCompare?baseVersion=GCaaa&targetVersion=GCbbb")]
    [Arguments("git@ssh.dev.azure.com:v3/acme/Platform/deploy", "https://dev.azure.com/acme/Platform/_git/deploy/branchCompare?baseVersion=GCaaa&targetVersion=GCbbb")]
    [Arguments("https://acme.visualstudio.com/Platform/_git/deploy", "https://acme.visualstudio.com/Platform/_git/deploy/branchCompare?baseVersion=GCaaa&targetVersion=GCbbb")]
    [Arguments("https://bitbucket.org/acme/deploy.git", "https://bitbucket.org/acme/deploy/branches/compare/bbb..aaa#diff")]
    public async Task Known_hosts_get_their_own_compare_url(string repo, string expected)
    {
        await Assert.That(GitCompareLink.CompareUrl(repo, "aaa", "bbb")!.AbsoluteUri).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://git.example.com/acme/deploy.git")]
    [Arguments("https://github.com/acme")]
    [Arguments("")]
    [Arguments("not a url")]
    public async Task Unknown_hosts_and_malformed_urls_get_no_link(string repo)
    {
        await Assert.That(GitCompareLink.CompareUrl(repo, "aaa", "bbb")).IsNull();
    }

    [Test]
    public async Task The_two_latest_history_revisions_are_compared_and_a_chart_gets_versions_without_a_link()
    {
        var history = """[{"id":1,"revision":"1a0b7cf9e6d4835201ac9b3f7e15d240c6a83b19"},{"id":2,"revision":"5d27ea01c93b6478fe1d0a852c37b94f6e0d1a83"},{"id":3,"revision":"8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f"}]""";
        var compare = GitCompareLink.For(Argo("x", history: history))!;
        await Assert.That(compare.FromShort).IsEqualTo("5d27ea0");
        await Assert.That(compare.ToShort).IsEqualTo("8f3c1d9");
        await Assert.That(compare.Link!.AbsoluteUri).IsEqualTo(
            "https://github.com/acme/deploy/compare/5d27ea01c93b6478fe1d0a852c37b94f6e0d1a83...8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f");

        var chart = GitCompareLink.For(Argo("x", history: """[{"id":1,"revision":"62.2.0"},{"id":2,"revision":"62.3.0"}]""",
            source: """{"repoURL":"https://prometheus-community.github.io/helm-charts","chart":"kube-prometheus-stack"}"""))!;
        await Assert.That(chart.IsChart).IsTrue();
        await Assert.That(chart.Link).IsNull();
        await Assert.That(chart.ToShort).IsEqualTo("62.3.0");

        await Assert.That(GitCompareLink.For(Argo("x", history: """[{"id":1,"revision":"abc"}]"""))).IsNull();
    }
}

public class LinkedResourcesTests
{
    [Test]
    public async Task References_from_the_spec_link_services_ingresses_routes_hpa_pdb_configmaps_secrets_and_claims()
    {
        var web = R("""
            {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"web","namespace":"shop"},
             "spec":{"selector":{"matchLabels":{"app":"web"}},
               "template":{"metadata":{"labels":{"app":"web","tier":"front"}},
                 "spec":{"containers":[{"name":"app","image":"a",
                     "env":[{"name":"URL","valueFrom":{"configMapKeyRef":{"name":"web-config","key":"url"}}},
                            {"name":"TOKEN","valueFrom":{"secretKeyRef":{"name":"web-token","key":"t"}}}],
                     "envFrom":[{"configMapRef":{"name":"web-env"}}]}],
                   "volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"web-data"}},{"name":"tls","secret":{"secretName":"web-tls"}}]}}}}
            """);
        var candidates = new[]
        {
            R("""{"apiVersion":"v1","kind":"Service","metadata":{"name":"web","namespace":"shop"},"spec":{"selector":{"app":"web"}}}"""),
            R("""{"apiVersion":"v1","kind":"Service","metadata":{"name":"api","namespace":"shop"},"spec":{"selector":{"app":"api"}}}"""),
            R("""{"apiVersion":"v1","kind":"Service","metadata":{"name":"headless","namespace":"shop"},"spec":{}}"""),
            R("""{"apiVersion":"v1","kind":"Service","metadata":{"name":"web","namespace":"other"},"spec":{"selector":{"app":"web"}}}"""),
            R("""{"apiVersion":"networking.k8s.io/v1","kind":"Ingress","metadata":{"name":"web","namespace":"shop"},"spec":{"rules":[{"http":{"paths":[{"backend":{"service":{"name":"web"}}}]}}]}}"""),
            R("""{"apiVersion":"networking.k8s.io/v1","kind":"Ingress","metadata":{"name":"api","namespace":"shop"},"spec":{"defaultBackend":{"service":{"name":"api"}}}}"""),
            R("""{"apiVersion":"gateway.networking.k8s.io/v1","kind":"HTTPRoute","metadata":{"name":"web","namespace":"shop"},"spec":{"rules":[{"backendRefs":[{"name":"web","port":80}]}]}}"""),
            R("""{"apiVersion":"autoscaling/v2","kind":"HorizontalPodAutoscaler","metadata":{"name":"web","namespace":"shop"},"spec":{"scaleTargetRef":{"apiVersion":"apps/v1","kind":"Deployment","name":"web"}}}"""),
            R("""{"apiVersion":"autoscaling/v2","kind":"HorizontalPodAutoscaler","metadata":{"name":"api","namespace":"shop"},"spec":{"scaleTargetRef":{"kind":"Deployment","name":"api"}}}"""),
            R("""{"apiVersion":"policy/v1","kind":"PodDisruptionBudget","metadata":{"name":"web","namespace":"shop"},"spec":{"selector":{"matchLabels":{"tier":"front"}}}}"""),
            R("""{"apiVersion":"policy/v1","kind":"PodDisruptionBudget","metadata":{"name":"api","namespace":"shop"},"spec":{"selector":{"matchLabels":{"app":"api"}}}}"""),
        };

        var links = LinkedResources.Find([web], [], candidates);
        var keys = links.Select(l => $"{l.Kind}/{l.Name}").ToList();

        await Assert.That(keys).IsEquivalentTo([
            "Service/web", "Ingress/web", "HTTPRoute/web", "HorizontalPodAutoscaler/web", "PodDisruptionBudget/web",
            "PersistentVolumeClaim/web-data", "Secret/web-tls", "ConfigMap/web-env", "ConfigMap/web-config", "Secret/web-token",
        ]);
        await Assert.That(links.Single(l => l.Kind == "Ingress").Via).IsEqualTo("backend Service web");
        await Assert.That(links.All(l => l.Namespace == "shop")).IsTrue();
    }
}
