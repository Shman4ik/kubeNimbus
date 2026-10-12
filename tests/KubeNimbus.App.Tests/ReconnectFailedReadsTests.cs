using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #311: the Helm and Argo views are one-shot reads, not watches, so #294's guard in
/// <c>RestartWatch</c> did not cover them. Behind a failed reconnect's view the client is the
/// one that just failed, and a namespace change or a refresh there starts no read.
/// </summary>
[NotInParallel]
public class ReconnectFailedReadsTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    /// <summary>Mutation-checked by removing the <c>_reconnectFailed</c> guard from either read.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Behind_a_failed_reconnect_helm_and_argo_start_no_read(bool helm)
    {
        using var server = new StubApiServer();
        var client = server.Connect();
        var tab = new ClusterTabViewModel(client.Context) { Client = client, IsConnected = true };
        var section = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        section.Kinds.Add(new SidebarKindViewModel(
            new ResourceDescriptor("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []), "argo"));
        tab.SidebarSections.Add(section);
        tab.SetNamespaces(["team-a", "team-b"]);
        if (helm)
        {
            tab.IsHelmView = true;
        }
        else
        {
            tab.IsArgoView = true;
        }

        await tab.ShowReconnectFailureAsync(new HttpRequestException("No connection could be made"));
        var before = server.Requests.Count;

        tab.SelectedNamespace = "team-b";
        tab.RefreshCommand.Execute(null);
        await Task.Delay(300);

        await Assert.That(server.Requests.Count).IsEqualTo(before);
        await Assert.That(tab.IsHelmLoading).IsFalse();
        await Assert.That(tab.IsArgoLoading).IsFalse();
    }
}
