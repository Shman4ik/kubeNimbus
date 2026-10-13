using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The Helm release browser is its own view model, HelmBrowserViewModel (ENG-13, #361); this
// is the half of it that is the tab's: owning one, and what it is given (helm-releases.md).
public sealed partial class ClusterTabViewModel : IHelmBrowserHost
{
    /// <summary>The Helm release browser, shown in the list's slot while <see cref="IsHelmView"/>.</summary>
    public HelmBrowserViewModel Helm { get; }

    bool IHelmBrowserHost.IsBehindFailedReconnect => _reconnectFailed;

    void IHelmBrowserHost.ReportHelmReadFailure(string message) => ConnectionWarning = message;

    void IHelmBrowserHost.OpenHelmRelease(HelmRelease release)
    {
        var key = $"helm:{release.Namespace}/{release.Name}";
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new HelmReleaseTabViewModel(Client, release), replacePreview: false);
    }
}
