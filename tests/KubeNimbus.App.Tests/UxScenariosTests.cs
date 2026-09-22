using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

[NotInParallel]
public class UxScenariosTests
{
    [Test]
    public async Task Early_pods_survive_catalog_replacement_and_keep_the_chosen_namespace()
    {
        var tab = TestObjects.Tab();
        tab.NamespaceOptions.Add("default");
        tab.NamespaceOptions.Add("payments");
        await Assert.That(tab.StartInitialPods()).IsTrue();
        tab.SelectedNamespace = "payments";
        var row = new ResourceRowViewModel(TestObjects.Pod("payments", "web"));
        tab.Rows.Add(row);
        tab.SelectedRow = row;
        var discovered = new SidebarKindViewModel(ResourceDescriptor.Pods with { Subresources = ["exec"] }, "workload");
        tab.SidebarSections.Clear();
        var section = new SidebarSectionViewModel("Workloads");
        section.Kinds.Add(discovered);
        tab.SidebarSections.Add(section);
        tab.ReconcileInitialPods();
        await Assert.That(ReferenceEquals(tab.SelectedKind, discovered)).IsTrue();
        await Assert.That(ReferenceEquals(tab.Rows.Single(), row)).IsTrue();
        await Assert.That(tab.SelectedNamespace).IsEqualTo("payments");
    }

    [Test]
    public async Task Restored_non_pod_kind_waits_for_discovery()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(TestObjects.Context) { RestoreKindKey = "apps/Deployment" };
        await Assert.That(tab.StartInitialPods()).IsFalse();
        await Assert.That(tab.SelectedKind).IsNull();
    }

    [Test]
    public async Task Namespace_filter_does_not_select_and_recents_are_bounded_and_context_scoped()
    {
        var tab = TestObjects.Tab();
        for (var i = 0; i < 300; i++) tab.NamespaceOptions.Add($"team-{i:D3}");
        for (var i = 0; i < 7; i++) tab.SelectedNamespace = $"team-{i:D3}";
        tab.NamespaceFilter = "TEAM-29";
        await Assert.That(tab.FilteredNamespaces.Count).IsEqualTo(10);
        await Assert.That(tab.SelectedNamespace).IsEqualTo("team-006");
        tab.NamespaceFilter = "no-such-namespace";
        await Assert.That(tab.NoNamespaceMatches).IsTrue();
        tab.NamespaceFilter = "";
        await Assert.That(tab.FilteredNamespaces.Count(n => n.IsRecent)).IsEqualTo(5);
        await Assert.That(tab.FilteredNamespaces[1].Name).IsEqualTo("team-006");
        var reopened = new ClusterTabViewModel(TestObjects.Context);
        reopened.NamespaceOptions.Add("team-006");
        await Assert.That(reopened.FilteredNamespaces.Single(n => n.Name == "team-006").IsRecent).IsTrue();
        var other = new ClusterTabViewModel(TestObjects.Context with { Name = "another" });
        other.NamespaceOptions.Add("team-006");
        await Assert.That(other.FilteredNamespaces.Any(n => n.IsRecent)).IsFalse();
    }

    [Test]
    public async Task Workload_detail_tracks_conditions_pods_and_action_target()
    {
        TestObjects.RedirectStores();
        using var doc = JsonDocument.Parse("""
            {"kind":"Deployment","apiVersion":"apps/v1","metadata":{"name":"web","namespace":"payments","generation":4},
            "spec":{"replicas":3,"selector":{"matchLabels":{"app":"not-in-demo"}},"template":{"spec":{"containers":[]}}},
            "status":{"observedGeneration":3,"readyReplicas":1,"updatedReplicas":2,
            "conditions":[{"type":"Available","status":"False","reason":"MinimumReplicasUnavailable","message":"Waiting for pods"}]}}
            """);
        var descriptor = new ResourceDescriptor("apps", "v1", "Deployment", "deployments", "deployment", true, [], [])
            { Subresources = ["scale"] };
        RowActionKind? armed = null;
        var row = new ResourceRowViewModel(new DynamicResource(doc.RootElement.Clone()), "cluster-b");
        var detail = new WorkloadDetailTabViewModel(null, descriptor, row, _ => { },
            kind => { armed = kind; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        await Assert.That(detail.Rollout).Contains("1/3 ready");
        await Assert.That(detail.Rollout).Contains("waiting for controller");
        await Assert.That(detail.Conditions.Single().Status).IsEqualTo("False");
        detail.ApplyPod(new(ResourceEventType.Added, TestObjects.Pod("payments", "web-1")));
        detail.ApplyPod(new(ResourceEventType.Modified, TestObjects.Pod("payments", "web-1", restarts: 9)));
        await Assert.That(detail.Pods.Single().ClusterName).IsEqualTo("cluster-b");
        await Assert.That(detail.Pods.Single().RestartsText).Contains("9");
        await detail.ScaleCommand.ExecuteAsync(null);
        await Assert.That(armed).IsEqualTo(RowActionKind.Scale);
        detail.ApplyPod(new(ResourceEventType.Deleted, TestObjects.Pod("payments", "web-1")));
        await Assert.That(detail.Pods.Count).IsEqualTo(0);
        await Assert.That(WorkloadDetailTabViewModel.Supports(descriptor with { Group = "example.io" })).IsFalse();
        await detail.OnClosingAsync();
    }
}
