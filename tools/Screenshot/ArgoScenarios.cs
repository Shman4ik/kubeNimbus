using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Bundle K's Argo CD states (#193, #194, #195), on the demo cluster through the production
/// commands, in their own file so the shared scenario file stays out of the diff.
/// </summary>
internal static class ArgoScenarios
{
    private static ClusterTabViewModel DemoTab(Func<SidebarKindViewModel, bool> kind)
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(kind));
        return tab;
    }

    private static ClusterTabViewModel Opened(string kind, string name)
    {
        var tab = DemoTab(k => k.Descriptor is { Group: ArgoCd.Group } d && d.Kind == kind);
        tab.SelectedNamespace = "argocd";
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);
        tab.IsInspectorMaximized = true;
        return tab;
    }

    /// <summary>
    /// The ApplicationSet pane on its Applications tab: the five Applications the git
    /// directories generator stamped out, worst first, each with its own two pills.
    /// </summary>
    public static ClusterTabViewModel ApplicationSetDetail(int tabIndex = ArgoApplicationSetTabViewModel.ApplicationsTabIndex)
    {
        var tab = Opened("ApplicationSet", "payments-services");
        if (tab.SelectedInspectorTab is ArgoApplicationSetTabViewModel set)
        {
            set.SelectedTabIndex = tabIndex;
        }

        return tab;
    }

    /// <summary>
    /// The AppProject pane: allowed repositories (one a deny pattern), destinations, kinds with
    /// the empty-list rule stated, two sync windows saying what they do to a manual sync, roles.
    /// </summary>
    public static ClusterTabViewModel ProjectDetail() => Opened("AppProject", "payments");

    /// <summary>
    /// Terminate sync armed on the demo's one Application with a sync in flight: it asks
    /// first, says what stays applied, and the demo refuses in place (demo rule 5).
    /// </summary>
    public static ClusterTabViewModel Terminate()
    {
        var tab = DemoTab(k => k.IsArgoDashboard);
        tab.SelectedArgoApplication = tab.ArgoApplications.First(a => a.Name == "notification-dispatcher");
        tab.TerminateArgoSyncCommand.Execute(null);
        return tab;
    }

    /// <summary>
    /// The dashboard's fleet list (#195): one merged list with a Cluster column, the seven
    /// counts summed across the clusters, and "n of m clusters run Argo CD" in the header.
    /// Rows are written in, as the resource list's fleet scenario does: the real read needs
    /// several live clusters. IsFleetView goes first because it re-reads the dashboard.
    /// </summary>
    public static ClusterTabViewModel Fleet()
    {
        var tab = DemoTab(k => k.IsArgoDashboard);
        tab.IsFleetViewAvailable = true;
        tab.IsFleetView = true;

        var descriptor = tab.ArgoApplications[0].Descriptor;
        var demo = KubeNimbus.App.Demo.DemoData.ArgoApplications;
        var fleet = new List<ArgoFleetApplication>();
        foreach (var (cluster, take) in new[] { ("prod-eu", demo.Count), ("prod-us", 4), ("staging", 3) })
        {
            fleet.AddRange(demo.Take(take).Select(a => new ArgoFleetApplication(cluster, descriptor, a)));
        }

        var read = new ArgoFleetRead(fleet, Serving: 3, Members: 4, Failed: []);
        tab.ArgoApplications.Clear();
        foreach (var row in fleet
            .Select(a => new ArgoApplicationRowViewModel(a.Application, a.Descriptor, a.ClusterName))
            .Order(tab.ArgoSort.Comparer))
        {
            tab.ArgoApplications.Add(row);
        }

        tab.ArgoCounts = read.Counts;
        tab.FleetSummary = read.Summary;
        tab.SelectedArgoApplication = tab.ArgoApplications[0];
        return tab;
    }
}
