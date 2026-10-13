using System.Net;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #339: a 403 on a watched list ends the wait with the refusal stated in the list's place, in
/// the server's words. The demo cluster has no API server to refuse anything, so the refusal is
/// handed to the same method the watch's ending calls.
/// </summary>
internal static class RefusalScenarios
{
    private static WatchRefusedException Refused(string sentence) =>
        WatchRefusedException.From(new HttpRequestException(sentence, null, HttpStatusCode.Forbidden))!;

    private static ClusterTabViewModel DemoTab(Func<SidebarKindViewModel, bool> kind)
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(kind));
        return tab;
    }

    /// <summary>Pods across the cluster, refused: the sentence, and a namespace to try instead.</summary>
    public static ClusterTabViewModel List()
    {
        var tab = DemoTab(k => k.Descriptor is { Group: "", Kind: "Pod" });
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.EndListWatch(
            Refused("pods is forbidden: User \"system:serviceaccount:payments:viewer\" cannot list resource \"pods\" in API group \"\" at the cluster scope"),
            CancellationToken.None);
        return tab;
    }

    /// <summary>The Argo CD dashboard, refused across the cluster.</summary>
    public static ClusterTabViewModel Argo()
    {
        var tab = DemoTab(k => k.IsArgoDashboard);
        tab.ArgoApplications.Clear();
        tab.ArgoCounts = null;
        tab.EndArgoWatch(
            Refused("applications.argoproj.io is forbidden: User \"system:serviceaccount:payments:viewer\" cannot list resource \"applications\" in API group \"argoproj.io\" at the cluster scope"),
            CancellationToken.None);
        return tab;
    }
}
