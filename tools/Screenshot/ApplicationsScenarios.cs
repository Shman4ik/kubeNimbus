using Avalonia.Threading;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The Applications mode's scenarios. Every one of them runs the demo cluster through the
/// real <c>ConnectCommand</c> and the real <see cref="ApplicationsViewModel.Activate"/>, so
/// the rows, the verdicts and the page are what the shipping app computes from the dataset
/// (demo rule 4) — only the states a demo cluster cannot produce (still loading, refused by
/// RBAC) are written in through the view model's fixture seams.
/// </summary>
public static class ApplicationsScenarios
{
    private static ClusterTabViewModel DemoTab()
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.Applications.Activate();
        return tab;
    }

    public static ClusterTabViewModel List(
        ApplicationChip chip = ApplicationChip.All, string filter = "", bool showSystem = false)
    {
        var tab = DemoTab();
        var apps = tab.Applications;
        apps.Chip = chip;
        apps.Filter = filter;
        apps.ShowSystemNamespaces = showSystem;
        apps.SelectedRow = apps.VisibleRows.FirstOrDefault();
        return tab;
    }

    /// <summary>
    /// A real cluster mid-connect: the reads have started and nothing has answered. The
    /// state UI rule 18 is about — it names what it waits for and never says "no applications".
    /// </summary>
    public static ClusterTabViewModel Loading()
    {
        var context = new ClusterContext("prod-payments", "payments-prod-euw1", "payments", "fixture-user", "/home/fixture/.kube/config");
        var tab = new ClusterTabViewModel(context) { IsConnected = true, Status = "Connected — Kubernetes v1.31.2." };
        tab.Applications.MarkPending("Deployment");
        tab.Applications.MarkPending("Pod");
        tab.Applications.MarkPending("Application");
        tab.Applications.ScopeText = "All namespaces";
        return tab;
    }

    /// <summary>Narrow RBAC: cluster-wide lists refused, per-namespace fallback, one namespace refused too.</summary>
    public static ClusterTabViewModel RbacFallback()
    {
        var tab = DemoTab();
        tab.Applications.SetFallbackForFixture(
            [("Pod", "pods"), ("Deployment", "deployments"), ("StatefulSet", "statefulsets")],
            ["payments", "monitoring", "team-risk"],
            [("Pod", "team-risk", "pods is forbidden: User \"oidc:ana@example.com\" cannot list resource \"pods\" in API group \"\" in the namespace \"team-risk\"")]);
        tab.Applications.Filter = "";
        return tab;
    }

    public static ClusterTabViewModel Page(
        string name, bool merged = false, bool editYaml = false, bool restart = false)
    {
        var tab = DemoTab();
        var apps = tab.Applications;
        var row = apps.Rows.First(r => r.Name == name);
        apps.Open(row);
        var page = apps.Page!;
        if (merged)
        {
            page.SelectedPod = page.Pods[0];
        }

        if (editYaml)
        {
            page.EditYamlCommand.Execute(null);
        }

        if (restart)
        {
            page.RestartCommand.Execute(null);
        }

        DrainLogs(page);
        return tab;
    }

    /// <summary>
    /// Demo logs arrive on a timer; the headless harness does not run in real time, so the
    /// dispatcher is pumped until the replayed stream has landed.
    /// </summary>
    private static void DrainLogs(ApplicationPageViewModel page)
    {
        for (var i = 0; i < 600; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (page.Logs is not { } logs || logs.Sources.All(s => s.State is LogSourceState.Ended or LogSourceState.Failed))
            {
                break;
            }

            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
