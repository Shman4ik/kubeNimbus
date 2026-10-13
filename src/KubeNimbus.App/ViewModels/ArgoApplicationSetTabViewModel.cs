using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Inspector tab for one Argo CD ApplicationSet (#194): what it generates Applications from,
/// the template each one is stamped from, and the Applications it generated with their own
/// sync and health — the question its YAML answers only after reading the generator spec and
/// then going to look for the Applications by hand.
///
/// <para>
/// The generated Applications come from the set's own <c>status.resources</c> (Argo CD 2.6+),
/// and when the account can list Applications in the set's namespace each one's live sync and
/// health is laid over it, plus any Application the set owns that the status does not name
/// (an older Argo). Read-only, like the Application pane; Reload re-reads both.
/// </para>
/// </summary>
public sealed partial class ArgoApplicationSetTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly Func<OwnerRef, string?, Task>? _openResource;
    private readonly Func<CancellationToken, Task<ResourceDescriptor?>>? _applicationDescriptor;
    private readonly CancellationTokenSource _cts = new();

    public ArgoApplicationSetTabViewModel(
        ClusterClient? client,
        ResourceDescriptor descriptor,
        ArgoApplicationSet set,
        Func<OwnerRef, string?, Task>? openResource = null,
        string clusterName = "",
        Func<CancellationToken, Task<ResourceDescriptor?>>? applicationDescriptor = null)
        : base(clusterName.Length == 0 ? $"ApplicationSet/{set?.Name}" : $"ApplicationSet/{set?.Name} · {clusterName}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(set);

        _client = client;
        _descriptor = descriptor;
        _openResource = openResource;
        _applicationDescriptor = applicationDescriptor;
        Key = KeyFor(clusterName, set.Namespace, set.Name);
        Apply(set, live: null);
        if (client is not null)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Cluster-qualified, like every inspector key (fleet rows share names across clusters).</summary>
    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"argo-set:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    [ObservableProperty]
    private ArgoApplicationSet? _set;

    public ObservableCollection<ArgoGeneratedApplicationRowViewModel> Generated { get; } = [];

    public ObservableCollection<ArgoCondition> Conditions { get; } = [];

    /// <summary>Overview / Applications / Conditions; on the view model for the harness, like the Application pane's.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>Index of the Applications tab — the generated list.</summary>
    public const int ApplicationsTabIndex = 1;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>"3 generated · 1 out of sync" — the Applications tab's own caption.</summary>
    [ObservableProperty]
    private string _generatedSummary = "";

    /// <summary>
    /// Why the live sync and health are missing, when the Applications could not be listed —
    /// stated above the list rather than leaving rows that look unsynced (UI rule 20).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveNotice))]
    private string? _liveNotice;

    public bool HasLiveNotice => !string.IsNullOrEmpty(LiveNotice);

    public bool HasGenerated => Generated.Count > 0;

    public bool HasConditions => Conditions.Count > 0;

    public string GeneratorsText => Set is { Generators.Count: > 0 } set
        ? string.Join("\n", set.Generators)
        : "None — this set generates nothing.";

    /// <summary>The set's own health (Argo 3.x writes one), or empty on an older Argo.</summary>
    public string HealthText => Set?.Health ?? "";

    public bool HasHealth => HealthText.Length > 0;

    public string HealthHealth => HealthText switch
    {
        "Healthy" => "ok",
        "Progressing" => "warn",
        "Degraded" => "error",
        _ => "idle",
    };

    public string EmptyGeneratedNotice =>
        Conditions.Any(c => c.IsProblem)
            ? "This set has generated no Applications. Its conditions say why."
            : "This set has generated no Applications — its generators produced nothing.";

    private void Apply(ArgoApplicationSet set, IReadOnlyList<ArgoApplication>? live)
    {
        Set = set;

        var byName = live?.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var rows = new List<ArgoGeneratedApplicationRowViewModel>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generated in set.Generated)
        {
            named.Add(generated.Name);
            rows.Add(byName is not null && byName.TryGetValue(generated.Name, out var app)
                ? new ArgoGeneratedApplicationRowViewModel(app.Name, app.Namespace, app.Sync, app.Health, OpenAsync)
                : new ArgoGeneratedApplicationRowViewModel(generated.Name, generated.Namespace, generated.Sync, generated.Health, OpenAsync));
        }

        // Applications this set owns that its status does not list: an Argo older than 2.6,
        // whose ApplicationSet status has no resources at all.
        if (live is not null && set.Uid.Length > 0)
        {
            foreach (var app in live.Where(a => !named.Contains(a.Name) && OwnedBy(a, set.Uid)))
            {
                rows.Add(new ArgoGeneratedApplicationRowViewModel(app.Name, app.Namespace, app.Sync, app.Health, OpenAsync));
            }
        }

        Generated.Clear();
        foreach (var row in rows
            .OrderBy(r => ArgoGeneratedApplicationRowViewModel.Rank(r))
            .ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            Generated.Add(row);
        }

        Conditions.Clear();
        foreach (var condition in set.Conditions)
        {
            Conditions.Add(condition);
        }

        var outOfSync = rows.Count(r => r.Sync == ArgoSyncState.OutOfSync);
        var unhealthy = rows.Count(r => r.Health is ArgoHealthState.Degraded or ArgoHealthState.Missing);
        var parts = new List<string>(3) { $"{rows.Count} generated" };
        if (outOfSync > 0)
        {
            parts.Add($"{outOfSync} out of sync");
        }

        if (unhealthy > 0)
        {
            parts.Add($"{unhealthy} degraded or missing");
        }

        GeneratedSummary = string.Join(" · ", parts);

        OnPropertyChanged(nameof(HasGenerated));
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(GeneratorsText));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HasHealth));
        OnPropertyChanged(nameof(HealthHealth));
        OnPropertyChanged(nameof(EmptyGeneratedNotice));
    }

    private static bool OwnedBy(ArgoApplication application, string uid) =>
        application.Resource.Raw.TryGetProperty("metadata", out var metadata)
        && metadata.TryGetProperty("ownerReferences", out var owners)
        && owners.ValueKind == System.Text.Json.JsonValueKind.Array
        && owners.EnumerateArray().Any(o =>
            o.TryGetProperty("uid", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String && u.GetString() == uid);

    /// <summary>Re-reads the set, then the Applications in its namespace for their live state.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_client is not { } client || Set is not { } current)
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

            var set = ArgoCd.ReadApplicationSet(resource);
            IReadOnlyList<ArgoApplication>? live = null;
            try
            {
                if (_applicationDescriptor is not null
                    && await _applicationDescriptor(_cts.Token) is { } applications)
                {
                    live = await client.ListArgoApplicationsAsync(applications, set.Namespace, _cts.Token);
                    LiveNotice = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LiveNotice = $"Sync and health are from the set's own status: the Applications could not be read ({ex.Message}).";
            }

            Apply(set, live);
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-load
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read the ApplicationSet: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task OpenAsync(ArgoGeneratedApplicationRowViewModel row)
    {
        if (_openResource is null)
        {
            return Task.CompletedTask;
        }

        // Argo serves Application and ApplicationSet at one version (v1alpha1 in every release
        // so far), and the resolve checks what comes back against the reference anyway.
        var apiVersion = $"{ArgoCd.Group}/{_descriptor.Version}";
        return _openResource(new OwnerRef(apiVersion, "Application", row.Name, Uid: null, Controller: false), row.Namespace);
    }

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}

/// <summary>One Application an ApplicationSet generated, as a row in its pane.</summary>
public sealed partial class ArgoGeneratedApplicationRowViewModel(
    string name, string @namespace, ArgoSyncState sync, ArgoHealthState health,
    Func<ArgoGeneratedApplicationRowViewModel, Task> open) : ObservableObject
{
    public string Name { get; } = name;

    public string Namespace { get; } = @namespace;

    public ArgoSyncState Sync { get; } = sync;

    public ArgoHealthState Health { get; } = health;

    /// <summary>
    /// Argo leaves both blank on a freshly generated Application it has not compared yet;
    /// those render as nothing rather than as two "Unknown" pills.
    /// </summary>
    public string SyncText => Sync == ArgoSyncState.Unknown ? "" : Sync.ToString();

    public bool HasSync => Sync != ArgoSyncState.Unknown;

    public string HealthText => Health == ArgoHealthState.Unknown ? "" : Health.ToString();

    public bool HasHealth => Health != ArgoHealthState.Unknown;

    public string SyncHealth => Sync switch
    {
        ArgoSyncState.Synced => "ok",
        ArgoSyncState.OutOfSync => "warn",
        _ => "idle",
    };

    public string HealthHealth => Health switch
    {
        ArgoHealthState.Healthy => "ok",
        ArgoHealthState.Progressing => "warn",
        ArgoHealthState.Degraded or ArgoHealthState.Missing => "error",
        _ => "idle",
    };

    /// <summary>Worst first, the dashboard's own order.</summary>
    public static int Rank(ArgoGeneratedApplicationRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Health switch
        {
            ArgoHealthState.Degraded => 0,
            ArgoHealthState.Missing => 1,
            _ when row.Sync == ArgoSyncState.OutOfSync => 2,
            ArgoHealthState.Progressing => 3,
            _ => 4,
        };
    }

    [RelayCommand]
    private Task Open() => open(this);
}
