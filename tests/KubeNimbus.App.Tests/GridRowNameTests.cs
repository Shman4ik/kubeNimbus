using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #236: a grid row is named from its item's <c>ToString()</c> (accessible-names rule 11), and
/// the node detail's pods, the Helm releases, the Argo CD Applications, an Application's
/// resources and an ApplicationSet's generated Applications had none, so UI Automation read
/// every one of their rows as an empty <c>DataItem</c>. Each now says what it is and where,
/// identity only, as <see cref="ResourceRowViewModel"/> does.
/// </summary>
public class GridRowNameTests
{
    [Test]
    public async Task A_node_detail_pod_row_is_named_by_pod_and_namespace()
    {
        var row = new NodePodViewModel(TestObjects.Pod("payments", "api-1"));

        await Assert.That(row.ToString()).IsEqualTo("api-1, payments");
    }

    [Test]
    public async Task A_helm_release_row_is_named_by_release_and_namespace()
    {
        var row = new HelmReleaseRowViewModel(
            new HelmRelease("checkout", "payments", 7, "deployed", "checkout", "1.4.2", "2.14.3", DateTimeOffset.UnixEpoch, "Upgrade complete"));

        await Assert.That(row.ToString()).IsEqualTo("checkout, payments");
    }

    [Test]
    public async Task An_argo_application_row_names_its_cluster_in_a_fleet_list_only()
    {
        var app = Demo.DemoData.ArgoApplications.First(a => a.Name == "checkout");
        var descriptor = new ResourceDescriptor("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []);

        await Assert.That(new ArgoApplicationRowViewModel(app, descriptor).ToString()).IsEqualTo($"checkout, {app.Namespace}");
        await Assert.That(new ArgoApplicationRowViewModel(app, descriptor, "eu").ToString()).IsEqualTo($"checkout, {app.Namespace}, eu");
    }

    [Test]
    public async Task An_argo_resource_row_names_its_kind_and_a_cluster_scoped_one_has_no_namespace()
    {
        var web = new ArgoResourceRowViewModel(
            new ArgoResource("apps", "v1", "Deployment", "payments", "web", ArgoSyncState.Synced, ArgoHealthState.Healthy),
            _ => Task.CompletedTask);
        var role = new ArgoResourceRowViewModel(
            new ArgoResource("rbac.authorization.k8s.io", "v1", "ClusterRole", "", "reader", ArgoSyncState.Synced, ArgoHealthState.Healthy),
            _ => Task.CompletedTask);

        await Assert.That(web.ToString()).IsEqualTo("Deployment web, payments");
        await Assert.That(role.ToString()).IsEqualTo("ClusterRole reader");
    }

    [Test]
    public async Task A_generated_application_row_is_named_by_application_and_namespace()
    {
        var row = new ArgoGeneratedApplicationRowViewModel(
            "checkout-eu", "argocd", ArgoSyncState.Synced, ArgoHealthState.Healthy, _ => Task.CompletedTask);

        await Assert.That(row.ToString()).IsEqualTo("checkout-eu, argocd");
    }
}
