using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;
using KubeNimbus.Core.Commands;
using KubeNimbus.Core.Settings;

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
        : $"No kubeconfig contexts — the demo cluster is still in here  ({Hotkeys.Describe(Hotkeys.ClusterSwitcher)})";

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
    /// Kubeconfig files chosen through <see cref="OpenKubeconfigFileCommand"/>, searched
    /// alongside $KUBECONFIG and ~/.kube/config. Persisted as paths and nothing else —
    /// the file is re-read through <c>Kubeconfig</c> on every load and every connect, so
    /// no credential is ever copied into app storage (CLAUDE.md rule #4).
    ///
    /// This exists because neither of the other two routes is reachable for the audience
    /// that most needs one: $KUBECONFIG isn't inherited by a GUI launched from Explorer,
    /// a shortcut or the Store, and "drop a file at ~/.kube/config" is not an instruction
    /// anyone can follow from inside the app.
    /// </summary>
    private readonly List<string> _pickedKubeconfigPaths = [];

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

    /// <summary>
    /// Whether the cluster tab's resource-catalog sidebar is shown. Shell-owned and
    /// mirrored onto every tab, exactly like <see cref="IsAdvancedView"/> — the
    /// sidebar lives inside <c>ClusterTabView</c>, but the control that hides it is in
    /// the command bar, and the choice is global rather than per-tab (hiding it on one
    /// cluster and not the next would read as a bug).
    ///
    /// <para>
    /// Bound one-way from the toggle's <c>IsChecked</c> plus an explicit
    /// <see cref="SetSidebarVisibleCommand"/> target value — never an inverting command
    /// beside a two-way binding, which is the double-toggle no-op this repo has shipped
    /// three times (UI rule 8b).
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarVisible = true;

    partial void OnIsSidebarVisibleChanged(bool value)
    {
        App.Update(s => s with { IsSidebarVisible = value });

        foreach (var tab in Tabs)
        {
            tab.IsSidebarVisible = value;
        }
    }

    /// <summary>
    /// Sets the sidebar's visibility to an explicit value, for the palette entry.
    /// Explicit rather than inverting, for the reason on <see cref="SetAdvancedView"/>.
    /// </summary>
    [RelayCommand]
    private void SetSidebarVisible(bool value) => IsSidebarVisible = value;

    /// <summary>
    /// Opens the preferences page. One instance, because every control on it applies
    /// immediately and a second copy would be two views of the same live state racing
    /// each other's writes — which the single overlay gives for free.
    /// </summary>
    [RelayCommand]
    private void ShowPreferences() => IsPreferencesOpen = true;

    /// <summary>
    /// The preferences page's own view model, built on first open and torn down when
    /// the overlay closes. Held rather than rebuilt per open so the page keeps its
    /// scroll position and its kubeconfig-list selection across a dismiss.
    /// </summary>
    [ObservableProperty]
    private PreferencesViewModel? _preferences;

    [ObservableProperty]
    private bool _isPreferencesOpen;

    /// <summary>
    /// The page subscribes to this view model's <c>PropertyChanged</c> to mirror the
    /// settings the shell owns, so an open page is a live listener. Closing it has to
    /// <see cref="PreferencesViewModel.Detach"/>, or every dismissed page stays
    /// subscribed for the life of the window.
    /// </summary>
    partial void OnIsPreferencesOpenChanged(bool value)
    {
        if (value)
        {
            Preferences ??= new PreferencesViewModel(this);
            return;
        }

        Preferences?.Detach();
        Preferences = null;
    }

    /// <summary>Opens the About box.</summary>
    [RelayCommand]
    private void ShowAbout() => IsAboutOpen = true;

    [ObservableProperty]
    private bool _isAboutOpen;

    /// <summary>
    /// The F1 cheat sheet's rows, projected from <see cref="Core.Commands.CommandCatalog"/>.
    /// Rebuilt when the Ctrl/Cmd scheme changes: the key caps spell the modifier out, so
    /// a sheet built once would keep showing the other platform's chord.
    /// </summary>
    [ObservableProperty]
    private ShortcutsViewModel _shortcuts = new();

    [ObservableProperty]
    private bool _isShortcutsOpen;

    [RelayCommand]
    private void ToggleShortcuts() => IsShortcutsOpen = !IsShortcutsOpen;

    /// <summary>
    /// Toggles the sidebar, for the keyboard binding. Inverting is safe here and
    /// nowhere else: this is reached from a <c>KeyBinding</c>, never from the command
    /// bar's <c>ToggleButton</c> — that one uses a two-way <c>IsChecked</c> alone, and
    /// wiring both to the same control is the double-toggle no-op of UI rule 8b.
    /// </summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public MainWindowViewModel()
    {
        Palette = new CommandPaletteViewModel(BuildPaletteItems);
        Switcher = new ClusterSwitcherViewModel(BuildSwitcherItems) { Activate = ActivateSwitcherItem };

        LoadPreferences();

        // The sheet spells out Ctrl or Cmd on every cap, so it has to be rebuilt when
        // the preference changes rather than showing the other platform's chords until
        // restart. The window rebuilds its key bindings off the same event.
        Hotkeys.Changed += () => Shortcuts = new ShortcutsViewModel();

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
                tab.IsSidebarVisible = IsSidebarVisible;
            }
        };

        _ = InitializeAsync();
    }

    /// <summary>
    /// Reads what has to exist before the first tab opens: the session state that
    /// isn't tabs (pins, recents, environment overrides — the environment colour is
    /// read as each tab is constructed) and the preferences the shell mirrors onto
    /// every tab. Two files, because they answer different questions — see
    /// <see cref="AppSettings"/> — and one call site, because both are needed at the
    /// same moment.
    /// </summary>
    private void LoadPreferences()
    {
        var settings = WorkspaceStore.Load();
        var preferences = App.LoadSettings();

        _pinned.Clear();
        _pinned.AddRange(settings.PinnedContexts ?? []);

        _recent.Clear();
        _recent.AddRange(settings.RecentContexts ?? []);

        _pickedKubeconfigPaths.Clear();
        _pickedKubeconfigPaths.AddRange(preferences.KubeconfigPaths);

        // Straight to the backing fields: this runs during construction, before any
        // binding or tab exists, and going through the properties would only persist
        // the values that were just read back over themselves. MVVMTK0034 is the
        // analyzer asking "did you mean the property?" — here, no.
#pragma warning disable MVVMTK0034
        _isAdvancedView = preferences.IsAdvancedView;
        _isSidebarVisible = preferences.IsSidebarVisible;
#pragma warning restore MVVMTK0034

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

        // Its own group, last and labelled, so nobody reaches for it thinking it is one
        // of their clusters — and so it is still findable on a machine that has none.
        if (!seen.Contains(ClusterContext.Demo.Name))
        {
            yield return new ClusterSwitcherItemViewModel(
                ClusterContext.Demo, ClusterSwitcherGroup.Demo, EnvironmentFor(ClusterContext.Demo),
                openTab: null, isPinned: false, isCurrent: false);
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
            var contexts = await Kubeconfig.LoadContextsAsync(extraPaths: _pickedKubeconfigPaths);
            AvailableContexts.Clear();
            foreach (var ctx in contexts)
            {
                AvailableContexts.Add(ctx);
            }

            HasContexts = AvailableContexts.Count > 0;
            RefreshSearchPaths();
            Status = HasContexts
                ? $"{AvailableContexts.Count} context(s) available."
                : "No kubeconfig contexts found.";
            AddNewTabCommand.NotifyCanExecuteChanged();
            return true;
        }
        catch (Exception ex)
        {
            // Still refresh the search list: a file that failed to parse is exactly
            // when "here is what was read" matters, and leaving the previous list on
            // screen would name the wrong file (UI rule 9).
            RefreshSearchPaths();
            Status = $"Failed to read kubeconfig: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the empty state's "Searched:" list. Picked files carry their own
    /// source label, so a config the user chose is distinguishable from one the app
    /// found — and a picked file that has since been moved shows as <c>missing</c>
    /// instead of vanishing without explanation.
    /// </summary>
    private void RefreshSearchPaths() =>
        KubeconfigSearchPaths = string.Join(
            Environment.NewLine,
            Kubeconfig.CandidatePaths(_pickedKubeconfigPaths).Select(c =>
                $"{(c.Exists ? "found  " : "missing")}  {c.Path}   ({c.Source})"));

    [RelayCommand]
    private async Task ReloadContextsAsync()
    {
        if (await LoadContextsAsync() && Tabs.Count == 0 && AvailableContexts.Count > 0)
        {
            await AddTabAsync(AvailableContexts[0]);
        }
    }

    /// <summary>
    /// "Open kubeconfig file…" — the only route to a cluster that works from inside the
    /// app. $KUBECONFIG is not inherited by a GUI launched from Explorer, a shortcut or
    /// the Microsoft Store, and "put a file at ~/.kube/config" is an instruction nobody
    /// can act on without leaving the app, so a first run on a clean machine had no
    /// reachable next step at all.
    ///
    /// The file is never copied or read into app storage: only its path is kept, and
    /// every load and every connect re-resolves it through the same kubeconfig chain
    /// as any other file (CLAUDE.md rule #4).
    /// </summary>
    [RelayCommand]
    private async Task OpenKubeconfigFileAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open kubeconfig file",
            AllowMultiple = false,
            // Kubeconfig files are as often extensionless ("config") as .yaml, so an
            // extension filter alone would hide the single most likely file.
            FileTypeFilter =
            [
                new FilePickerFileType("kubeconfig") { Patterns = ["config", "*.yaml", "*.yml", "*.conf", "kubeconfig*"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }

        // Try it before remembering it. A file that turns out not to be a kubeconfig
        // would otherwise be persisted and re-break every subsequent start, with the
        // rescan button rerunning the same failure.
        var previous = _pickedKubeconfigPaths.ToArray();
        _pickedKubeconfigPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _pickedKubeconfigPaths.Insert(0, path);

        if (!await LoadContextsAsync() || !HasContexts)
        {
            _pickedKubeconfigPaths.Clear();
            _pickedKubeconfigPaths.AddRange(previous);
            await LoadContextsAsync();
            Status = $"No clusters in {Path.GetFileName(path)} — it has no contexts, or it isn't a kubeconfig file.";
            return;
        }

        SaveWorkspace();

        // Same follow-through as a rescan that finds something: opening the file and
        // then being left on the empty state would read as the pick not having worked.
        if (Tabs.Count == 0)
        {
            await AddTabAsync(AvailableContexts[0]);
        }
    }

    /// <summary>
    /// Forgets a picked kubeconfig path and rescans, for the preferences page's list.
    /// Only the app's memory of the path goes — the file is not touched, which matters
    /// on a page listing the files that reach someone's production clusters.
    ///
    /// <para>
    /// Open tabs are deliberately left alone. A tab holds a live, already-resolved
    /// connection; closing clusters as a side effect of tidying a path list would be a
    /// far bigger action than the one asked for, and the tab simply will not come back
    /// on the next restore.
    /// </para>
    /// </summary>
    public async Task ForgetKubeconfigPathAsync(string path)
    {
        if (_pickedKubeconfigPaths.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        SaveWorkspace();
        await LoadContextsAsync();
    }

    private async Task RestoreWorkspaceAsync()
    {
        var settings = WorkspaceStore.Load();
        foreach (var snapshot in settings.Tabs)
        {
            // The demo cluster is not a kubeconfig context, so it is never in
            // AvailableContexts and the name+path match below can't find it. The
            // sentinel path is what identifies it — that is the whole reason it is a
            // path rather than a new field on TabSnapshot.
            if (snapshot.KubeconfigPath == ClusterContext.DemoKubeconfigPath)
            {
                await AddTabAsync(ClusterContext.Demo);
                continue;
            }

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

    /// <summary>
    /// Always true since the demo cluster exists. It used to be <c>HasContexts</c>,
    /// which was right when an empty kubeconfig meant an empty switcher — but the
    /// switcher now always carries at least the demo row, and gating on contexts made
    /// the top bar's cluster button dead on exactly the machine where the demo cluster
    /// is the only thing to reach. UI rule 9 asks for a command that *cannot* run to be
    /// disabled; this one can.
    /// </summary>
    private static bool CanAddNewTab() => true;

    /// <summary>
    /// Opens (or switches to) the built-in demo cluster. Deliberately <b>not</b> gated
    /// on <see cref="HasContexts"/>: no kubeconfig is precisely when this is the only
    /// thing on screen worth clicking, and it is the button a Microsoft Store reviewer
    /// on a clean machine presses to see the app do anything at all.
    /// </summary>
    [RelayCommand]
    private async Task OpenDemoClusterAsync()
    {
        if (Tabs.FirstOrDefault(t => t.IsDemo) is { } existing)
        {
            SelectedTab = existing;
            return;
        }

        await AddTabAsync(ClusterContext.Demo);
    }

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

        // The picked kubeconfig paths are a preference, not session state — they are
        // what the app should look at next launch regardless of which tabs were open —
        // so they live in settings.json and are written alongside rather than into the
        // workspace. Same call, because every gesture that changes one is a gesture
        // that saves the workspace anyway.
        App.Update(s => s with { KubeconfigPaths = [.. _pickedKubeconfigPaths] });
    }

    /// <summary>
    /// Persists a theme chosen from the top bar's light/dark toggle. Goes through
    /// <see cref="App.SetTheme"/> so the toggle and the preferences page write the
    /// same setting in the same spelling — they used to disagree, because the toggle
    /// wrote ThemeVariant names into the workspace.
    /// </summary>
    public void PersistTheme(string? theme) => App.SetTheme(theme ?? "system");

    /// <summary>
    /// Read-modify-write through <see cref="App.Update"/>, never a cached snapshot:
    /// the preferences window can be open at the same time as this toggle, and
    /// writing back a snapshot taken before it would revert whatever it just changed.
    /// </summary>
    private static void PersistAdvancedView(bool value) =>
        App.Update(s => s with { IsAdvancedView = value });

    /// <summary>
    /// One palette row built from its catalog entry — title and icon from the
    /// descriptor, the shortcut appended to the subtitle when it has one. The action
    /// stays a closure supplied by the caller, because most of this app's palette rows
    /// are conditional on the selected tab or row and only exist while they apply.
    /// </summary>
    private static PaletteItem Catalog(CommandId id, string subtitle, Action run)
    {
        var descriptor = CommandCatalog.Get(id);
        var shortcut = descriptor.ShortcutLabel(Hotkeys.PrimaryLabel);

        return new PaletteItem(
            descriptor.Title,
            shortcut is { Length: > 0 } ? $"{subtitle} · {shortcut}" : subtitle,
            descriptor.IconKey,
            run);
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

        // Nothing is reachable exactly one way. The empty state's button is the route a
        // first run finds; this is the route everyone else does, and it stays offered
        // once a real cluster is open so the demo remains a place to try something out.
        yield return new PaletteItem(
            Tabs.Any(t => t.IsDemo) ? "Go to the demo cluster" : "Explore the demo cluster",
            "Sample data that ships with the app — nothing is connected",
            "LayersIconGeometry",
            () => OpenDemoClusterCommand.Execute(null));

        // The machine's own terminal on the selected cluster. Offered on a demo tab too,
        // unlike the access review: this one refuses in place with a sentence that says
        // why (the demo section's rule 5), which is a real answer, where the access
        // review has none — and rule 15 asks the ☰ menu and the palette to carry the
        // same commands.
        if (SelectedTab is { } terminalTab)
        {
            yield return Catalog(
                CommandId.OpenTerminal,
                $"KUBECONFIG and the current context, pointed at {terminalTab.Header}",
                () => terminalTab.OpenInTerminalCommand.Execute(null));
        }

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

        // Same explicit-target shape, same reason, for the sidebar.
        var sidebarTarget = !IsSidebarVisible;
        yield return new PaletteItem(
            sidebarTarget ? "Show the resource sidebar" : "Hide the resource sidebar",
            sidebarTarget ? "Bring back the kind catalog" : "Give the resource list the full width",
            "SidebarToggleIconGeometry",
            () => IsSidebarVisible = sidebarTarget);

        // Title, icon and shortcut caption all come from the catalog rather than being
        // retyped here, so the palette row, the tooltip and the F1 sheet cannot spell
        // the same command three different ways.
        yield return Catalog(CommandId.Preferences, "Theme, shortcuts, kubeconfig files, logs and metrics",
            () => ShowPreferencesCommand.Execute(null));

        yield return Catalog(CommandId.ShortcutsWindow, "Every gesture, grouped",
            () => ToggleShortcutsCommand.Execute(null));

        yield return Catalog(CommandId.About, "Version and license",
            () => ShowAboutCommand.Execute(null));

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
        // The selected row's actions, mirroring the row context menu. The palette had
        // no logs, exec or port-forward entry at all — the three things the app exists
        // to do — so the only route to any of them was opening a pod's detail pane and
        // finding the buttons on its container strip. Offered only when they apply,
        // rather than listed-and-disabled: a palette is a search, and an entry that
        // matches your query and then refuses to run is worse than no match.
        if (SelectedTab is { SelectedRow: { } row } rowTab)
        {
            var where = $"{row.Namespace}/{row.Name}";

            if (rowTab.IsPodRowSelected)
            {
                yield return new PaletteItem("Logs", where, "PlayIconGeometry",
                    () => rowTab.OpenLogsCommand.Execute(null));

                yield return new PaletteItem("Previous logs", $"{where} · the crashed instance", "PlayIconGeometry",
                    () => rowTab.OpenPreviousLogsCommand.Execute(null));

                yield return new PaletteItem("Exec into container", where, "ConsoleIconGeometry",
                    () => rowTab.ExecIntoSelectedCommand.Execute(null));

                yield return new PaletteItem("Port-forward", where, "SwapHorizontalIconGeometry",
                    () => rowTab.PortForwardSelectedCommand.Execute(null));
            }

            // The mutating actions, gated on what this row's own kind and object
            // actually support (a scale subresource; a pod template to stamp) rather
            // than on a list of kinds — and offered only when they apply, for the same
            // reason the pod-only entries above are: a palette entry that matches a
            // search and then refuses to run is worse than no match. Each of them arms
            // the confirm strip; none of them changes anything on this click.
            if (rowTab.CanScaleSelectedRow)
            {
                yield return new PaletteItem("Scale…", $"{where} · set the replica count", "ScaleIconGeometry",
                    () => rowTab.ScaleSelectedCommand.Execute(null));
            }

            if (rowTab.CanRestartSelectedRow)
            {
                yield return new PaletteItem(
                    "Rollout restart…", $"{where} · roll its pods", "RestartIconGeometry",
                    () => rowTab.RestartSelectedCommand.Execute(null));
            }

            yield return new PaletteItem("Edit YAML", where, "CodeBracesIconGeometry",
                () => rowTab.EditSelectedYamlCommand.Execute(null));

            if (rowTab.CanDeleteSelectedRow)
            {
                yield return new PaletteItem("Delete…", $"{where} · asks to confirm", "DeleteIconGeometry",
                    () => rowTab.DeleteSelectedCommand.Execute(null));
            }
        }

        // IsDemo excluded: the access review is three real API-server calls
        // (SelfSubjectRulesReview, the RBAC object scan, SubjectAccessReview) with no
        // honest offline stand-in, and a palette entry that matches a search and then
        // refuses to run is worse than no match.
        if (IsAdvancedView && SelectedTab is { IsConnected: true, IsDemo: false } connected)
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
