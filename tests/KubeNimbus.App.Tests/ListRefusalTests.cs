using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #339 in the resource list: a 403 on the list's shared watch ends it (WatchCache) instead of
/// retrying it behind "connection lost" and a spinner, and the list says the account may not
/// list the kind, in the server's words, with a namespace to try when it asked the whole cluster.
/// The engine half — that a real 403 ends the shared watch after one request — is
/// <c>WatchCacheTests</c> in the Core tests.
/// </summary>
public class ListRefusalTests
{
    private const string Sentence =
        "pods is forbidden: User \"system:serviceaccount:team:viewer\" cannot list resource \"pods\" in API group \"\" at the cluster scope";

    private static WatchRefusedException Refused() =>
        WatchRefusedException.From(new HttpRequestException(Sentence, null, HttpStatusCode.Forbidden))!;

    /// <summary>
    /// Mutation-checked: with <c>EndListWatch</c> treating a refusal like any other ending, the
    /// list lands on "No Pods found" — a verdict the app does not have (UI rule 20).
    /// </summary>
    [Test]
    public async Task A_refused_list_ends_the_wait_and_says_why_not_that_it_is_empty()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.IsListLoading = true;

        tab.EndListWatch(Refused(), CancellationToken.None);

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsListRefused).IsTrue();
        await Assert.That(tab.ListRefusal).IsEqualTo(Sentence);
        await Assert.That(tab.ListRefusalHint).Contains("namespace");
    }

    [Test]
    public async Task A_refused_list_in_a_chosen_namespace_offers_no_namespace_hint()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.SelectedNamespace = "payments";
        tab.IsListLoading = true;

        tab.EndListWatch(Refused(), CancellationToken.None);

        await Assert.That(tab.IsListRefused).IsTrue();
        await Assert.That(tab.ListRefusalHint).IsEqualTo("");
    }

    [Test]
    public async Task Another_kind_or_namespace_asks_again_and_a_stale_ending_says_nothing()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.EndListWatch(Refused(), CancellationToken.None);

        tab.SelectedNamespace = "payments";
        await Assert.That(tab.IsListRefused).IsFalse();

        tab.EndListWatch(Refused(), new CancellationToken(canceled: true));
        await Assert.That(tab.IsListRefused).IsFalse();
    }

    private static WatchRefusedException RefusedIn(string ns) =>
        WatchRefusedException.From(new HttpRequestException(
            $"pods is forbidden: User \"viewer\" cannot list resource \"pods\" in API group \"\" in the namespace \"{ns}\"",
            null, HttpStatusCode.Forbidden))!;

    /// <summary>
    /// #354: several namespaces, every one refused. Mutation-checked: with
    /// <c>ConcludeNamespaceRefusals</c> returning at once, the list lands on "No Pods found"
    /// plus a warning line, which is what it did before.
    /// </summary>
    [Test]
    public async Task Several_namespaces_all_refused_say_so_in_the_lists_place()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.IsListLoading = true;
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Reset));
        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Reset));

        // What NamespaceWatch does for a namespace whose watch ended: the failure, then its Synced.
        tab.EndNamespaceWatch("a", RefusedIn("a"));
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Synced));
        await Assert.That(tab.IsListRefused).IsFalse();

        tab.EndNamespaceWatch("b", RefusedIn("b"));
        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Synced));

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsListRefused).IsTrue();
        await Assert.That(tab.ListRefusal!).Contains("namespace \"a\"");
        await Assert.That(tab.ListRefusal!).Contains("namespace \"b\"");
        await Assert.That(tab.ListRefusalHint).IsEqualTo("");
        await Assert.That(tab.ConnectionWarning).IsNull();
    }

    [Test]
    public async Task Several_namespaces_some_refused_keep_the_list_and_a_warning_that_stays()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.IsListLoading = true;
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Reset));
        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Reset));
        tab.EndNamespaceWatch("a", RefusedIn("a"));
        tab.ApplyNamespaced(new("a", ResourceEvent<DynamicResource>.Synced));
        tab.ApplyNamespaced(new("b", ResourceEvent<DynamicResource>.Synced));

        // b answered with nothing: that is a real empty list, and a's refusal stays said.
        await Assert.That(tab.IsListRefused).IsFalse();
        await Assert.That(tab.IsListEmpty).IsTrue();
        await Assert.That(tab.ConnectionWarning!).StartsWith("a: ");
    }

    /// <summary>
    /// #354: a fleet list whose every member refused it. Mutation-checked: with
    /// <c>EndFleetWatch</c> only ending the wait, the list says "No Pods found".
    /// </summary>
    [Test]
    public async Task A_fleet_list_every_member_refused_says_so_one_line_per_cluster()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.IsListLoading = true;

        tab.EndFleetMemberWatch("prod-eu", Refused());
        tab.EndFleetMemberWatch("prod-us", Refused());
        tab.EndFleetWatch(targets: 2);

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.IsListRefused).IsTrue();
        await Assert.That(tab.ListRefusal!.Split("\n\n")).IsEquivalentTo([$"prod-eu: {Sentence}", $"prod-us: {Sentence}"]);
        await Assert.That(tab.ConnectionWarning).IsNull();
    }

    [Test]
    public async Task A_fleet_list_some_members_refused_is_not_a_refusal()
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.IsListLoading = true;

        tab.EndFleetMemberWatch("prod-eu", Refused());
        tab.EndFleetMemberWatch("prod-us", new InvalidOperationException("The watch ended."));
        tab.EndFleetWatch(targets: 2);

        await Assert.That(tab.IsListRefused).IsFalse();
        await Assert.That(tab.IsListLoading).IsFalse();
    }
}
