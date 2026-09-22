using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Where a freshly connected tab lands (<see cref="ClusterTabViewModel.ApplyInitialView"/>).
/// Before this, every tab opened on Pods in all namespaces whatever it had been left on,
/// so each launch began with the same two or three clicks on every tab — and a context
/// whose kubeconfig names a namespace opened somewhere kubectl would not.
/// </summary>
public class ClusterTabInitialViewTests
{
    private static ClusterTabViewModel Tab(
        string? restoreKind = null, string? restoreNamespace = null, params string[] namespaces)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(TestObjects.Context)
        {
            RestoreKindKey = restoreKind,
            RestoreNamespace = restoreNamespace,
        };

        var section = new SidebarSectionViewModel("Workloads");
        section.Kinds.Add(new SidebarKindViewModel(TestObjects.PodDescriptor, "workload"));
        section.Kinds.Add(new SidebarKindViewModel(TestObjects.ConfigMapDescriptor, "config"));
        tab.SidebarSections.Add(section);

        foreach (var ns in namespaces)
        {
            tab.NamespaceOptions.Add(ns);
        }

        return tab;
    }

    [Test]
    public async Task The_saved_kind_and_namespace_come_back()
    {
        var tab = Tab("/ConfigMap", "payments", "default", "payments");

        tab.ApplyInitialView(fallbackNamespace: "default");

        await Assert.That(tab.SelectedKind!.Descriptor.Kind).IsEqualTo("ConfigMap");
        await Assert.That(tab.SelectedNamespace).IsEqualTo("payments");
    }

    [Test]
    public async Task All_namespaces_is_a_saved_choice_too_and_beats_the_contexts_own()
    {
        var tab = Tab(null, ClusterTabViewModel.AllNamespaces, "default", "payments");

        tab.ApplyInitialView(fallbackNamespace: "default");

        await Assert.That(tab.SelectedNamespace).IsEqualTo(ClusterTabViewModel.AllNamespaces);
    }

    [Test]
    public async Task With_nothing_saved_the_contexts_own_namespace_and_pods_are_used()
    {
        var tab = Tab(null, null, "default", "payments");

        tab.ApplyInitialView(fallbackNamespace: "payments");

        await Assert.That(tab.SelectedKind!.Descriptor.Kind).IsEqualTo("Pod");
        await Assert.That(tab.SelectedNamespace).IsEqualTo("payments");
    }

    [Test]
    public async Task A_kind_the_cluster_no_longer_serves_falls_back_to_pods()
    {
        var tab = Tab("cert-manager.io/Certificate", null, "default");

        tab.ApplyInitialView(fallbackNamespace: null);

        await Assert.That(tab.SelectedKind!.Descriptor.Kind).IsEqualTo("Pod");
    }

    [Test]
    public async Task A_namespace_missing_from_a_list_that_was_read_is_not_opened()
    {
        // It has been deleted since: opening on it would be an empty list that looks
        // like a broken watch.
        var tab = Tab(null, "gone", "default", "payments");

        tab.ApplyInitialView(fallbackNamespace: null);

        await Assert.That(tab.SelectedNamespace).IsEqualTo(ClusterTabViewModel.AllNamespaces);
        await Assert.That(tab.NamespaceOptions).DoesNotContain("gone");
    }

    [Test]
    public async Task When_listing_namespaces_was_refused_the_contexts_namespace_is_still_reachable()
    {
        // RBAC that grants a namespace but not the right to enumerate namespaces: the
        // picker holds only "All namespaces", and the context's own namespace is the one
        // place this user can actually list anything.
        var tab = Tab(null, null);

        tab.ApplyInitialView(fallbackNamespace: "team-a");

        await Assert.That(tab.NamespaceOptions).Contains("team-a");
        await Assert.That(tab.SelectedNamespace).IsEqualTo("team-a");
    }

    [Test]
    public async Task View_changes_are_reported_only_once_the_tab_has_settled()
    {
        var tab = Tab(null, null, "default", "payments");
        var configMaps = tab.SidebarSections[0].Kinds[1];
        var raised = 0;
        tab.ViewStateChanged += (_, _) => raised++;

        tab.SelectedNamespace = "payments";
        await Assert.That(raised).IsEqualTo(0);

        tab.ApplyInitialView(fallbackNamespace: null);
        raised = 0;

        tab.SelectedNamespace = "default";
        tab.SelectKindCommand.Execute(configMaps);

        await Assert.That(raised).IsEqualTo(2);
        await Assert.That(tab.ViewKindKey).IsEqualTo("/ConfigMap");
    }
}
