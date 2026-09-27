using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The namespace picker under narrow RBAC. A user granted one namespace and not
/// <c>list namespaces</c> is the expected case on a shared cluster, and before this the
/// picker could only offer names the cluster had listed — so with no context namespace
/// there was no control that could narrow the query at all.
/// <c>[NotInParallel]</c> because choosing a namespace writes the recents to the
/// workspace, a real file behind the process-global <c>WorkspaceStore.DirectoryOverride</c>.
/// </summary>
[NotInParallel]
public class ClusterTabNamespacePickerTests
{
    [Test]
    public async Task A_typed_name_is_offered_first_while_namespaces_have_not_been_listed()
    {
        var tab = TestObjects.Tab();

        tab.NamespaceFilter = "payments";

        await Assert.That(tab.FilteredNamespaces[0]).IsEqualTo(new NamespaceChoice("payments", false, IsTyped: true));
        await Assert.That(tab.NamespaceCandidate!.IsTyped).IsTrue();
        await Assert.That(tab.NamespaceSearchPlaceholder).Contains("type a namespace");
    }

    [Test]
    public async Task Choosing_a_typed_name_opens_it_and_keeps_it_in_the_picker()
    {
        var tab = TestObjects.Tab();
        tab.NamespaceFilter = "payments";

        tab.ChooseNamespace(tab.NamespaceCandidate!);
        tab.NamespaceFilter = "";

        await Assert.That(tab.SelectedNamespace).IsEqualTo("payments");
        await Assert.That(tab.NamespaceOptions).Contains("payments");
        await Assert.That(tab.FilteredNamespaces.Any(n => n.Name == "payments" && !n.IsTyped)).IsTrue();
    }

    [Test]
    public async Task A_name_the_API_server_would_refuse_is_not_offered()
    {
        var tab = TestObjects.Tab();

        tab.NamespaceFilter = "Payments_API";

        await Assert.That(tab.FilteredNamespaces.Any(n => n.IsTyped)).IsFalse();
    }

    [Test]
    public async Task Once_namespaces_are_listed_a_typed_name_is_not_offered()
    {
        var tab = TestObjects.Tab();
        tab.NamespaceOptions.Add("payments");
        tab.MarkNamespacesListed();

        // A name missing from a list that was read has been deleted, or never existed;
        // opening it would be an empty list that looks like a broken watch.
        tab.NamespaceFilter = "billing";

        await Assert.That(tab.FilteredNamespaces.Any(n => n.IsTyped)).IsFalse();
        await Assert.That(tab.NoNamespaceMatches).IsTrue();
        await Assert.That(tab.NamespaceSearchPlaceholder).IsEqualTo("Filter namespaces…");
    }

    [Test]
    public async Task A_name_already_in_the_list_is_not_offered_twice()
    {
        var tab = TestObjects.Tab();
        tab.NamespaceOptions.Add("payments");

        tab.NamespaceFilter = "payments";

        await Assert.That(tab.FilteredNamespaces.Count(n => n.Name == "payments")).IsEqualTo(1);
        await Assert.That(tab.FilteredNamespaces.Any(n => n.IsTyped)).IsFalse();
    }
}
