using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The harness's view of the fixture data. Everything here is now a passthrough to
/// <see cref="DemoData"/> in the app itself: the shipping demo cluster and these
/// scenarios read the same objects, so a screenshot cannot drift away from what a
/// user clicking "Explore demo cluster" actually sees. The dataset lives in the app
/// because it ships with it — this is the borrower, not the owner.
///
/// The one thing that stays here is <see cref="CreateOfflineClient"/>, which needs a
/// kubeconfig *file on disk* and is a harness-only affordance: the shipping demo path
/// has no <c>ClusterClient</c> at all (see <c>ClusterTabViewModel.IsDemo</c>).
/// </summary>
internal static class FixtureData
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IReadOnlyList<DynamicResource> Pods => DemoData.Pods;

    public static IReadOnlyList<DynamicResource> Deployments => DemoData.Deployments;

    public static IReadOnlyList<DynamicResource> Events => DemoData.Events;

    public static IReadOnlyList<DynamicResource> PodMetrics => DemoData.PodMetrics;

    public static DynamicResource Secret => DemoData.Secret;

    public static string[] Namespaces => DemoData.Namespaces;

    public static IReadOnlyList<HelmRelease> HelmReleases => DemoData.HelmReleases;

    public static IReadOnlyList<ResourceDescriptor> BuildCatalog() => DemoData.BuildCatalog();

    public static SidebarSectionViewModel[] BuildSidebarSections(IReadOnlyList<ResourceDescriptor> catalog) =>
        DemoData.BuildSidebarSections(catalog);

    /// <summary>
    /// Offline ClusterClient: points at an unreachable local port so construction
    /// never touches the network, but real objects still exist to satisfy
    /// ViewModel constructors that expect a live ClusterClient. Background calls
    /// that do fire (event refresh, exec connect) fail fast and are swallowed by
    /// the same error handling the app already has for a lost connection.
    ///
    /// Scenarios that want the *demo* behaviour instead pass no client at all —
    /// see <c>ClusterTabScenarios.DemoList</c>.
    /// </summary>
    public static ClusterClient CreateOfflineClient()
    {
        var kubeconfigPath = Path.Combine(FixturesDir, "kubeconfig-fake.yaml");
        var context = new ClusterContext("fixture-cluster", "fake-cluster", "payments", "fake-user", kubeconfigPath);
        return ClusterClient.Connect(context);
    }
}
