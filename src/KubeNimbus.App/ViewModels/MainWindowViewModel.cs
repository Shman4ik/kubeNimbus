using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Shell root: multi-cluster context tabs (each a <see cref="ClusterTabViewModel"/>
/// with its own connection/sidebar/list/inspector state), the command palette,
/// and workspace persistence (tabs + theme, no credentials — CLAUDE.md rule #4).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ClusterContext> AvailableContexts { get; } = [];

    public ObservableCollection<ClusterTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private ClusterTabViewModel? _selectedTab;

    partial void OnSelectedTabChanged(ClusterTabViewModel? oldValue, ClusterTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(SwitcherLabel));
        OnPropertyChanged(nameof(SwitcherTooltip));

        // The switcher marks the current tab so it isn't offered as the top hit.
        if (Switcher.IsOpen)
        {
            Switcher.Refresh();
        }
    }

    /// <summary>
    /// What the switcher button reads. It names the cluster you are looking at, not
    /// "select a context" — the top bar's job is to answer "which cluster am I in?"
    /// without being asked, which is the single most-cited multi-cluster complaint.
    /// </summary>
    public string SwitcherLabel => SelectedTab?.Header ?? (HasContexts ? "Select a cluster" : "No clusters");

    public string SwitcherTooltip => HasContexts
        ? $"Switch or open a cluster  ({Hotkeys.Describe(Hotkeys.ClusterSwitcher)})"
        : "No kubeconfig contexts found";

    [ObservableProperty]
    private string _status = "Loading kubeconfig…";

    /// <summary>
    /// False when the kubeconfig search turned up nothing. The empty state has to
    /// explain that case rather than offering an "Open a tab" button that can't
    /// do anything (UI rule 8) — with no kubeconfig there is no context to open.
    /// </summary>
    [ObservableProperty]
    private bool _hasContexts;

    /// <summary>Where the search looked, listed so a miss is diagnosable without a debugger.</summary>
    [ObservableProperty]
    private string _kubeconfigSearchPaths = "";

    public CommandPaletteViewModel Palette { get; }

    /// <summary>
    /// The cluster switcher popup that replaced the top bar's context ComboBox —
    /// see <see cref="ClusterSwitcherViewModel"/> for why a dropdown was the wrong
    /// primitive here.
    /// </summary>
    public ClusterSwitcherViewModel Switcher { get; }

    /// <summary>Pinned context names, in user order. Persisted.</summary>
    private readonly List<string> _pinned = [];

    /// <summary>Recently opened context names, newest first. Persisted.</summary>
    private readonly List<string> _recent = [];

    /// <summary>Context name → user-assigned environment, overriding the name guess. Persisted.</summary>
    private readonly Dictionary<string, ClusterEnvironment> _environmentOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// The one global "advanced view" switch — persisted, default off. Off hides the
    /// controls only a fraction of sessions need (usage columns, the fleet toggle, the
    /// log toolbar's wrap/copy/download, exec's Send, YAML force-apply, the sidebar's
    /// count badges, the Helm/RBAC palette entries); on restores the full surface. One
    /// boolean rather than a page of them, because the complaint it answers is about
    /// the whole surface, not any one control.
    ///
    /// Every cluster tab carries a mirror of this (see
    /// <see cref="ClusterTabViewModel.IsAdvancedView"/>) so the list and sidebar can
    /// bind it with compiled bindings against their own DataContext; this property is
    /// the shell's copy, for the top bar and the palette.
    ///
    /// Bind two-way, and never alongside a toggling <c>Command</c> on the same
    /// control — see the note on the tab's copy for why that combination silently
    /// does nothing.
    /// </summary>
    [ObservableProperty]
    private bool _isAdvancedView;

    partial void OnIsAdvancedViewChanged(bool value)
    {
        PersistAdvancedView(value);

        // Broadcast, like RefreshFleetMembership: tabs already open have to follow the
        // switch live. Assigning an unchanged bool raises nothing, so the tab that
        // originated the toggle (via AdvancedViewChanged) doesn't echo back here.
        foreach (var tab in Tabs)
        {
            tab.IsAdvancedView = value;
        }
    }

    /// <summary>
    /// Sets the advanced view to an explicit value. Deliberately not an inverting
    /// "toggle" command: a <c>ToggleButton</c> flips its own <c>IsChecked</c> — and
    /// therefore the two-way-bound property — in <c>OnClick()</c> *before* its
    /// <c>Command</c> runs, so an inverting command bound next to <c>IsChecked</c>
    /// lands back where it started. A control that knows the value it wants can't
    /// hit that.
    /// </summary>
    [RelayCommand]
    private void SetAdvancedView(bool value) => IsAdvancedView = value;

    [ObservableProperty]
    private bool _isShortcutsOpen;

    [RelayCommand]
    private void ToggleShortcuts() => IsShortcutsOpen = !IsShortcutsOpen;

    [RelayCommand]
    private void CloseShortcuts() => IsShortcutsOpen = false;

    public MainWindowViewModel()
    {
        Palette = new CommandPaletteViewModel(BuildPaletteItems);
        Switcher = new ClusterSwitcherViewModel(BuildSwitcherItems) { Activate = ActivateSwitcherItem };

        LoadPreferences();

        // Stamp the environment on every tab that enters the strip, wherever it came
        // from. Doing it here rather than in AddTabAsync means a tab built outside the
        // normal path — the screenshot harness does exactly that — still carries its
        // colour, and there is one place where the override is applied.
        Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<ClusterTabViewModel>() ?? [])
            {
                tab.Environment = EnvironmentFor(tab.Context);

                // Same seam, same reason: the advanced view is global, so a tab from
                // anywhere — including one the screenshot harness built by hand —
                // has to arrive carrying it. Value first, then the write-back, so
                // stamping never round-trips through the shell.
                tab.IsAdvancedView = IsAdvancedView;
                tab.AdvancedViewChanged = value => IsAdvancedView = value;
            }
        };

        _ = InitializeAsync();
    }

    /// <summary>
    /// Reads the parts of the workspace that aren't tabs — pins, recents and
    /// environment overrides. Separate from <see cref="RestoreWorkspaceAsync"/>
    /// because these have to be in place *before* the first tab opens: the
    /// environment colour is read as each tab is constructed.
    /// </summary>
    private void LoadPreferences()
    {
        var settings = WorkspaceStore.Load();

        _pinned.Clear();
        _pinned.AddRange(settings.PinnedContexts ?? []);

        _recent.Clear();
        _recent.AddRange(settings.RecentContexts ?? []);

        // Straight to the backing field: this runs during construction, before any
        // binding or tab exists, and going through the property would only persist
        // the value that was just read back over itself.
        _isAdvancedView = settings.IsAdvancedView ?? false;

        _environmentOverrides.Clear();
        foreach (var (name, value) in settings.EnvironmentOverrides ?? [])
        {
            // An unparseable value means a hand-edited or newer file; drop it rather
            // than throwing away the whole workspace.
            if (Enum.TryParse<ClusterEnvironment>(value, ignoreCase: true, out var environment))
            {
                _environmentOverrides[name] = environment;
            }
        }
    }

    /// <summary>
    /// The environment a context is treated as: the user's assignment if there is
    /// one, otherwise the name guess. Everything that colours a cluster goes
    /// through here so the override applies uniformly.
    /// </summary>
    public ClusterEnvironment EnvironmentFor(ClusterContext context) =>
        _environmentOverrides.TryGetValue(context.Name, out var assigned)
            ? assigned
            : ClusterEnvironments.Classify(context.Name, context.ClusterName);

    public bool IsEnvironmentAssigned(ClusterContext context) => _environmentOverrides.ContainsKey(context.Name);

    /// <summary>
    /// Assigns (or, with null, clears back to the guess) a context's environment.
    /// Applied to every open tab on that context immediately — the colour is a
    /// safety signal, and a stale one is worse than none.
    /// </summary>
    public void SetEnvironment(ClusterContext context, ClusterEnvironment? environment)
    {
        if (environment is { } value)
        {
            _environmentOverrides[context.Name] = value;
        }
        else
        {
            _environmentOverrides.Remove(context.Name);
        }

        foreach (var tab in Tabs.Where(t => t.Context.Name == context.Name))
        {
            tab.Environment = EnvironmentFor(tab.Context);
        }

        SaveWorkspace();
        Switcher.Refresh();
    }

    public bool IsPinned(string contextName) => _pinned.Contains(contextName, StringComparer.Ordinal);

    public void SetPinned(string contextName, bool pinned)
    {
        if (pinned && !IsPinned(contextName))
        {
            _pinned.Add(contextName);
        }
        else if (!pinned)
        {
            _pinned.RemoveAll(n => string.Equals(n, contextName, StringComparison.Ordinal));
        }

        SaveWorkspace();
        Switcher.Refresh();
    }

    private void RecordRecent(string contextName)
    {
        _recent.RemoveAll(n => string.Equals(n, contextName, StringComparison.Ordinal));
        _recent.Insert(0, contextName);
        if (_recent.Count > WorkspaceStore.MaxRecentContexts)
        {
            _recent.RemoveRange(WorkspaceStore.MaxRecentContexts, _recent.Count - WorkspaceStore.MaxRecentContexts);
        }
    }

    /// <summary>
    /// Rows for the switcher, bucketed. A context that is already open appears
    /// only under "Open" — the same cluster listed twice is the confusion the old
    /// two-control arrangement created in the first place.
    /// </summary>
    private IEnumerable<ClusterSwitcherItemViewModel> BuildSwitcherItems()
    {
        var openByName = Tabs.ToLookup(t => t.Context.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tab in Tabs)
        {
            yield return new ClusterSwitcherItemViewModel(
                tab.Context, ClusterSwitcherGroup.Open, EnvironmentFor(tab.Context), tab,
                IsPinned(tab.Context.Name), ReferenceEquals(tab, SelectedTab));
            seen.Add(tab.Context.Name);
        }

        foreach (var context in AvailableContexts)
        {
            if (openByName.Contains(context.Name) || !seen.Add(context.Name))
            {
                continue;
            }

            var group = IsPinned(context.Name) ? ClusterSwitcherGroup.Pinned
                : _recent.Contains(context.Name, StringComparer.Ordinal) ? ClusterSwitcherGroup.Recent
                : ClusterSwitcherGroup.All;

            yield return new ClusterSwitcherItemViewModel(
                context, group, EnvironmentFor(context), openTab: null, IsPinned(context.Name), isCurrent: false);
        }
    }

    private void ActivateSwitcherItem(ClusterSwitcherItemViewModel item)
    {
        if (item.OpenTab is { } tab)
        {
            SelectedTab = tab;
        }
        else
        {
            _ = AddTabAsync(item.Context);
        }
    }

    [RelayCommand]
    private void OpenSwitcher() => Switcher.Open();

    /// <summary>
    /// Jump to the nth open tab (Ctrl/Cmd+1…9), the gesture every tabbed app has
    /// and the fastest path once you know where a cluster sits in the strip.
    /// 9 means "last", matching browser convention.
    /// </summary>
    public void SelectTabByOrdinal(int ordinal)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        SelectedTab = ordinal >= 9 ? Tabs[^1] : Tabs[Math.Min(ordinal - 1, Tabs.Count - 1)];
    }

    private async Task InitializeAsync()
    {
        if (await LoadContextsAsync())
        {
            await RestoreWorkspaceAsync();
        }
    }

    /// <summary>
    /// (Re)reads the kubeconfig chain. Separate from <see cref="InitializeAsync"/> so
    /// the empty state can offer a rescan: dropping a file into ~/.kube/config while
    /// the app is open is a normal first-run flow, and it shouldn't need a restart.
    /// Returns false when the read failed outright.
    /// </summary>
    private async Task<bool> LoadContextsAsync()
    {
        try
        {
            var contexts = await Kubeconfig.LoadContextsAsync();
            AvailableContexts.Clear();
            foreach (var ctx in contexts)
            {
                AvailableContexts.Add(ctx);
            }

            HasContexts = AvailableContexts.Count > 0;
            KubeconfigSearchPaths = string.Join(
                Environment.NewLine,
                Kubeconfig.CandidatePaths().Select(c =>
                    $"{(c.Exists ? "found  " : "missing")}  {c.Path}   ({c.Source})"));
            Status = HasContexts
                ? $"{AvailableContexts.Count} context(s) available."
                : "No kubeconfig contexts found.";
            AddNewTabCommand.NotifyCanExecuteChanged();
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Failed to read kubeconfig: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private async Task ReloadContextsAsync()
    {
        if (await LoadContextsAsync() && Tabs.Count == 0 && AvailableContexts.Count > 0)
        {
            await AddTabAsync(AvailableContexts[0]);
        }
    }

    private async Task RestoreWorkspaceAsync()
    {
        var settings = WorkspaceStore.Load();
        foreach (var snapshot in settings.Tabs)
        {
            var match = AvailableContexts.FirstOrDefault(c =>
                c.Name == snapshot.ContextName && c.KubeconfigPath == snapshot.KubeconfigPath);
            if (match is not null)
            {
                await AddTabAsync(match);
            }
        }

        if (Tabs.Count == 0 && AvailableContexts.Count > 0)
        {
            await AddTabAsync(AvailableContexts[0]);
        }
    }

    /// <summary>
    /// "Add a cluster" now means "open the switcher" rather than "open whatever the
    /// dropdown happens to be showing" — the picking happens in the searchable
    /// popup, where a long context list is actually navigable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddNewTab))]
    private void AddNewTab() => Switcher.Open();

    private bool CanAddNewTab() => HasContexts;

    partial void OnHasContextsChanged(bool value)
    {
        AddNewTabCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SwitcherLabel));
        OnPropertyChanged(nameof(SwitcherTooltip));
    }

    private async Task AddTabAsync(ClusterContext context)
    {
        var tab = new ClusterTabViewModel(context) { FleetMembersProvider = FleetMembers };
        Tabs.Add(tab);
        SelectedTab = tab;
        RecordRecent(context.Name);
        SaveWorkspace();
        await tab.ConnectCommand.ExecuteAsync(null);
        RefreshFleetMembership();
    }

    /// <summary>
    /// Every connected cluster, for the aggregated fleet views. Names are made unique
    /// because fleet row keys are built from them — two tabs on the same context would
    /// otherwise merge into one apparent cluster.
    /// </summary>
    private IReadOnlyList<FleetMember> FleetMembers()
    {
        var members = new List<FleetMember>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in Tabs)
        {
            if (tab.Client is not { } client)
            {
                continue;
            }

            var name = tab.Header;
            var suffix = 2;
            while (!used.Add(name))
            {
                name = $"{tab.Header} ({suffix++})";
            }

            members.Add(new FleetMember(name, client));
        }

        return members;
    }

    /// <summary>
    /// Re-offers (or withdraws) the fleet toggle and re-fans any active aggregated
    /// watch after the set of connected clusters changes. A fleet of one is just the
    /// tab you're already looking at, so the toggle disappears below two clusters —
    /// and any tab left in fleet mode drops back to its own cluster rather than
    /// holding a watch on a client that has been disposed.
    /// </summary>
    private void RefreshFleetMembership()
    {
        var connected = Tabs.Count(t => t.Client is not null);
        foreach (var tab in Tabs)
        {
            tab.IsFleetViewAvailable = connected > 1;
            if (connected <= 1)
            {
                tab.IsFleetView = false;
            }
            else
            {
                tab.RefreshFleetMembership();
            }
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(ClusterTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        RefreshFleetMembership();
        await tab.DisposeAsync();
        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        SaveWorkspace();
    }

    /// <summary>Drag-reorder support — called from the view's drag/drop handler.</summary>
    public void MoveTab(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Tabs.Count || newIndex >= Tabs.Count)
        {
            return;
        }

        Tabs.Move(oldIndex, newIndex);
        SaveWorkspace();
    }

    private void SaveWorkspace()
    {
        var settings = WorkspaceStore.Load();
        var tabs = Tabs.Select(t => new TabSnapshot(t.Context.Name, t.Context.KubeconfigPath)).ToList();
        WorkspaceStore.Save(settings with
        {
            Tabs = tabs,
            PinnedContexts = [.. _pinned],
            RecentContexts = [.. _recent],
            EnvironmentOverrides = _environmentOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
        });
    }

    public void PersistTheme(string? theme)
    {
        var settings = WorkspaceStore.Load();
        WorkspaceStore.Save(settings with { Theme = theme });
    }

    /// <summary>
    /// Read-modify-write, same shape as <see cref="PersistTheme"/> — the workspace
    /// file also holds tabs, pins, recents and environment overrides, and rewriting
    /// it from this view model's fields alone would drop whatever another write
    /// landed in the meantime.
    /// </summary>
    private static void PersistAdvancedView(bool value)
    {
        var settings = WorkspaceStore.Load();
        WorkspaceStore.Save(settings with { IsAdvancedView = value });
    }

    private IEnumerable<PaletteItem> BuildPaletteItems()
    {
        // The switcher, not the palette, is where a large context list is navigated:
        // enumerating every kubeconfig context here would bury every other command
        // under hundreds of "open cluster" rows on a real estate. Open tabs stay,
        // because there are only ever a handful and they're a genuine destination.
        yield return new PaletteItem(
            "Switch cluster…", $"{AvailableContexts.Count} contexts · {Hotkeys.Describe(Hotkeys.ClusterSwitcher)}",
            "SwapHorizontalIconGeometry", Switcher.Open);

        foreach (var tab in Tabs)
        {
            yield return new PaletteItem(
                $"Switch to {tab.Header}",
                tab.Environment.Label() is { } env ? $"Cluster tab · {env}" : "Cluster tab",
                "SwapHorizontalIconGeometry", () => SelectedTab = tab);
        }

        // The switch's own entry, and the reason hiding controls by default is safe:
        // everything the advanced view hides is one Ctrl/Cmd+K away, and the entry
        // states what it does rather than naming a mode nobody has seen yet. The
        // target value is captured now, not inverted when the action runs, so it can
        // never race whatever else has touched the flag since the palette opened.
        var advancedTarget = !IsAdvancedView;
        yield return new PaletteItem(
            advancedTarget ? "Advanced view: show every control" : "Advanced view: hide advanced controls",
            advancedTarget
                ? "Usage columns, fleet view, log tools, force-apply, Helm & RBAC"
                : "Back to the minimal layout",
            "TuneIconGeometry",
            () => IsAdvancedView = advancedTarget);

        // Gated on the toggle's own visibility rather than on IsFleetViewAvailable, so
        // the palette offers exactly what the command bar does — including the way out
        // of an aggregation left running when the advanced view was switched off.
        if (SelectedTab is { IsFleetToggleVisible: true } fleetable)
        {
            var fleetTarget = !fleetable.IsFleetView;
            yield return new PaletteItem(
                fleetTarget ? "Fleet view: aggregate across all clusters" : "Fleet view: back to this cluster only",
                $"{Tabs.Count(t => t.Client is not null)} connected clusters", "LayersIconGeometry",
                () => fleetable.IsFleetView = fleetTarget);
        }

        // Access review is a deliberate errand, not something you stumble into — it
        // rides the advanced view along with the rest of the specialist surface.
        if (IsAdvancedView && SelectedTab is { IsConnected: true } connected)
        {
            yield return new PaletteItem(
                "Access review — my permissions",
                $"RBAC · {connected.SelectedNamespace}", "AccountMultipleIconGeometry",
                () => connected.OpenAccessReviewCommand.Execute(null));

            yield return new PaletteItem(
                "Access review — who can do X?",
                "RBAC · scan every subject", "AccountMultipleIconGeometry",
                () => connected.OpenWhoCanCommand.Execute(null));

            if (connected.SelectedRowAsSubject is { } subject)
            {
                yield return new PaletteItem(
                    $"Access review: {subject.Name}",
                    "RBAC · ServiceAccount bindings", "AccountMultipleIconGeometry",
                    () => connected.OpenAccessReviewCommand.Execute(subject));
            }
        }

        if (SelectedTab is { } current)
        {
            foreach (var section in current.SidebarSections)
            {
                // Helm reaches the palette through the sidebar catalog like every
                // other kind, so this is where its entry is gated. The sidebar
                // section itself stays — it only exists on clusters that actually
                // store releases, which is already the "is this worth showing?"
                // test UI rule 1 asks for.
                if (!IsAdvancedView && section.Title == SidebarGrouping.HelmSection)
                {
                    continue;
                }

                foreach (var kind in section.Kinds)
                {
                    // Same-named kinds from different API groups carry their group
                    // here too, or the palette shows two identical-looking entries.
                    var subtitle = kind.HasGroupLabel
                        ? $"{section.Title} · {kind.GroupLabel} · {current.Header}"
                        : $"{section.Title} · {current.Header}";
                    yield return new PaletteItem(kind.DisplayName, subtitle, section.IconKey,
                        () => current.SelectKindCommand.Execute(kind));
                }
            }
        }
    }
}
