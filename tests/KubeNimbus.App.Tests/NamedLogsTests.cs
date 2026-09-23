using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>L3: every nested row resolves a name through the tab's one logs entry point.</summary>
[NotInParallel]
public class NamedLogsTests
{
    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    [Test]
    public async Task Workload_pod_row_opens_shared_logs_pane()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "apps", Kind: "Deployment" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.First(r => r.Name == "payment-service-report-generator");
        await tab.OpenSelectedCommand.ExecuteAsync(null);
        var detail = (WorkloadDetailTabViewModel)tab.SelectedInspectorTab!;
        var pod = detail.Pods.First();
        detail.SelectedPod = pod;

        await detail.OpenPodLogsCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(((PodDetailTabViewModel)tab.SelectedInspectorTab!).PodName).IsEqualTo(pod.Name);
        await Assert.That(((PodDetailTabViewModel)tab.SelectedInspectorTab!).SelectedDetailTabIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Node_pod_row_opens_shared_logs_pane()
    {
        var tab = DemoTab();
        var kind = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor is { Group: "", Kind: "Node" });
        tab.SelectKindCommand.Execute(kind);
        tab.SelectedRow = tab.Rows.First(r => r.Name == "demo-worker-1");
        await tab.OpenSelectedCommand.ExecuteAsync(null);
        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;
        var pod = detail.Pods.First();
        detail.SelectedPod = pod;

        await detail.OpenPodLogsCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(((PodDetailTabViewModel)tab.SelectedInspectorTab!).PodName).IsEqualTo(pod.Name);
    }

    [Test]
    public async Task Event_about_pod_has_logs_and_opens_shared_pane()
    {
        var tab = DemoTab();
        var pod = DemoData.Pods.First();
        using var json = JsonDocument.Parse($$$"""
            {"apiVersion":"v1","kind":"Event","metadata":{"name":"pod-event","namespace":"{{{pod.Namespace}}}"},
             "involvedObject":{"kind":"Pod","name":"{{{pod.Name}}}","namespace":"{{{pod.Namespace}}}"}}
            """);
        var row = new ResourceRowViewModel(new DynamicResource(json.RootElement.Clone()));
        tab.SelectedRow = row;

        await Assert.That(row.HasLogs).IsTrue();
        await Assert.That(tab.CanOpenDirectLogsForSelectedRow).IsTrue();
        await tab.OpenLogsCommand.ExecuteAsync(null);

        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<PodDetailTabViewModel>();
        await Assert.That(((PodDetailTabViewModel)tab.SelectedInspectorTab!).PodName).IsEqualTo(pod.Name);
    }

    [Test]
    public async Task Argo_workload_resource_uses_shared_logs_and_gone_pod_is_stated()
    {
        var tab = DemoTab();
        var application = DemoData.ArgoApplications.First(a => a.Name == "fraud-detector");
        var descriptor = DemoData.BuildCatalog().First(d => d is { Group: "argoproj.io", Kind: "Application" });
        var detail = new ArgoApplicationTabViewModel(null, descriptor, application,
            openLogs: (owner, ns, token) => tab.OpenNamedLogsAsync(owner, ns, cancellationToken: token));
        detail.SelectedResource = detail.Resources.First(r => r.Name == "fraud-detector" && r.Kind == "Deployment");

        await Assert.That(detail.SelectedResource.HasLogs).IsTrue();
        await detail.OpenSelectedResourceLogsCommand.ExecuteAsync(null);
        await Assert.That(tab.SelectedInspectorTab).IsTypeOf<WorkloadLogsTabViewModel>();

        await tab.OpenNamedLogsAsync(new OwnerRef("v1", "Pod", "deleted-pod", null, false), "payments");
        await Assert.That(tab.ConnectionWarning).Contains("no longer available");

        var existingPod = DemoData.Pods.First();
        await tab.OpenNamedLogsAsync(new OwnerRef("v1", "Pod", existingPod.Name, "old-uid", false), existingPod.Namespace);
        await Assert.That(tab.ConnectionWarning).Contains("was replaced");
    }

    [Test]
    public async Task Argo_resource_logs_are_not_limited_to_builtin_workload_kinds()
    {
        var rollout = new ArgoResource("argoproj.io", "v1alpha1", "Rollout", "payments", "web",
            ArgoSyncState.Synced, ArgoHealthState.Healthy);
        ArgoResource? opened = null;
        var row = new ArgoResourceRowViewModel(rollout, _ => Task.CompletedTask,
            resource => { opened = resource; return Task.CompletedTask; });

        await Assert.That(row.HasLogs).IsTrue();
        await row.OpenLogsCommand.ExecuteAsync(null);
        await Assert.That(opened).IsEqualTo(rollout);
    }

    [Test]
    public async Task Closing_workload_detail_cancels_named_log_resolution()
    {
        var deployment = DemoData.Deployments.First(d => d.Name == "payment-service-report-generator");
        var descriptor = DemoData.BuildCatalog().First(d => d is { Group: "apps", Kind: "Deployment" });
        var detail = new WorkloadDetailTabViewModel(null, descriptor, new ResourceRowViewModel(deployment),
            _ => { }, _ => Task.CompletedTask, (_, _) => Task.CompletedTask,
            openLogs: (_, _, token) => Task.Delay(Timeout.Infinite, token));
        detail.SelectedPod = detail.Pods.First();

        var resolution = detail.OpenPodLogsAsync(detail.SelectedPod);
        await detail.OnClosingAsync();

        await Assert.That(resolution.IsCanceled).IsTrue();
    }

    [Test]
    public async Task Closing_node_detail_cancels_named_log_resolution()
    {
        var node = new ResourceRowViewModel(DemoData.Nodes.First());
        var detail = new NodeDetailTabViewModel(null, node,
            openLogs: (_, _, token) => Task.Delay(Timeout.Infinite, token));
        detail.SelectedPod = detail.Pods.First();

        var resolution = detail.OpenPodLogsAsync(detail.SelectedPod);
        await detail.OnClosingAsync();

        await Assert.That(resolution.IsCanceled).IsTrue();
    }

    [Test]
    public async Task Closing_argo_detail_cancels_named_log_resolution()
    {
        var application = DemoData.ArgoApplications.First(a => a.Name == "fraud-detector");
        var descriptor = DemoData.BuildCatalog().First(d => d is { Group: "argoproj.io", Kind: "Application" });
        var detail = new ArgoApplicationTabViewModel(null, descriptor, application,
            openLogs: (_, _, token) => Task.Delay(Timeout.Infinite, token));
        detail.SelectedResource = detail.Resources.First(r => r.Kind == "Deployment");

        var resolution = detail.OpenSelectedResourceLogsCommand.ExecuteAsync(null);
        await detail.OnClosingAsync();

        await Assert.That(resolution.IsCanceled).IsTrue();
    }
}
