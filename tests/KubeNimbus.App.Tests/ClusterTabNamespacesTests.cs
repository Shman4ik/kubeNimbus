using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Several namespaces in the Resources list: the selection model (the first is still
/// <see cref="ClusterTabViewModel.SelectedNamespace"/>), the picker's two gestures, the
/// merged watch's namespace-scoped Reset and its loading verdict (UI rule 18), the demo
/// cluster's rows, and the workspace round trip.
/// </summary>
public class ClusterTabNamespacesTests
{
    private static ClusterTabViewModel TabWith(params string[] namespaces)
    {
        var tab = TestObjects.Tab();
        foreach (var ns in namespaces)
        {
            tab.NamespaceOptions.Add(ns);
        }

        return tab;
    }

    private static NamespaceChoice Row(ClusterTabViewModel tab, string name) =>
        tab.FilteredNamespaces.Single(c => c.Name == name);

    [Test]
    public async Task Several_namespaces_keep_the_first_as_the_selected_one()
    {
        var tab = TabWith("payments", "checkout", "ledger");

        tab.SetNamespaces(["payments", "checkout"]);

        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["checkout", "payments"]);
        await Assert.That(tab.SelectedNamespace).IsEqualTo("checkout");
        await Assert.That(tab.NamespaceDisplay).IsEqualTo("checkout, payments");

        tab.SetNamespaces(["payments", "checkout", "ledger"]);
        await Assert.That(tab.NamespaceDisplay).IsEqualTo("3 namespaces");
        await Assert.That(tab.NamespaceButtonTip).Contains("checkout, ledger, payments");

        // Anything that sets the one namespace — the palette, a reveal, a restore — sets it alone.
        tab.SelectedNamespace = "ledger";
        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["ledger"]);

        tab.SetNamespaces([]);
        await Assert.That(tab.SelectedNamespace).IsEqualTo(ClusterTabViewModel.AllNamespaces);
        await Assert.That(tab.NamespaceDisplay).IsEqualTo(ClusterTabViewModel.AllNamespaces);
    }

    /// <summary>
    /// The box adds or removes and keeps the rows where they are; a click chooses one alone.
    /// The rows' checks follow in place, and the All namespaces row clears the choice.
    /// </summary>
    [Test]
    public async Task The_box_adds_a_namespace_and_a_click_chooses_one_alone()
    {
        var tab = TabWith("payments", "checkout", "ledger");
        tab.RebuildNamespaceChoices();
        var payments = Row(tab, "payments");

        tab.ToggleNamespace(payments);
        tab.ToggleNamespace(Row(tab, "ledger"));
        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["ledger", "payments"]);
        await Assert.That(Row(tab, "payments")).IsSameReferenceAs(payments);
        await Assert.That(payments.IsChecked).IsTrue();
        await Assert.That(Row(tab, ClusterTabViewModel.AllNamespaces).IsChecked).IsFalse();

        tab.ToggleNamespace(payments);
        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["ledger"]);
        await Assert.That(payments.IsChecked).IsFalse();

        tab.ChooseNamespace(Row(tab, "checkout"));
        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["checkout"]);

        tab.ToggleNamespace(Row(tab, "payments"));
        tab.ToggleNamespace(Row(tab, ClusterTabViewModel.AllNamespaces));
        await Assert.That(tab.SelectedNamespaces.Count).IsEqualTo(0);
        await Assert.That(Row(tab, ClusterTabViewModel.AllNamespaces).IsChecked).IsTrue();
    }

    /// <summary>
    /// One namespace relisting (a 410) clears only its own rows, and the list says it is
    /// loading — never "no pods" — until every namespace has answered or a row arrived.
    /// </summary>
    [Test]
    public async Task A_namespace_reset_keeps_the_others_rows_and_the_verdict_waits_for_every_namespace()
    {
        var tab = TestObjects.Tab();
        tab.IsListLoading = true;

        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Reset));
        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Reset));
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Synced));
        await Assert.That(tab.IsListLoading).IsTrue();
        await Assert.That(tab.IsListEmpty).IsFalse();

        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Synced));
        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsTrue();

        tab.ApplyNamespaced(new("a", TestObjects.Added(TestObjects.Pod("a", "web"))));
        tab.ApplyNamespaced(new("b", TestObjects.Added(TestObjects.Pod("b", "api"))));
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Reset));

        await Assert.That(tab.Rows.Select(r => r.Name)).IsEquivalentTo(["api"]);
        await Assert.That(tab.IsListLoading).IsFalse();
    }

    [Test]
    public async Task The_demo_list_shows_every_chosen_namespace()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SetNamespaces([]);
        var namespaces = tab.Rows.Select(r => r.Namespace).Distinct().Order().Take(2).ToList();
        await Assert.That(namespaces.Count).IsEqualTo(2);

        tab.SetNamespaces(namespaces);

        await Assert.That(tab.Rows.Select(r => r.Namespace).Distinct().Order()).IsEquivalentTo(namespaces);
        await Assert.That(tab.Rows.Count).IsGreaterThan(0);
    }

    [Test]
    [NotInParallel]
    public async Task Several_namespaces_survive_a_restart()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo) { RestoreNamespaces = ["payments", "monitoring"], RestoreNamespace = "monitoring" };
        tab.ConnectCommand.Execute(null);

        await Assert.That(tab.SelectedNamespaces).IsEquivalentTo(["monitoring", "payments"]);
    }
}
