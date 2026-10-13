using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The Helm release browser: reading releases from their storage Secrets for the chosen
// namespaces, and opening one (helm-releases.md).
public sealed partial class ClusterTabViewModel
{
    /// <summary>Reloads the Helm release list for the selected namespace.</summary>
    [RelayCommand]
    private async Task RefreshHelmReleasesAsync()
    {
        // Behind a failed reconnect's view the client is the one that just failed, so a
        // namespace change starts no read (#311, as RestartWatch does for #294); the
        // Reconnect that succeeds runs Refresh, which reads again.
        if ((Client is null && !IsDemo) || _reconnectFailed)
        {
            return;
        }

        IsHelmLoading = true;
        IsHelmEmpty = false;
        HelmReleases.Clear();
        try
        {
            // One read per chosen namespace (none: the whole cluster), as the list's watch does,
            // so a user granted those namespaces and not the cluster can still read them.
            var namespaces = SelectedNamespaces;
            List<HelmRelease> releases = [];
            if (Client is null)
            {
                releases.AddRange(Demo.DemoData.HelmReleases.Where(r => namespaces.Count == 0 || namespaces.Contains(r.Namespace)));
            }
            else if (namespaces.Count == 0)
            {
                releases.AddRange(await Client.ListHelmReleasesAsync(null));
            }
            else
            {
                foreach (var ns in namespaces)
                {
                    releases.AddRange(await Client.ListHelmReleasesAsync(ns));
                }
            }
            foreach (var release in releases.Select(r => new HelmReleaseRowViewModel(r)).Order(HelmSort.Comparer))
            {
                HelmReleases.Add(release);
            }

            SelectedHelmRelease = HelmReleases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"Could not read Helm releases: {ex.Message}";
        }
        finally
        {
            IsHelmLoading = false;
            IsHelmEmpty = HelmReleases.Count == 0;
        }
    }

    /// <summary>Double-click / Enter on a release row: opens its values/manifest/notes/history tab.</summary>
    [RelayCommand]
    private void OpenSelectedHelmRelease()
    {
        if (SelectedHelmRelease is not { } row || (Client is null && !IsDemo))
        {
            return;
        }

        var key = $"helm:{row.Namespace}/{row.Name}";
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            existing.IsPreview = false;
            SelectedInspectorTab = existing;
            return;
        }

        AddInspectorTab(new HelmReleaseTabViewModel(Client, row.Release), replacePreview: false);
    }
}
