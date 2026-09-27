using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Ingress detail: every route as <c>host/path → backend</c>, whether it is served over TLS,
/// and a URL that can be opened and copied. The backend Service opens its own detail pane,
/// so "the site is down" walks Ingress → Service → the pods behind it in three clicks.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is read off the Ingress object the list is already watching, so there is
/// no second stream and nothing to refresh — the rows follow the watch. The only request of
/// its own is the Events read.
/// </para>
/// <para>
/// The URL is <see cref="IngressRules.BuildUrl"/>'s, never a manifest string: a host that is
/// not a DNS name has no URL, and its row reads as plain text with the Open and Copy
/// actions disabled rather than hidden, so the reader can see there is nothing to open.
/// </para>
/// </remarks>
public sealed partial class IngressDetailTabViewModel : InspectorTabViewModelBase
{
    public const int RulesTabIndex = 0;

    public const int EventsTabIndex = 1;

    private readonly ClusterClient? _client;
    private readonly ResourceRowViewModel _row;
    private readonly Func<OwnerRef, string?, Task>? _openObject;
    private readonly CancellationTokenSource _cts = new();

    public IngressDetailTabViewModel(
        ClusterClient? client,
        ResourceRowViewModel row,
        Func<OwnerRef, string?, Task>? openObject = null,
        string clusterName = "")
        : base(clusterName.Length == 0 ? $"Ingress/{row.Name}" : $"Ingress/{row.Name} · {clusterName}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(row);

        _client = client;
        _row = row;
        _openObject = openObject;
        Key = KeyFor(clusterName, row.Namespace, row.Name);

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();
        _ = RefreshEventsAsync();
    }

    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"ingress:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    public string Namespace => _row.Namespace;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>"nginx · 203.0.113.10" — the class and where the controller says it is reachable.</summary>
    [ObservableProperty]
    private string _summaryText = "";

    public ObservableCollection<IngressRouteViewModel> Routes { get; } = [];

    public bool HasNoRoutes => Routes.Count == 0;

    /// <summary>Hosts a TLS entry lists that no rule serves — usually a typo in one of the two places.</summary>
    [ObservableProperty]
    private string _tlsWarning = "";

    public bool HasTlsWarning => TlsWarning.Length > 0;

