using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Inspector tab for one Argo CD Application: what Git says it should be, what the cluster
/// made of that, and the objects Argo is managing on its behalf. It answers the question the
/// dashboard's two pills raise and cannot fit — <em>which</em> resource is degraded, and what
/// Argo said about it.
///
/// <para>
/// Read-only, and deliberately: Sync and Refresh arm the confirm strip above the list
/// (UI rule 17), and an action fired from inside a dock tab would arm a strip that a
/// maximized inspector is covering. The list's context menu and the command palette are the
/// two ways in, exactly as they are for scale, restart and drain.
/// </para>
/// </summary>
public sealed partial class ArgoApplicationTabViewModel : InspectorTabViewModelBase
{
    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly Func<OwnerRef, string?, Task>? _openResource;
    private readonly CancellationTokenSource _cts = new();

    public ArgoApplicationTabViewModel(
        ClusterClient? client,
        ResourceDescriptor descriptor,
        ArgoApplication application,
        Func<OwnerRef, string?, Task>? openResource = null,
        string clusterName = "")
        : base($"Argo/{application?.Name}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(application);

        _client = client;
        _descriptor = descriptor;
        _openResource = openResource;
        Key = KeyFor(clusterName, application.Namespace, application.Name);
        Apply(application);
    }

    /// <summary>
    /// Cluster-qualified, like every other inspector key: the same Application name exists in
    /// the <c>argocd</c> namespace of every cluster in a fleet, and without the qualifier the
    /// second cluster's Application would silently reuse the first one's tab.
    /// </summary>
    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"argo:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    [ObservableProperty]
    private ArgoApplication? _application;

    /// <summary>The objects Argo manages for this Application, each with its own sync and health.</summary>
    public ObservableCollection<ArgoResourceRowViewModel> Resources { get; } = [];

    public ObservableCollection<ArgoCondition> Conditions { get; } = [];

    public ObservableCollection<ArgoRevision> History { get; } = [];

    /// <summary>
    /// Which of Overview / Resources / Conditions / History is showing. On the view model
    /// rather than left to the strip's own <c>SelectedIndex</c> (which is what the Helm pane
    /// does) because two things outside the view need to set it: the screenshot harness, and
    /// any future deep link — the access review's <c>WhoCanTabIndex</c> is the precedent.
    /// The strip and the headerless TabControl both bind here, so they cannot disagree.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>Index of the Resources tab — the one carrying "which resource is degraded".</summary>
    public const int ResourcesTabIndex = 1;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// "3 of 24 out of sync · 1 degraded" — the resource list's own summary, so a long list
    /// does not have to be scrolled to find out whether anything in it is wrong.
    /// </summary>
    [ObservableProperty]
    private string _resourceSummary = "";

    /// <summary>
    /// The two pills at the top of the Overview tab. Projected here rather than bound
    /// straight to the enums so the view can use the app's own <c>HealthEqualsConverter</c>
    /// vocabulary — the same one the dashboard rows and every other status pill in the app
    /// use — instead of a converter that exists for one pane.
    /// </summary>
    public string SyncText => Application?.Sync.ToString() ?? "";

    public string HealthText => Application?.Health.ToString() ?? "";

    public string SyncHealth => Application?.Sync switch
    {
        ArgoSyncState.Synced => "ok",
        ArgoSyncState.OutOfSync => "warn",
        _ => "idle",
    };

    public string HealthHealth => Application?.Health switch
    {
        ArgoHealthState.Healthy => "ok",
        ArgoHealthState.Progressing => "warn",
        ArgoHealthState.Degraded or ArgoHealthState.Missing => "error",
        _ => "idle",
    };

    public bool HasConditions => Conditions.Count > 0;

    public bool HasHistory => History.Count > 0;

    public bool HasResources => Resources.Count > 0;

    /// <summary>
    /// An Application with no <c>status.resources</c> at all has never been reconciled — a
    /// fresh Application, or one whose repository Argo cannot read. That is its own state and
    /// not an empty list (UI rule 9).
    /// </summary>
    public string EmptyResourcesNotice =>
        Application is { Sync: ArgoSyncState.Unknown }
            ? "Argo has not reconciled this Application yet, so it is not managing any resources. Its conditions "
              + "below usually say why — an unreachable repository is the common one."
            : "Argo reports no managed resources for this Application.";

