using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What the Helm release browser needs from the cluster tab that owns it: the tab's client
/// and namespaces, where a failed read is said, and how a release is opened in the dock.
/// </summary>
internal interface IHelmBrowserHost
{
    /// <summary>The tab's client; null on the demo cluster and before a connect.</summary>
    ClusterClient? Client { get; }

    bool IsDemo { get; }

    /// <summary>True behind a failed reconnect's view, where the client is the one that just failed (#311).</summary>
    bool IsBehindFailedReconnect { get; }

    /// <summary>The namespaces chosen in the tab's picker; empty is the whole cluster.</summary>
    IReadOnlyList<string> SelectedNamespaces { get; }

    /// <summary>Says a failed read where the tab says its other warnings.</summary>
    void ReportHelmReadFailure(string message);

    /// <summary>Opens the release's tab in the dock, or brings forward the one already open.</summary>
    void OpenHelmRelease(HelmRelease release);
}

/// <summary>
/// The Helm release browser (helm-releases.md): the releases read from their storage
/// Secrets for the tab's chosen namespaces, the grid's sort and selection, its loading and
/// empty states, and opening one. A child of <see cref="ClusterTabViewModel"/>, reached as
/// <see cref="ClusterTabViewModel.Helm"/>; whether the browser is the thing on screen is the
/// tab's <see cref="ClusterTabViewModel.IsHelmView"/>, because the tab decides what fills
/// its content area.
/// </summary>
public sealed partial class HelmBrowserViewModel : ObservableObject
{
    private readonly IHelmBrowserHost _host;

    internal HelmBrowserViewModel(IHelmBrowserHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public ObservableCollection<HelmReleaseRowViewModel> Releases { get; } = [];

    /// <summary>The Helm grid's header sort; namespace/name until a header is clicked. Kept across reloads for the tab's life.</summary>
    public GridSort<HelmReleaseRowViewModel> Sort => _sort ??= new(
        Releases, HelmReleaseComparer.ByKey, (column, descending) => new HelmReleaseComparer(column, descending),
        () => SelectedRelease, release => SelectedRelease = release);

    private GridSort<HelmReleaseRowViewModel>? _sort;

    [ObservableProperty]
    private HelmReleaseRowViewModel? _selectedRelease;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Reloads the Helm release list for the selected namespace.</summary>
    [RelayCommand]
    internal async Task RefreshAsync()
    {
        // Behind a failed reconnect's view the client is the one that just failed, so a
        // namespace change starts no read (#311, as RestartWatch does for #294); the
        // Reconnect that succeeds runs Refresh, which reads again.
        if ((_host.Client is null && !_host.IsDemo) || _host.IsBehindFailedReconnect)
        {
            return;
        }

        IsLoading = true;
        IsEmpty = false;
        Releases.Clear();
        try
        {
            // One read per chosen namespace (none: the whole cluster), as the list's watch does,
            // so a user granted those namespaces and not the cluster can still read them.
            var namespaces = _host.SelectedNamespaces;
            List<HelmRelease> releases = [];
            if (_host.Client is not { } client)
            {
                releases.AddRange(Demo.DemoData.HelmReleases.Where(r => namespaces.Count == 0 || namespaces.Contains(r.Namespace)));
            }
            else if (namespaces.Count == 0)
            {
                releases.AddRange(await client.ListHelmReleasesAsync(null));
            }
            else
            {
                foreach (var ns in namespaces)
                {
                    releases.AddRange(await client.ListHelmReleasesAsync(ns));
                }
            }
            foreach (var release in releases.Select(r => new HelmReleaseRowViewModel(r)).Order(Sort.Comparer))
            {
                Releases.Add(release);
            }

            SelectedRelease = Releases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _host.ReportHelmReadFailure($"Could not read Helm releases: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            IsEmpty = Releases.Count == 0;
        }
    }

    /// <summary>Double-click / Enter on a release row: opens its values/manifest/notes/history tab.</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedRelease is not { } row || (_host.Client is null && !_host.IsDemo))
        {
            return;
        }

        _host.OpenHelmRelease(row.Release);
    }
}
