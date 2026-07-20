using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One connected cluster (one kubeconfig context) — owns its ClusterClient,
/// discovery-built sidebar, the live list for whichever resource kind/namespace
/// is selected, and the inspector tab strip (pod detail / YAML / exec /
/// port-forward). Tabs are multi-cluster because there's one of these per tab.
/// </summary>
public sealed partial class ClusterTabViewModel : ObservableObject, IAsyncDisposable
{
    public const string AllNamespaces = "All namespaces";

    private readonly Dictionary<string, ResourceRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private CancellationTokenSource? _watchCts;

    public ClusterContext Context { get; }

    public string Header => Context.Name;

    public ClusterClient? Client { get; private set; }

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = "Not connected.";

    [ObservableProperty]
    private string? _connectionWarning;

    public ObservableCollection<SidebarSectionViewModel> SidebarSections { get; } = [];

    [ObservableProperty]
    private string _sidebarFilter = "";

    public ObservableCollection<string> NamespaceOptions { get; } = [AllNamespaces];

    [ObservableProperty]
    private string _selectedNamespace = AllNamespaces;

    [ObservableProperty]
    private SidebarKindViewModel? _selectedKind;

    public ObservableCollection<ResourceRowViewModel> Rows { get; } = [];

    /// <summary>True from the moment a watch (re)starts until its first event
    /// arrives — distinguishes "still loading" from "genuinely empty" so the
    /// list doesn't flash an empty state while the initial list is in flight.</summary>
    [ObservableProperty]
    private bool _isListLoading;

    /// <summary>True once the list has genuinely settled on zero rows (not
    /// merely mid-load) — drives the "No <kind> found" empty state.</summary>
    [ObservableProperty]
    private bool _isListEmpty;

    partial void OnIsListLoadingChanged(bool value) => RecomputeListEmpty();

    private void RecomputeListEmpty() => IsListEmpty = Rows.Count == 0 && !IsListLoading;

    [ObservableProperty]
    private ResourceRowViewModel? _selectedRow;

    public ObservableCollection<InspectorTabViewModelBase> InspectorTabs { get; } = [];

    [ObservableProperty]
    private InspectorTabViewModelBase? _selectedInspectorTab;

