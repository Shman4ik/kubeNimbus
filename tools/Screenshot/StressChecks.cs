using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The stress mode (<c>-- --stress</c>): every surface that shows cluster data, fed the
/// amount a large cluster produces, with the work it does measured against a budget.
/// </summary>
/// <remarks>
/// <para>
/// Screenshots never caught this class of bug, because a fixture is a dozen rows and a
/// dozen rows are fast whatever the code does. Two freezes shipped that way: the log panes
/// realized every buffered line as live controls (4,000 lines, ~37,000 controls, seven
/// seconds of layout, and every theme switch restyled all of them), and their scrollback
/// trim raised a collection notification per line (396,000 for one "Everything" flush).
/// </para>
/// <para>
/// Three measurements per check, and the first two are what the budgets rest on because
/// they are the same on every machine: the <b>visuals</b> in the window afterwards (a list
/// that does not virtualize has one row of controls per item), and the <b>notifications</b>
/// the watched collection raised during the action (a list that is rebuilt item by item
/// raises one per item). Time is the third, with a budget loose enough for a slow CI runner
/// — it is there to catch the order-of-magnitude regressions, seconds where there should be
/// milliseconds, not to benchmark.
/// </para>
/// </remarks>
internal static class StressChecks
{
    private const int LogBurst = 200_000;
    private const int Pods = 5_000;
    private const int ArgoResources = 3_000;
    private const int ConfigMapKeys = 5_000;
    private const int ServicePods = 1_000;
    private const int Apps = 2_000;
    private const int DaemonSetPods = 1_000;

    // A window with every list virtualized holds on the order of a thousand visuals.
    private const int VisualBudget = 6_000;
    private const int SlowMs = 3_000;
    private const int ThemeMs = 1_500;

    private sealed record Result(string Name, string Action, long Ms, int Visuals, int? Notifications, int NotificationBudget, long MsBudget)
    {
        public bool Failed =>
            Visuals > VisualBudget
            || (Notifications is { } n && n > NotificationBudget)
            || Ms > MsBudget;
    }

    internal static int Run(Func<ClusterTabViewModel, bool, Control> hostIn, string? filter = null)
    {
        Control host(ClusterTabViewModel tab) => hostIn(tab, false);
        Control hostApplications(ClusterTabViewModel tab) => hostIn(tab, true);

        var results = new List<Result>();

        // `-- --stress <substring>` runs only the checks whose name contains it.
        IEnumerable<Result> Check(
            string name,
            Func<ClusterTabViewModel, Control> host,
            Func<ClusterTabViewModel> build,
            Func<ClusterTabViewModel, (INotifyCollectionChanged? Watched, Action Act, string Label, int NotificationBudget)> arrange) =>
            filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                ? []
                : Measure(name, host, build(), arrange);

        results.AddRange(Check("logs-workload", host, () => ClusterTabScenarios.DemoWorkloadLogs(), tab =>
        {
            var pane = (WorkloadLogsTabViewModel)tab.SelectedInspectorTab!;
            var source = pane.RegisterSource("stress-pod-0", "app");
            for (var i = 0; i < LogBurst; i++)
            {
                pane.Enqueue($"{Stamp(i)} info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET /api/{i} 200", source);
            }

            return (pane.LogLines, () => pane.Flush(force: true), $"flush {LogBurst:N0} lines", 4);
        }));

        results.AddRange(Check("logs-pod", host, () => ClusterTabScenarios.DemoPodDetail(), tab =>
        {
            var pane = (PodDetailTabViewModel)tab.SelectedInspectorTab!;
            for (var i = 0; i < LogBurst; i++)
            {
                pane.Enqueue($"{Stamp(i)} info: request {i} finished in 3ms");
            }

            return (pane.LogLines, pane.FlushLogLines, $"flush {LogBurst:N0} lines", 4);
        }));

        results.AddRange(Check("resource-list", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            for (var i = 0; i < Pods; i++)
            {
                var row = new ResourceRowViewModel(StressPod(i));
                row.ApplyUsage(1_000_000L * (i % 997), 1024L * 1024 * (i % 512));
                tab.Rows.Add(row);
            }

            return (tab.VisibleRows, () => tab.RowFilter = "stress-pod-1", "search keystroke", 2);
        }));

