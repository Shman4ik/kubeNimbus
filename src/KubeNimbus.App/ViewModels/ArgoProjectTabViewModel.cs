using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Inspector tab for one Argo CD AppProject (#194): which repositories its Applications may
/// deploy from, where to, which kinds, in which windows, and who holds its roles. Each list
/// is read off the object, and each empty list says what Argo makes of it — an empty source
/// list allows no repository, an empty cluster-kind allow list allows no cluster-scoped kind —
/// because that is the half of a project its YAML does not say.
/// </summary>
public sealed partial class ArgoProjectTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly CancellationTokenSource _cts = new();

    public ArgoProjectTabViewModel(
        ClusterClient? client, ResourceDescriptor descriptor, ArgoProject project, string clusterName = "")
        : base($"AppProject/{project?.Name}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(project);

        _client = client;
        _descriptor = descriptor;
        Key = KeyFor(clusterName, project.Namespace, project.Name);
        Project = project;
    }

    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"argo-project:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    [NotifyPropertyChangedFor(nameof(HasSyncWindows))]
    [NotifyPropertyChangedFor(nameof(HasRoles))]
    [NotifyPropertyChangedFor(nameof(HasClusterDeny))]
    [NotifyPropertyChangedFor(nameof(HasNamespaceAllow))]
    [NotifyPropertyChangedFor(nameof(HasNamespaceDeny))]
    private ArgoProject? _project;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasDescription => Project is { Description.Length: > 0 };

    public bool HasSyncWindows => Project is { SyncWindows.Count: > 0 };

    public bool HasRoles => Project is { Roles.Count: > 0 };

    public bool HasClusterDeny => Project is { ClusterResourceDeny.Count: > 0 };

    public bool HasNamespaceAllow => Project is { NamespaceResourceAllow.Count: > 0 };

    public bool HasNamespaceDeny => Project is { NamespaceResourceDeny.Count: > 0 };

    /// <summary>Re-reads the project from the API server.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_client is not { } client || Project is not { } current)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var resource = await client.ReadResourceAsync(_descriptor, current.Namespace, current.Name, _cts.Token);
            if (resource is null)
            {
                ErrorMessage = $"{current.Key} is no longer on the cluster.";
                return;
            }

            Project = ArgoCd.ReadProject(resource);
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-load
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read the AppProject: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