    private void Apply(ArgoApplication application)
    {
        Application = application;

        Resources.Clear();
        foreach (var resource in application.Resources
            .OrderBy(ArgoResourceRowViewModel.Rank)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            Resources.Add(new ArgoResourceRowViewModel(resource, OpenResourceAsync));
        }

        Conditions.Clear();
        foreach (var condition in application.Conditions)
        {
            Conditions.Add(condition);
        }

        History.Clear();
        foreach (var revision in application.History)
        {
            History.Add(revision);
        }

        var outOfSync = application.Resources.Count(r => r.Sync == ArgoSyncState.OutOfSync);
        var unhealthy = application.Resources.Count(r =>
            r.Health is ArgoHealthState.Degraded or ArgoHealthState.Missing);
        var parts = new List<string>(2) { $"{application.Resources.Count} managed" };
        if (outOfSync > 0)
        {
            parts.Add($"{outOfSync} out of sync");
        }

        if (unhealthy > 0)
        {
            parts.Add($"{unhealthy} degraded or missing");
        }

        ResourceSummary = string.Join(" · ", parts);

        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(EmptyResourcesNotice));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(SyncHealth));
        OnPropertyChanged(nameof(HealthHealth));
    }

    /// <summary>
    /// Re-reads the Application from the API server. Not a watch: an Application changes when
    /// Argo reconciles it, which is every few minutes, and one pane holding a watch open for
    /// that is the same trade pod detail's Events tab already settled in favour of an explicit
    /// refresh. Asking Argo to re-compare against Git is a different thing entirely and is the
    /// list's Refresh action, not this button.
    /// </summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_client is not { } client || Application is not { } current)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var resource = await client.ReadResourceAsync(
                _descriptor, current.Namespace, current.Name, _cts.Token);
            if (resource is null)
            {
                ErrorMessage = $"{current.Key} is no longer on the cluster.";
                return;
            }

            Apply(ArgoCd.ReadApplication(resource));
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-load
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read the Application: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens one of the managed resources in the ordinary way — the same resolve-and-open path
    /// owner chips and event navigation use, so a Deployment Argo reports as Degraded is one
    /// click from its own YAML rather than from a search of the list.
    /// </summary>
    private Task OpenResourceAsync(ArgoResource resource)
    {
        if (_openResource is null)
        {
            return Task.CompletedTask;
        }

        var owner = new OwnerRef(resource.ApiVersion, resource.Kind, resource.Name, Uid: null, Controller: false);
        return _openResource(owner, resource.Namespace.Length > 0 ? resource.Namespace : null);
    }

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}

/// <summary>One object Argo manages for an Application, as a row in the detail pane.</summary>
public sealed partial class ArgoResourceRowViewModel(ArgoResource resource, Func<ArgoResource, Task> open)
    : ObservableObject
{
    public ArgoResource Resource { get; } = resource;

    public string Kind => Resource.Kind;

    public string Name => Resource.Name;

    public string Namespace => Resource.Namespace;

    /// <summary>"apps/v1 · payments" — group and namespace, which is what tells two same-named rows apart.</summary>
    public string Qualifier => Resource.Namespace.Length > 0
        ? $"{Resource.ApiVersion} · {Resource.Namespace}"
        : Resource.ApiVersion;

    public string SyncText => Resource.Sync.ToString();

    /// <summary>
    /// Argo leaves health unset for kinds it has no health check for — a ConfigMap has no
    /// notion of healthy — so an unknown health renders as nothing at all rather than as the
    /// word "Unknown" beside every configuration object in the list.
    /// </summary>
    public string HealthText => Resource.Health == ArgoHealthState.Unknown ? "" : Resource.Health.ToString();

    public bool HasHealth => Resource.Health != ArgoHealthState.Unknown;

    public string SyncHealth => Resource.Sync switch
    {
        ArgoSyncState.Synced => "ok",
        ArgoSyncState.OutOfSync => "warn",
        _ => "idle",
    };

    public string HealthHealth => Resource.Health switch
    {
        ArgoHealthState.Healthy => "ok",
        ArgoHealthState.Progressing => "warn",
        ArgoHealthState.Degraded or ArgoHealthState.Missing => "error",
        _ => "idle",
    };

    /// <summary>Worst first, so a 200-resource Application opens on the one that is broken.</summary>
    public static int Rank(ArgoResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.Health switch
        {
            ArgoHealthState.Degraded => 0,
            ArgoHealthState.Missing => 1,
            _ when resource.Sync == ArgoSyncState.OutOfSync => 2,
            ArgoHealthState.Progressing => 3,
            _ => 4,
        };
    }

    [RelayCommand]
    private Task Open() => open(Resource);
}