        results.AddRange(Check("resource-list-initial", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            // The watch's initial list as AsyncBatching delivers it: a Reset, every object,
            // and the Synced frame, in one batch.
            var events = new List<ResourceEvent<DynamicResource>> { ResourceEvent<DynamicResource>.Reset };
            events.AddRange(Enumerable.Range(0, Pods).Select(i => new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressPod(i))));
            events.Add(ResourceEvent<DynamicResource>.Synced);
            return (tab.VisibleRows, () => tab.ApplyBatch(events), $"initial list of {Pods:N0} pods", 4);
        }));

        results.AddRange(Check("fleet-relist", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            tab.ApplyFleetBatch(Enumerable.Range(0, Pods)
                .Select(i => new FleetResourceEvent(i % 2 == 0 ? "east" : "west",
                    new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressPod(i))))
                .ToList());
            return (tab.Rows, () => tab.ApplyFleet(new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Reset)),
                $"one member of two relists ({Pods / 2:N0} rows)", 2);
        }));

        results.AddRange(Check("applications-list", hostApplications, ApplicationsTab, tab =>
        {
            var apps = tab.Applications;
            apps.Apply("Deployment", ResourceEvent<DynamicResource>.Reset);
            for (var i = 0; i < Apps; i++)
            {
                apps.Apply("Deployment", new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressDeployment(i)));
            }

            apps.Apply("Deployment", ResourceEvent<DynamicResource>.Synced);
            apps.RebuildNow();
            return (apps.VisibleRows, () => apps.Filter = "stress-app-1", $"search keystroke over {apps.VisibleRows.Count:N0} apps", 2);
        }));

        results.AddRange(Check("applications-page", hostApplications, ApplicationsTab, tab =>
        {
            var apps = tab.Applications;
            apps.Apply("DaemonSet", new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressDaemonSet()));
            apps.Apply("Pod", ResourceEvent<DynamicResource>.Reset);
            for (var i = 0; i < DaemonSetPods; i++)
            {
                apps.Apply("Pod", new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressAgentPod(i)));
            }

            apps.Apply("Pod", ResourceEvent<DynamicResource>.Synced);
            apps.RebuildNow();
            apps.Open(apps.Rows.First(r => r.Name == "node-agent"));
            var page = apps.Page!;
            return (page.Pods, () =>
            {
                // A pod of the DaemonSet restarts: the list rebuilds and the open page with it.
                apps.Apply("Pod", new ResourceEvent<DynamicResource>(ResourceEventType.Modified, StressAgentPod(7, restarts: 1)));
                apps.RebuildNow();
            }, $"list rebuild, page open on {page.Pods.Count - 1:N0} pods", 4);
        }));

        results.AddRange(Check("resource-list-sort", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            for (var i = 0; i < Pods; i++)
            {
                var row = new ResourceRowViewModel(StressPod(i));
                row.ApplyUsage(1_000_000L * (i % 997), 1024L * 1024 * (i % 512));
                tab.Rows.Add(row);
            }

            return (tab.VisibleRows, () => tab.SetSort(ResourceColumn.Name, descending: true, persist: false), "header sort", 2);
        }));

        results.AddRange(Check("resource-list-metrics-poll", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            for (var i = 0; i < Pods; i++)
            {
                var row = new ResourceRowViewModel(StressPod(i));
                row.ApplyUsage(1_000_000L * (i % 997), 1024L * 1024 * (i % 512));
                tab.Rows.Add(row);
            }

            tab.SetSort(ResourceColumn.Cpu, descending: true, persist: false);
            return (tab.VisibleRows, () =>
            {
                // A poll that reshuffles most of a CPU-sorted list, as a real one does.
                var shuffle = new Random(7);
                foreach (var row in tab.Rows)
                {
                    row.ApplyUsage(1_000_000L * shuffle.Next(1, 4000), 1024L * 1024 * shuffle.Next(1, 512));
                }

                tab.ResortVisibleRows();
            }, "CPU-sorted metrics poll", 4);
        }));

        results.AddRange(Check("argo-resources", host, () => ClusterTabScenarios.WorkloadsList(), tab =>
        {
            var pane = new ArgoApplicationTabViewModel(null, ArgoDescriptor, ArgoCd.ReadApplication(StressArgoApplication()))
            {
                IsPreview = false,
                SelectedTabIndex = ArgoApplicationTabViewModel.ResourcesTabIndex,
            };
            return (null, () =>
            {
                tab.InspectorTabs.Add(pane);
                tab.SelectedInspectorTab = pane;
                tab.IsInspectorMaximized = true;
            }, $"open {ArgoResources:N0} managed resources", 0);
        }));

        results.AddRange(Check("yaml-diff", host, () => ClusterTabScenarios.YamlEditor(), tab =>
        {
            var yaml = (YamlEditorTabViewModel)tab.SelectedInspectorTab!;
            var preview = StressPreview();
            return (null, () =>
            {
                yaml.PendingPreview = new ApplyPreviewViewModel(preview, isForce: false);
                tab.IsInspectorMaximized = true;
            }, $"preview a {ConfigMapKeys:N0}-key ConfigMap change", 0);
        }));

        results.AddRange(Check("service-backends", host, () => NetworkingScenarios.ServiceDetail(), tab =>
        {
            var pane = (ServiceDetailTabViewModel)tab.SelectedInspectorTab!;
            return (pane.Backends, () =>
            {
                pane.ApplyPodEvent(ResourceEvent<DynamicResource>.Reset);
                for (var i = 0; i < ServicePods; i++)
                {
                    pane.ApplyPodEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Added, StressPod(i)));
                }

                pane.ApplyPodEvent(ResourceEvent<DynamicResource>.Synced);
            }, $"initial list of {ServicePods:N0} pods", 4);
        }));

        Console.WriteLine();
        Console.WriteLine($"{"check",-28} {"action",-40} {"ms",8} {"visuals",8} {"notify",8}");
        foreach (var r in results)
        {
            Console.WriteLine(
                $"{r.Name,-28} {r.Action,-40} {r.Ms,8} {r.Visuals,8} {(r.Notifications?.ToString() ?? "-"),8}{(r.Failed ? "  FAIL" : "")}");
        }

        var failed = results.Where(r => r.Failed).ToList();
        Console.WriteLine();
        Console.WriteLine(failed.Count == 0
            ? $"STRESS-OK: {results.Count} measurements within budget (visuals ≤ {VisualBudget:N0}, action ≤ {SlowMs:N0} ms, theme ≤ {ThemeMs:N0} ms)."
            : $"STRESS-FAIL: {failed.Count} of {results.Count} measurements over budget.");
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Hosts the tab, lets it settle, runs <paramref name="arrange"/>'s action with the
    /// collection it names watched, then switches the theme with the loaded data on screen.
    /// </summary>
    private static IEnumerable<Result> Measure(
        string name,
        Func<ClusterTabViewModel, Control> host,
        ClusterTabViewModel tab,
        Func<ClusterTabViewModel, (INotifyCollectionChanged? Watched, Action Act, string Label, int NotificationBudget)> arrange)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var window = (Window)host(tab);
        window.Show();
        LogSettle.Run(window);
        Settle();

        var (watched, act, label, budget) = arrange(tab);
        Settle();

        var notifications = 0;
        void Count(object? sender, NotifyCollectionChangedEventArgs e) => notifications++;
        if (watched is not null)
        {
            watched.CollectionChanged += Count;
        }

        var clock = Stopwatch.StartNew();
        act();
        Settle();
        clock.Stop();
        if (watched is not null)
        {
            watched.CollectionChanged -= Count;
        }

        var visuals = window.GetVisualDescendants().Count();
        yield return new Result(name, label, clock.ElapsedMilliseconds, visuals, watched is null ? null : notifications, budget, SlowMs);

        clock.Restart();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Settle();
        clock.Stop();
        yield return new Result(name, "theme switch", clock.ElapsedMilliseconds, visuals, null, 0, ThemeMs);

        window.Close();
        Settle();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Stamp(int i) =>
        new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static readonly ResourceDescriptor ArgoDescriptor =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", Namespaced: true, ShortNames: ["app"], Categories: []);

    private static readonly ResourceDescriptor ConfigMapDescriptor =
        new("", "v1", "ConfigMap", "configmaps", "configmap", Namespaced: true, ShortNames: ["cm"], Categories: []);

    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static DynamicResource StressPod(int i) => Parse($$"""
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "stress-pod-{{i}}", "namespace": "payments", "uid": "00000000-0000-0000-0000-{{i:D12}}",
                        "creationTimestamp": "2026-07-29T08:00:00Z", "labels": { "app": "stress" } },
          "spec": { "nodeName": "worker-{{i % 40}}", "containers": [ { "name": "app", "image": "registry.example.com/app:1.0" } ] },
          "status": { "phase": "Running", "podIP": "10.{{i / 65536 % 256}}.{{i / 256 % 256}}.{{i % 256}}",
                      "containerStatuses": [ { "name": "app", "ready": true, "restartCount": {{i % 3}}, "state": { "running": {} } } ] }
        }
        """);

    private static ClusterTabViewModel ApplicationsTab()
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.Applications.Activate();
        return tab;
    }

    private static DynamicResource StressDeployment(int i) => Parse($$"""
        {
          "apiVersion": "apps/v1", "kind": "Deployment",
          "metadata": { "name": "stress-app-{{i}}", "namespace": "team-{{i % 20}}", "uid": "d0000000-0000-0000-0000-{{i:D12}}",
                        "creationTimestamp": "2026-07-01T08:00:00Z", "generation": 1 },
          "spec": { "replicas": 2, "selector": { "matchLabels": { "app": "stress-app-{{i}}" } },
                    "template": { "metadata": { "labels": { "app": "stress-app-{{i}}" } },
                                  "spec": { "containers": [ { "name": "app", "image": "registry.example.com/app:1.0" } ] } } },
          "status": { "observedGeneration": 1, "replicas": 2, "readyReplicas": 2, "updatedReplicas": 2, "availableReplicas": 2 }
        }
        """);

    private static DynamicResource StressDaemonSet() => Parse("""
        {
          "apiVersion": "apps/v1", "kind": "DaemonSet",
          "metadata": { "name": "node-agent", "namespace": "monitoring", "uid": "da000000-0000-0000-0000-000000000001",
                        "creationTimestamp": "2026-07-01T08:00:00Z", "generation": 1 },
          "spec": { "selector": { "matchLabels": { "app": "node-agent" } },
                    "template": { "metadata": { "labels": { "app": "node-agent" } },
                                  "spec": { "containers": [ { "name": "agent", "image": "registry.example.com/agent:2.0" } ] } } },
          "status": { "observedGeneration": 1, "desiredNumberScheduled": 1000, "currentNumberScheduled": 1000,
                      "numberReady": 1000, "updatedNumberScheduled": 1000, "numberAvailable": 1000 }
        }
        """);

    private static DynamicResource StressAgentPod(int i, int restarts = 0) => Parse($$"""
        {
          "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "node-agent-{{i:D4}}", "namespace": "monitoring", "uid": "a0000000-0000-0000-0000-{{i:D12}}",
                        "creationTimestamp": "2026-07-01T08:00:00Z", "labels": { "app": "node-agent" },
                        "ownerReferences": [ { "apiVersion": "apps/v1", "kind": "DaemonSet", "name": "node-agent",
                                               "uid": "da000000-0000-0000-0000-000000000001", "controller": true } ] },
          "spec": { "nodeName": "node-{{i}}", "containers": [ { "name": "agent", "image": "registry.example.com/agent:2.0" } ] },
          "status": { "phase": "Running", "podIP": "10.1.{{i / 256 % 256}}.{{i % 256}}",
                      "containerStatuses": [ { "name": "agent", "ready": true, "restartCount": {{restarts}}, "state": { "running": {} } } ] }
        }
        """);

    private static DynamicResource StressArgoApplication()
    {
        var resources = new StringBuilder();
        for (var i = 0; i < ArgoResources; i++)
        {
            if (i > 0) resources.Append(',');
            var (group, kind) = (i % 4) switch
            {
                0 => ("apps", "Deployment"),
                1 => ("", "Service"),
                2 => ("", "ConfigMap"),
                _ => ("networking.k8s.io", "Ingress"),
            };
            var health = i % 97 == 0 ? "Degraded" : "Healthy";
            var sync = i % 53 == 0 ? "OutOfSync" : "Synced";
            resources.Append($$$"""{"group":"{{{group}}}","version":"v1","kind":"{{{kind}}}","namespace":"platform","name":"component-{{{i}}}","status":"{{{sync}}}","health":{"status":"{{{health}}}"}}""");
        }

        return Parse($$"""
            {
              "apiVersion": "argoproj.io/v1alpha1", "kind": "Application",
              "metadata": { "name": "platform-root", "namespace": "argocd" },
              "spec": { "project": "default", "source": { "repoURL": "https://git.example.com/platform.git", "path": "apps", "targetRevision": "main" },
                        "destination": { "server": "https://kubernetes.default.svc", "namespace": "platform" } },
              "status": { "sync": { "status": "Synced", "revision": "0a25554" }, "health": { "status": "Healthy" },
                          "resources": [ {{resources}} ] }
            }
            """);
    }

    private static ApplyPreview StressPreview()
    {
        string ConfigMap(string suffix)
        {
            var data = new StringBuilder();
            for (var i = 0; i < ConfigMapKeys; i++)
            {
                if (i > 0) data.Append(',');
                data.Append($"\"key-{i}\":\"value-{i}{(i % 2 == 0 ? suffix : "")}\"");
            }

            return """{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"dashboards","namespace":"monitoring"},"data":{""" + data + "}}";
        }

        using var live = JsonDocument.Parse(ConfigMap(""));
        using var previewed = JsonDocument.Parse(ConfigMap("-next"));
        var liveRoot = live.RootElement.Clone();
        var previewedRoot = previewed.RootElement.Clone();
        return new ApplyPreview(
            ResourceDiff.Between(liveRoot, previewedRoot),
            DynamicResource.FromListItem(previewedRoot, ConfigMapDescriptor),
            DynamicResource.FromListItem(liveRoot, ConfigMapDescriptor));
    }
}
