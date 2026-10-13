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
}
