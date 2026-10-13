using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Helm release browser as a child of the cluster tab (ENG-13, #361), exercised on its
/// own through a stand-in host: no tab, no cluster. The demo dataset is what a host with no
/// client on the demo cluster reads, so these run the browser's real read path.
/// </summary>
public class HelmBrowserViewModelTests
{
    private sealed class Host : IHelmBrowserHost
    {
        public ClusterClient? Client => null;

        public bool IsDemo { get; init; } = true;

        public bool IsBehindFailedReconnect { get; init; }

        public IReadOnlyList<string> SelectedNamespaces { get; init; } = [];

        public List<string> Warnings { get; } = [];

        public List<HelmRelease> Opened { get; } = [];

        public void ReportHelmReadFailure(string message) => Warnings.Add(message);

        public void OpenHelmRelease(HelmRelease release) => Opened.Add(release);
    }

    [Test]
    public async Task A_read_lists_every_release_in_key_order_and_selects_the_first()
    {
        var browser = new HelmBrowserViewModel(new Host());

        await browser.RefreshAsync();

        await Assert.That(browser.Releases.Count).IsEqualTo(Demo.DemoData.HelmReleases.Count);
        await Assert.That(browser.Releases.Select(r => $"{r.Namespace}/{r.Name}")).IsOrderedBy(k => k, StringComparer.Ordinal);
        await Assert.That(browser.SelectedRelease).IsSameReferenceAs(browser.Releases[0]);
        await Assert.That(browser.IsLoading).IsFalse();
        await Assert.That(browser.IsEmpty).IsFalse();
    }

    /// <summary>A namespace with no release is the empty state, not a blank grid (UI rule 9).</summary>
    [Test]
    public async Task A_namespace_with_no_release_is_the_empty_state()
    {
        var browser = new HelmBrowserViewModel(new Host { SelectedNamespaces = ["ledger"] });

        await browser.RefreshAsync();

        await Assert.That(browser.Releases).IsEmpty();
        await Assert.That(browser.SelectedRelease).IsNull();
        await Assert.That(browser.IsEmpty).IsTrue();
        await Assert.That(browser.IsLoading).IsFalse();
    }

    [Test]
    public async Task The_chosen_namespaces_narrow_the_read()
    {
        var browser = new HelmBrowserViewModel(new Host { SelectedNamespaces = ["ledger", "payments"] });

        await browser.RefreshAsync();

        await Assert.That(browser.Releases.Count).IsEqualTo(Demo.DemoData.HelmReleases.Count(r => r.Namespace == "payments"));
    }

    /// <summary>
    /// #311: behind a failed reconnect's view the client is the one that just failed, so no
    /// read starts, and the rows on screen are left as they were rather than cleared to an
    /// empty state that would claim the cluster has no releases.
    /// </summary>
    [Test]
    public async Task No_read_starts_behind_a_failed_reconnect_or_with_no_client()
    {
        foreach (var host in new[] { new Host { IsBehindFailedReconnect = true }, new Host { IsDemo = false } })
        {
            var browser = new HelmBrowserViewModel(host);
            browser.Releases.Add(new HelmReleaseRowViewModel(Demo.DemoData.HelmReleases[0]));

            await browser.RefreshAsync();

            await Assert.That(browser.Releases.Count).IsEqualTo(1);
            await Assert.That(browser.IsLoading).IsFalse();
            await Assert.That(browser.IsEmpty).IsFalse();
        }
    }

    [Test]
    public async Task The_header_sort_is_kept_across_a_reload()
    {
        var browser = new HelmBrowserViewModel(new Host());
        await browser.RefreshAsync();
        browser.Sort.Toggle(HelmReleaseComparer.Revision);
        browser.Sort.Toggle(HelmReleaseComparer.Revision);

        await browser.RefreshAsync();

        await Assert.That(browser.Sort.Column).IsEqualTo(HelmReleaseComparer.Revision);
        await Assert.That(browser.Releases.Select(r => r.Revision)).IsOrderedByDescending(r => r);
    }

    [Test]
    public async Task Opening_hands_the_selected_release_to_the_host()
    {
        var host = new Host();
        var browser = new HelmBrowserViewModel(host);
        await browser.RefreshAsync();
        browser.SelectedRelease = browser.Releases[^1];

        browser.OpenSelectedCommand.Execute(null);

        await Assert.That(host.Opened.Count).IsEqualTo(1);
        await Assert.That(host.Opened[0]).IsSameReferenceAs(browser.Releases[^1].Release);
    }

    [Test]
    public async Task Opening_with_nothing_selected_or_no_cluster_does_nothing()
    {
        var host = new Host();
        new HelmBrowserViewModel(host).OpenSelectedCommand.Execute(null);

        var offline = new Host { IsDemo = false };
        var browser = new HelmBrowserViewModel(offline) { SelectedRelease = new HelmReleaseRowViewModel(Demo.DemoData.HelmReleases[0]) };
        browser.OpenSelectedCommand.Execute(null);

        await Assert.That(host.Opened).IsEmpty();
        await Assert.That(offline.Opened).IsEmpty();
    }
}
