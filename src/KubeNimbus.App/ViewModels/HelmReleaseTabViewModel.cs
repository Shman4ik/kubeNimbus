using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Inspector tab for one Helm release: the values it was installed with, the
/// manifest Helm rendered, its notes, and its revision history. Read-only —
/// installing/upgrading/rolling back is Helm's job, not a viewer's.
/// </summary>
public sealed partial class HelmReleaseTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient _client;
    private readonly CancellationTokenSource _cts = new();

    public override string Key { get; }

    public string ReleaseNamespace { get; }

    public string ReleaseName { get; }

    public ObservableCollection<HelmReleaseRowViewModel> History { get; } = [];

    [ObservableProperty]
    private HelmReleaseRowViewModel? _selectedRevision;

    [ObservableProperty]
    private string _valuesYaml = "";

    [ObservableProperty]
    private string _manifest = "";

    [ObservableProperty]
    private string _notes = "";

    /// <summary>True while a revision is being fetched — the panes show a loading state rather than stale text.</summary>
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Set when the release stores no user-supplied values (chart defaults only).</summary>
    public bool HasValues => ValuesYaml.Trim().Length > 0;

    public bool HasNotes => Notes.Trim().Length > 0;

    partial void OnValuesYamlChanged(string value) => OnPropertyChanged(nameof(HasValues));

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    public HelmReleaseTabViewModel(ClusterClient client, HelmRelease release)
        : base($"Helm/{release.Name}")
    {
        _client = client;
        ReleaseNamespace = release.Namespace;
        ReleaseName = release.Name;
        Key = $"helm:{ReleaseNamespace}/{ReleaseName}";

        _ = LoadAsync(release.Revision);
    }

    private async Task LoadAsync(int? revision)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var detail = await _client.GetHelmReleaseAsync(ReleaseNamespace, ReleaseName, revision, _cts.Token);
            if (detail is null)
            {
                ErrorMessage = $"Release {ReleaseName} revision {revision} is no longer stored on the cluster.";
                return;
            }

            ValuesYaml = detail.ValuesYaml;
            Manifest = detail.Manifest;
            Notes = detail.Notes;

            if (History.Count == 0)
            {
                await LoadHistoryAsync();
            }

            foreach (var row in History)
            {
                row.IsSelected = row.Revision == detail.Release.Revision;
            }

            SelectedRevision = History.FirstOrDefault(r => r.Revision == detail.Release.Revision);
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-load
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read release: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadHistoryAsync()
    {
        var revisions = await _client.GetHelmReleaseHistoryAsync(ReleaseNamespace, ReleaseName, _cts.Token);
        History.Clear();
        foreach (var revision in revisions)
        {
            History.Add(new HelmReleaseRowViewModel(revision));
        }
    }

    /// <summary>Double-click / Enter on a history row: shows that revision's values, manifest and notes.</summary>
    [RelayCommand]
    private async Task ShowRevisionAsync(HelmReleaseRowViewModel? row)
    {
        if (row is null || IsLoading)
        {
            return;
        }

        await LoadAsync(row.Revision);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        History.Clear();
        await LoadAsync(SelectedRevision?.Revision);
    }

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