    /// <summary>The last copy's confirmation, or why an open failed. One line under the routes.</summary>
    [ObservableProperty]
    private string _actionMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBackendCommand))]
    private IngressRouteViewModel? _selectedRoute;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceRowViewModel.Resource))
        {
            RefreshFromRow();
        }
    }

    private void RefreshFromRow()
    {
        var view = IngressRules.Read(_row.Resource);

        var summary = new List<string>();
        summary.Add(view.ClassName.Length > 0
            ? view.ClassFromAnnotation ? $"class {view.ClassName} (annotation)" : $"class {view.ClassName}"
            : "no class — the cluster's default IngressClass, if it has one");
        summary.Add(view.Addresses.Count > 0 ? string.Join(", ", view.Addresses) : "no address yet");
        SummaryText = string.Join(" · ", summary);

        TlsWarning = view.TlsHostsWithoutRule.Count == 0
            ? ""
            : $"TLS lists {string.Join(", ", view.TlsHostsWithoutRule)}, which no rule serves — check the host is spelled the same in both places.";
        OnPropertyChanged(nameof(HasTlsWarning));

        var routes = view.Paths.Select(p => new IngressRouteViewModel(p, isDefault: false)).ToList();
        if (view.DefaultBackend is { } fallback)
        {
            routes.Add(new IngressRouteViewModel(
                new IngressPath("", "", "", fallback, false, "", null), isDefault: true));
        }

        var selected = SelectedRoute?.Key;
        if (!routes.Select(r => r.Key).SequenceEqual(Routes.Select(r => r.Key), StringComparer.Ordinal)
            || !routes.Zip(Routes).All(pair => pair.First.Route == pair.Second.Route))
        {
            Routes.Clear();
            foreach (var route in routes)
            {
                Routes.Add(route);
            }

            SelectedRoute = Routes.FirstOrDefault(r => r.Key == selected);
            OnPropertyChanged(nameof(HasNoRoutes));
        }
    }

    // Each command takes the row it was pressed on (the row's own buttons pass it), and
    // falls back to the selected row for the context menu and the keyboard.

    private bool CanUseUrl(IngressRouteViewModel? route) => (route ?? SelectedRoute)?.Url is not null;

    private bool CanOpenBackend(IngressRouteViewModel? route) =>
        (route ?? SelectedRoute)?.CanOpenBackend == true && _openObject is not null;

    /// <summary>Opens the route's URL in the default browser. The URL is the validated one, never manifest text.</summary>
    [RelayCommand(CanExecute = nameof(CanUseUrl))]
    private void OpenUrl(IngressRouteViewModel? route)
    {
        if ((route ?? SelectedRoute)?.Url is not { } url)
        {
            return;
        }

        try
        {
            // UseShellExecute is what hands the URL to the default browser; without it this
            // tries to execute "https://…" as a program and throws.
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            ActionMessage = "";
        }
        catch (Exception ex)
        {
            ActionMessage = $"Could not open a browser: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseUrl))]
    private async Task CopyUrlAsync(IngressRouteViewModel? route)
    {
        if ((route ?? SelectedRoute)?.Url is not { } url
            || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(url.AbsoluteUri);
        ActionMessage = $"Copied {url.AbsoluteUri}";
    }

    /// <summary>The backend Service's own detail pane — which pods are behind this route, and are they serving.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenBackend))]
    private async Task OpenBackendAsync(IngressRouteViewModel? route)
    {
        if ((route ?? SelectedRoute)?.Route.Backend.ServiceName is { Length: > 0 } name && _openObject is not null)
        {
            await _openObject(new OwnerRef("v1", "Service", name, null, false), Namespace);
        }
    }

    // ----------------------------------------------------------------------- events

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EventsCaption))]
    private bool _isLoadingEvents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEventsError))]
    [NotifyPropertyChangedFor(nameof(EventsCaption))]
    private string? _eventsError;

    public bool HasEventsError => !string.IsNullOrEmpty(EventsError);

    public bool HasNoEvents => !IsLoadingEvents && !HasEventsError && Events.Count == 0;

    public string EventsCaption => IsLoadingEvents
        ? "Reading events…"
        : HasEventsError
            ? "Could not read events"
            : Events.Count switch
            {
                0 => "No recent events",
                1 => "1 event",
                var n => $"{n} events",
            };

    /// <summary>What the ingress controller said — a sync, a missing TLS secret, a rejected annotation.</summary>
    [RelayCommand]
    private async Task RefreshEventsAsync()
    {
        IsLoadingEvents = true;
        EventsError = null;
        try
        {
            var events = _client is { } client
                ? await client.GetEventsForAsync(_row.Resource, _cts.Token)
                : [.. DemoData.Events
                    .Where(e => e.InvolvedObject() is { Kind: "Ingress" } involved
                        && string.Equals(involved.Name, _row.Name, StringComparison.Ordinal)
                        && string.Equals(e.InvolvedObjectNamespace(), Namespace, StringComparison.Ordinal))];

            Events.Clear();
            foreach (var evt in events)
            {
                Events.Add(new EventRowViewModel(evt));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EventsError = ex.Message;
        }
        finally
        {
            IsLoadingEvents = false;
            OnPropertyChanged(nameof(EventsCaption));
            OnPropertyChanged(nameof(HasNoEvents));
        }
    }

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}

/// <summary>One route of the Ingress pane, formatted.</summary>
public sealed class IngressRouteViewModel
{
    public IngressRouteViewModel(IngressPath route, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(route);

        Route = route;
        IsDefault = isDefault;
        Key = isDefault ? "default" : $"{route.Host}|{route.Path}|{route.PathType}";
        HostText = isDefault ? "(default backend)" : route.Host.Length > 0 ? route.Host : "(any host)";
        PathText = isDefault ? "any unmatched request" : route.Path.Length > 0 ? route.Path : "(no paths — default backend)";
        PathTypeText = route.PathType;
        BackendText = route.Backend.Display;
        TlsText = isDefault ? "" : route.IsTls
            ? route.TlsSecret.Length > 0 ? $"TLS · {route.TlsSecret}" : "TLS · default certificate"
            : "no TLS";
        TlsHealth = route.IsTls ? ResourceHealth.Ok : ResourceHealth.Idle;
        Url = route.Url;
        UrlText = route.Url?.AbsoluteUri
                  ?? (isDefault ? "" : route.Host.Length == 0 ? "any host — no single URL"
                      : route.Host.StartsWith("*.", StringComparison.Ordinal) ? "wildcard host — no single URL"
                      : "not a valid hostname — shown as text");
        HasUrl = Url is not null;
        CanOpenBackend = route.Backend.ServiceName is { Length: > 0 };
    }

    public IngressPath Route { get; }

    public bool IsDefault { get; }

    public string Key { get; }

    public string HostText { get; }

    public string PathText { get; }

    public string PathTypeText { get; }

    public string BackendText { get; }

    public string TlsText { get; }

    public string TlsHealth { get; }

    public Uri? Url { get; }

    /// <summary>The URL, or the reason there is none — never a manifest string presented as a link.</summary>
    public string UrlText { get; }

    public bool HasUrl { get; }

    public bool CanOpenBackend { get; }
}
