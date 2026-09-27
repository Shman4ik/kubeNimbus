using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-14: the demo cluster's list shows what the demo is meant to show. It is the first
/// surface a Microsoft Store reviewer sees, and its failure mode is quiet — a demo that
/// renders less than it should still renders — so this pins the story, not just a count:
/// every pod the dataset ships in the namespace the tab opens on, a crash loop among them,
/// usage on the running ones, three nodes of which one is cordoned, a CRD wearing its own
/// columns, and a kind with nothing behind it landing on the real empty state.
///
/// <para>
/// Through the real <c>ConnectCommand</c> and <c>SelectKindCommand</c>, i.e. through
/// <c>PopulateDemoRows</c> exactly as a click reaches it. ENG-18 (add nodes to the demo)
/// is covered here too: the dataset has carried them since FEAT-4, and the Nodes test
/// below is what keeps it that way.
/// </para>
/// </summary>
public class DemoRowsTests
{
    private static ClusterTabViewModel DemoTab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        return tab;
    }

    private static void Select(ClusterTabViewModel tab, string group, string kind) =>
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .Where(s => s.Title != SidebarGrouping.RecentSection)
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind));

    private static string Names(IEnumerable<ResourceRowViewModel> rows) =>
        string.Join(", ", rows.Select(r => r.Name).Order(StringComparer.Ordinal));

    [Test]
    public async Task The_demo_opens_on_every_payments_pod_with_a_crash_loop_among_them()
    {
        var tab = DemoTab();

        var expected = DemoData.Pods.Where(p => p.Namespace == "payments").ToList();
        await Assert.That(tab.SelectedNamespace).IsEqualTo("payments");
        await Assert.That(expected.Count).IsGreaterThanOrEqualTo(8);
        await Assert.That(Names(tab.Rows)).IsEqualTo(string.Join(", ", expected.Select(p => p.Name).Order(StringComparer.Ordinal)));

        // The two things the demo exists to show about a list: something broken to find,
        // and a settled list rather than a spinner or an empty-namespace verdict.
        await Assert.That(tab.Rows.Any(r => r.Status == "CrashLoopBackOff" && r.IsUnhealthy)).IsTrue();
        await Assert.That(tab.Rows.Any(r => r.Status == "Running" && !r.IsUnhealthy)).IsTrue();
        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(tab.Rows.Count);
    }

    /// <summary>
    /// Replayed usage, not gaps: a CPU/Memory column of nothing but dashes on the demo reads
    /// as a broken metrics-server rather than as a demo.
    /// </summary>
    [Test]
    public async Task Every_running_demo_pod_carries_usage()
    {
        var tab = DemoTab();
        tab.SelectedNamespace = ClusterTabViewModel.AllNamespaces;

        await Assert.That(tab.AreMetricsVisible).IsTrue();
        var running = tab.Rows.Where(r => r.Status == "Running").ToList();
        await Assert.That(running).IsNotEmpty();
        await Assert.That(running.All(r => r.CpuText.Length > 0 && r.CpuText != "—")).IsTrue();
        await Assert.That(running.All(r => r.MemoryText.Length > 0 && r.MemoryText != "—")).IsTrue();
    }

    [Test]
    public async Task All_namespaces_shows_every_demo_pod()
    {
        var tab = DemoTab();
        tab.SelectedNamespace = ClusterTabViewModel.AllNamespaces;

        await Assert.That(tab.Rows.Count).IsEqualTo(DemoData.Pods.Count);
        await Assert.That(tab.Rows.Select(r => r.Namespace).Distinct().Count()).IsGreaterThan(1);
    }

    [Test]
    public async Task The_demo_has_three_nodes_and_one_of_them_is_cordoned()
    {
        var tab = DemoTab();
        Select(tab, "", "Node");

        await Assert.That(Names(tab.Rows)).IsEqualTo("demo-cp-1, demo-worker-1, demo-worker-2");
        await Assert.That(tab.Rows.Single(r => r.Name == "demo-worker-2").Status).Contains("SchedulingDisabled");
        await Assert.That(tab.Rows.All(r => r.CpuText.Length > 0 && r.CpuText != "—")).IsTrue();
    }

    [Test]
    public async Task The_demo_deployments_are_listed_in_their_namespace()
    {
        var tab = DemoTab();
        Select(tab, "apps", "Deployment");

        var expected = DemoData.Deployments.Where(d => d.Namespace == "payments").Select(d => d.Name);
        await Assert.That(tab.Rows.Count).IsGreaterThan(0);
        await Assert.That(Names(tab.Rows)).IsEqualTo(string.Join(", ", expected.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The one CRD in the dataset renders the columns its own definition declares — the
    /// demo's evidence that CRDs are first-class, read through the same parser a live
    /// cluster's CRD goes through.
    /// </summary>
    [Test]
    public async Task Demo_certificates_wear_their_crds_own_columns()
    {
        var tab = DemoTab();
        Select(tab, "cert-manager.io", "Certificate");

        await Assert.That(tab.Rows.Count).IsGreaterThan(0);
        await Assert.That(tab.PrinterColumns[0].Name).IsEqualTo("Ready");

        // Both answers the condition filter can give, and — reporting-tls, which has no
        // status yet — the blank a path that resolves to nothing renders as.
        string Ready(string name) => tab.Rows.Single(r => r.Name == name).PrinterCells[0].Text;
        await Assert.That(Ready("checkout-tls")).IsEqualTo("True");
        await Assert.That(Ready("ledger-webhook-tls")).IsEqualTo("False");
        await Assert.That(Ready("reporting-tls")).IsEqualTo("");
    }

    /// <summary>Most of a 100-kind catalog has nothing behind it, and that has to read as an empty namespace.</summary>
    [Test]
    public async Task A_kind_the_dataset_has_nothing_for_lands_on_the_empty_state()
    {
        var tab = DemoTab();
        Select(tab, "", "LimitRange");

        await Assert.That(tab.Rows).IsEmpty();
        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsTrue();
    }
}