    partial void OnSelectedInspectorTabChanged(InspectorTabViewModelBase? oldValue, InspectorTabViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
        }
    }

    /// <summary>Expands the inspector to fill the content area (list hidden) — the
    /// fixed ~440px sidecar is too cramped for YAML editing or an exec terminal.</summary>
    [ObservableProperty]
    private bool _isInspectorMaximized;

    [RelayCommand]
    private void ToggleInspectorMaximized() => IsInspectorMaximized = !IsInspectorMaximized;

    public ClusterTabViewModel(ClusterContext context)
    {
        Context = context;
    }

    private bool CanConnect => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsConnecting = true;
        ConnectionWarning = null;
        Status = $"Connecting to {Context.Name}…";
        try
        {
            var client = ClusterClient.Connect(Context);
            var version = await client.GetServerVersionAsync();
            Client = client;
            IsConnected = true;
            Status = $"Connected — Kubernetes {version.GitVersion}.";

            await BuildSidebarAsync();
            await RefreshNamespacesAsync();

            var defaultKind = SidebarSections
                .FirstOrDefault(s => s.Title == "Workloads")?.Kinds
                .FirstOrDefault(k => k.Descriptor.Kind == "Pod")
                ?? SidebarSections.SelectMany(s => s.Kinds).FirstOrDefault();
            if (defaultKind is not null)
            {
                SelectKind(defaultKind);
            }
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task BuildSidebarAsync()
    {
        if (Client is null)
        {
            return;
        }

        var catalog = await Client.GetResourceCatalogAsync();
        var sections = new Dictionary<string, SidebarSectionViewModel>(StringComparer.Ordinal);
        foreach (var title in SidebarGrouping.SectionOrder)
        {
            sections[title] = new SidebarSectionViewModel(title);
        }

        foreach (var descriptor in catalog.OrderBy(d => d.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var title = SidebarGrouping.SectionFor(descriptor);
            sections[title].Kinds.Add(new SidebarKindViewModel(descriptor, SidebarGrouping.IconKeyFor(title)));
        }

        SidebarSections.Clear();
        foreach (var title in SidebarGrouping.SectionOrder)
        {
            if (sections[title].Kinds.Count > 0)
            {
                SidebarSections.Add(sections[title]);
            }
        }

        ApplySidebarFilter();
    }

    partial void OnSidebarFilterChanged(string value) => ApplySidebarFilter();

    [RelayCommand]
    private void ClearSidebarFilter() => SidebarFilter = "";

    /// <summary>
    /// Filters sidebar kinds by substring match on display name, live as the user
    /// types. A section with at least one match force-expands (without touching
    /// the user's own collapse choice, restored once the filter is cleared) so
    /// filtering never hides a result inside a collapsed section.
    /// </summary>
    private void ApplySidebarFilter()
    {
        // The filter TextBox's two-way binding can round-trip null on
        // control (re)creation — same reasoning as SelectedNamespace above.
        var query = (SidebarFilter ?? "").Trim();
        var filtering = query.Length > 0;

        foreach (var section in SidebarSections)
        {
            var anyMatch = false;
            foreach (var kind in section.Kinds)
            {
                var match = !filtering || kind.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
                kind.IsVisible = match;
                anyMatch |= match;
            }

            section.HasVisibleKinds = anyMatch;
            section.IsForceExpanded = filtering && anyMatch;
        }
    }

    [RelayCommand]
    private async Task RefreshNamespacesAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            var namespaces = await Client.ListResourceOnceAsync(ResourceDescriptor.Namespaces);
            var previousSelection = SelectedNamespace;
            NamespaceOptions.Clear();
            NamespaceOptions.Add(AllNamespaces);
            foreach (var ns in namespaces.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                NamespaceOptions.Add(ns.Name);
            }

            // Clearing/repopulating the bound collection can round-trip the ComboBox's
            // SelectedItem through null via the two-way binding; re-assert a valid
            // selection so the selector never ends up showing blank.
            SelectedNamespace = NamespaceOptions.Contains(previousSelection) ? previousSelection : AllNamespaces;
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"Could not list namespaces: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectKind(SidebarKindViewModel kind)
    {
        if (SelectedKind == kind)
        {
            return;
        }

        foreach (var section in SidebarSections)
        {
            foreach (var k in section.Kinds)
            {
                k.IsSelected = k == kind;
            }
        }

        SelectedKind = kind;
        RestartWatch();
    }

    partial void OnSelectedNamespaceChanged(string value) => RestartWatch();

    [RelayCommand]
    private void Refresh() => RestartWatch();

    private void RestartWatch()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;

        Rows.Clear();
        _rowsByKey.Clear();
        IsListLoading = Client is not null && SelectedKind is not null;
        RecomputeListEmpty();

        if (Client is null || SelectedKind is null)
        {
            return;
        }

        var descriptor = SelectedKind.Descriptor;
        var @namespace = descriptor.Namespaced && SelectedNamespace != AllNamespaces ? SelectedNamespace : null;

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        var client = Client;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                    descriptor, @namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() => ConnectionWarning = ex.Message),
                    cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Apply(evt));
                }
            }
            catch (OperationCanceledException)
            {
                // normal when switching kind/namespace or disconnecting
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Status = $"Watch ended: {ex.Message}");
            }
        }, token);
    }

    private void Apply(ResourceEvent<DynamicResource> evt)
    {
        IsListLoading = false;

        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                Rows.Clear();
                _rowsByKey.Clear();
                ConnectionWarning = null;
                break;

            case ResourceEventType.Added or ResourceEventType.Modified when evt.Resource is { } resource:
                if (_rowsByKey.TryGetValue(resource.Key, out var existing))
                {
                    existing.Update(resource);
                }
                else
                {
                    var row = new ResourceRowViewModel(resource);
                    _rowsByKey[resource.Key] = row;
                    Rows.Add(row);
                }

                break;

            case ResourceEventType.Deleted when evt.Resource is { } resource:
                if (_rowsByKey.Remove(resource.Key, out var removed))
                {
                    Rows.Remove(removed);
                }

                break;
        }

        RecomputeListEmpty();
    }

    /// <summary>Double-click / Enter: promotes (or opens) a permanent tab. Pod → detail; anything else → YAML.</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync() => await OpenRowAsync(SelectedRow, preview: false);

    /// <summary>Space: quick-peek — replaces the current preview tab in place.</summary>
    [RelayCommand]
    private async Task PeekSelectedAsync() => await OpenRowAsync(SelectedRow, preview: true);

    private async Task OpenRowAsync(ResourceRowViewModel? row, bool preview)
    {
        if (row is null || Client is null || SelectedKind is null)
        {
            return;
        }

        var key = SelectedKind.Descriptor.Kind == "Pod" ? $"pod:{row.Namespace}/{row.Name}" : $"yaml:{SelectedKind.Descriptor.ApiVersion}/{SelectedKind.Descriptor.Kind}:{row.Namespace}/{row.Name}";
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            SelectedInspectorTab = existing;
            return;
        }

        InspectorTabViewModelBase tab;
        if (SelectedKind.Descriptor.Kind == "Pod")
        {
            tab = new PodDetailTabViewModel(Client, row, AddInspectorTab, OpenOwnerAsync);
        }
        else
        {
            var yaml = row.Resource.ToYaml();
            tab = new YamlEditorTabViewModel(Client, SelectedKind.Descriptor, row.Namespace, row.Name, yaml);
        }

        tab.IsPreview = preview;
        AddInspectorTab(tab, replacePreview: preview);
    }

    private void AddInspectorTab(InspectorTabViewModelBase tab) => AddInspectorTab(tab, replacePreview: tab.IsPreview);

    private void AddInspectorTab(InspectorTabViewModelBase tab, bool replacePreview)
    {
        if (replacePreview)
        {
            var previousPreview = InspectorTabs.FirstOrDefault(t => t.IsPreview);
            if (previousPreview is not null)
            {
                _ = CloseInspectorTabAsync(previousPreview);
            }
        }

        InspectorTabs.Add(tab);
        SelectedInspectorTab = tab;
    }

    /// <summary>Resolves an ownerReference (pod → replicaset → deployment, etc.) and opens its YAML.</summary>
    private async Task OpenOwnerAsync(OwnerRef owner, string? namespaceHint)
    {
        if (Client is null)
        {
            return;
        }

        var resolved = await Client.ResolveOwnerAsync(owner, namespaceHint);
        if (resolved is null)
        {
            ConnectionWarning = $"Owner {owner.Kind}/{owner.Name} could not be resolved (deleted?).";
            return;
        }

        var catalog = await Client.GetResourceCatalogAsync();
        var descriptor = catalog.FirstOrDefault(d =>
            d.ApiVersion == owner.ApiVersion && d.Kind == owner.Kind);
        if (descriptor is null)
        {
            return;
        }

        var key = descriptor.Kind == "Pod"
            ? $"pod:{resolved.Namespace}/{resolved.Name}"
            : $"yaml:{descriptor.ApiVersion}/{descriptor.Kind}:{resolved.Namespace}/{resolved.Name}";
        var existing = InspectorTabs.FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            SelectedInspectorTab = existing;
            return;
        }

        var tab = new YamlEditorTabViewModel(Client, descriptor, resolved.Namespace, resolved.Name, resolved.ToYaml());
        AddInspectorTab(tab, replacePreview: false);
    }

    [RelayCommand]
    private void SelectInspectorTab(InspectorTabViewModelBase tab) => SelectedInspectorTab = tab;

    [RelayCommand]
    private async Task CloseInspectorTabAsync(InspectorTabViewModelBase tab)
    {
        await tab.OnClosingAsync();
        var index = InspectorTabs.IndexOf(tab);
        InspectorTabs.Remove(tab);
        if (SelectedInspectorTab == tab)
        {
            SelectedInspectorTab = InspectorTabs.Count == 0
                ? null
                : InspectorTabs[Math.Min(index, InspectorTabs.Count - 1)];
        }

        if (InspectorTabs.Count == 0)
        {
            IsInspectorMaximized = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watchCts is not null)
        {
            await _watchCts.CancelAsync();
            _watchCts.Dispose();
        }

        foreach (var tab in InspectorTabs.ToArray())
        {
            await tab.OnClosingAsync();
        }

        Client?.Dispose();
    }
}
