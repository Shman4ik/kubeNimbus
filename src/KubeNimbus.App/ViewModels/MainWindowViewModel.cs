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
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
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

        OnPropertyChanged(nameof(SwitcherTooltip));
        OnPropertyChanged(nameof(ShowsApplications));
        OnPropertyChanged(nameof(ShowsResources));
        ActivateModeOnSelectedTab();

        // Remember which tab is in front (see WorkspaceSettings.SelectedTabIndex).
        // Guarded against the restore, and against a tab being closed, where the
        // selection passes through null on its way to the neighbour.
        if (newValue is not null)
        {
            SaveWorkspace();
        }

        // The switcher marks the current tab so it isn't offered as the top hit.
        if (Switcher.IsOpen)
        {
            Switcher.Refresh();
        }
    }

    /// <summary>
    /// The switcher button's tooltip. It names a Ctrl/Cmd chord, so it depends on the
    /// hotkey scheme as well as on <see cref="HasContexts"/>, and
    /// <see cref="OnHotkeySchemeChanged"/> raises it for that reason. It used to survive a
    /// scheme change only by accident — the <c>ToolTip.Tip</c> holds a <c>TextBlock</c>
    /// whose binding re-reads this each time the popup attaches — which a tooltip that
    /// cached its text would not (ENG-17). Not routed through <c>CommandTip</c> because its
    /// sentence changes with the kubeconfig state, which an attached text cannot follow.
    /// </summary>
    public string SwitcherTooltip => HasContexts
        ? $"Switch or open a cluster  ({Hotkeys.Describe(Hotkeys.ClusterSwitcher)})"
        : $"No kubeconfig contexts — the demo cluster is still in here  ({Hotkeys.Describe(Hotkeys.ClusterSwitcher)})";

    /// <summary>
    /// Everything the shell renders that spells out Ctrl or Cmd: the F1 sheet, rebuilt
    /// rather than showing the other platform's chords until restart, and the switcher
    /// tooltip. The window rebuilds its key bindings off the same event, in
    /// <c>MainWindow</c>. Pinned by <c>ShellHotkeySchemeTests</c>, which fails if this
    /// subscription is removed (VER-19).
    /// </summary>
    private void OnHotkeySchemeChanged()
    {
        Shortcuts = new ShortcutsViewModel();
        OnPropertyChanged(nameof(SwitcherTooltip));
    }

    /// <summary>
    /// Removes this shell's handler from the static <c>Hotkeys.Changed</c> (ENG-16). The
    /// app has one shell for its whole life, so there it changes nothing; but a static
    /// event roots every subscriber, and the screenshot harness and the tests build a shell
    /// per scenario — each used to stay reachable, and keep rebuilding its cheat sheet on
    /// every scheme change, until the process ended. <c>MainWindow</c> disposes its view
    /// model when it unloads, the same moment it drops its own subscription.
    /// </summary>
    public void Dispose() => Hotkeys.Changed -= OnHotkeySchemeChanged;

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
    /// The sidebar's width in DIPs, as the reader last dragged it. Shell-owned and
    /// mirrored onto every tab, exactly like <see cref="IsSidebarVisible"/>: the
    /// splitter lives inside one <c>ClusterTabView</c>, but a drag on one cluster's
    /// sidebar has to move every other cluster's too — a width that reverted on each
    /// tab switch would read as the drag not having stuck.
    ///
    /// <para>
    /// Written by the view when a drag ends, not while it is in flight: persisting
    /// every intermediate pixel would write the settings file dozens of times per
    /// gesture.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private double _sidebarWidth = AppSettings.DefaultSidebarWidth;

    partial void OnSidebarWidthChanged(double value)
    {
        App.Update(s => s with { SidebarWidth = value });

        foreach (var tab in Tabs)
        {
            tab.SidebarWidth = value;
        }
    }

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
    /// The preferences tab last shown, so the page opens where it was left. For the
    /// session only: the page's view model is rebuilt on every open, this outlives it.
    /// </summary>
    public int PreferencesTab { get; set; }

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

    // ------------------------------------------------------------------ mode

    /// <summary>
    /// Which way into the cluster the window shows: the Applications list (the default,
    /// and the first screen of every cluster tab) or the Resources explorer. One value for
    /// the window, persisted in <c>workspace.json</c> as session state. Switching never
    /// restarts a watch or loses list or inspector state: both views stay alive, one hidden.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicationsMode), nameof(IsResourcesMode), nameof(ModeIndex),
        nameof(ShowsApplications), nameof(ShowsResources))]
    private ShellMode _mode = ShellMode.Applications;

    /// <summary>The content area shows the selected tab's Applications list.</summary>
    public bool ShowsApplications => IsApplicationsMode && SelectedTab is not null;

    /// <summary>The content area shows the selected tab's Resources explorer.</summary>
    public bool ShowsResources => IsResourcesMode && SelectedTab is not null;

    public bool IsApplicationsMode => Mode == ShellMode.Applications;

    public bool IsResourcesMode => Mode == ShellMode.Resources;

    /// <summary>The segmented control's selection, two-way. 0 = Applications, 1 = Resources.</summary>
    public int ModeIndex
    {
        get => (int)Mode;
        set
        {
            if (value is 0 or 1)
            {
                Mode = (ShellMode)value;
            }
        }
    }

    partial void OnModeChanged(ShellMode value)
    {
        ActivateModeOnSelectedTab();
        SaveWorkspace();
    }

    /// <summary>
    /// Explicit targets rather than a toggle, so no control can race its own state (UI rule 8b).
    /// Asked for while the Applications mode is already showing, it goes back to the list from
    /// an open application page — what a second press on the active tab does in a tab bar,
    /// and what the page's own "‹ Applications" link does, so the two controls named
    /// Applications on that screen agree. From Resources it only switches mode and keeps
    /// the page, because switching mode never loses state.
    /// </summary>
    [RelayCommand]
    private void ShowApplications()
    {
        if (IsApplicationsMode)
        {
            if (SelectedTab?.Applications is { IsPageOpen: true } applications)
            {
                applications.ClosePageCommand.Execute(null);
            }

            return;
        }

        Mode = ShellMode.Applications;
    }

    [RelayCommand]
    private void ShowResources() => Mode = ShellMode.Resources;

    /// <summary>
    /// The Applications list reads a tab's cluster only once it is shown for that tab, so
    /// restoring five tabs in Resources mode opens no watches the reader never looks at.
    /// </summary>
    private void ActivateModeOnSelectedTab()
    {
        if (IsApplicationsMode && SelectedTab is { } tab)
        {
            tab.Applications.Activate();
        }
    }

    public MainWindowViewModel()
    {
        Palette = new CommandPaletteViewModel(BuildPaletteItems)
        {
            // Every open takes a fresh one-shot look at the selected tab's pods and
            // workloads (the log rows). It returns at once; the rows join an open palette
            // through LogTargetsChanged below, and the stale answer shows meanwhile.
            Opening = () => SelectedTab?.RequestLogTargets(),
        };
        Switcher = new ClusterSwitcherViewModel(BuildSwitcherItems) { Activate = ActivateSwitcherItem };

        LoadPreferences();

        // Removed again in Dispose — see OnHotkeySchemeChanged.
        Hotkeys.Changed += OnHotkeySchemeChanged;

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

                // Value first, then the write-back, so stamping never round-trips
                // through the shell — exactly as the advanced view above does it.
                tab.SidebarWidth = SidebarWidth;
                tab.SidebarWidthChanged = value => SidebarWidth = value;

                // The application page hands linked objects and the YAML editor to the
                // Resources mode; this is how it gets there.
                tab.Applications.SwitchToResources = () => Mode = ShellMode.Resources;

                // Log rows landing while the palette is open join it in place. Only the
                // selected tab's: a background tab finishing a load has nothing to show.
                tab.LogTargetsChanged = () =>
                {
                    if (Palette.IsOpen && ReferenceEquals(SelectedTab, tab))
                    {
                        Palette.Refresh();
                    }
                };
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

#pragma warning disable MVVMTK0034
        _mode = Enum.TryParse<ShellMode>(settings.ShellMode, ignoreCase: true, out var mode) ? mode : ShellMode.Applications;
#pragma warning restore MVVMTK0034

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
        _sidebarWidth = preferences.SidebarWidth;
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
            // One unreadable file costs that file, not the chain: the rest still load,
            // and the status line names the file and the parser's own sentence.
            var failures = new List<KubeconfigReadFailure>();
            var picked = _pickedKubeconfigPaths.ToArray();

            // Taken before the read, so a file rewritten while this load runs still reads
            // as changed on the next focus rather than being missed. Inline: it is a
            // handful of stat calls, the same cost as the search-path refresh below.
            var fingerprint = Kubeconfig.ChainFingerprint(picked);
            var contexts = await Kubeconfig.LoadContextsAsync(
                extraPaths: picked, failures: failures);
            AvailableContexts.Clear();
            foreach (var ctx in contexts)
            {
                AvailableContexts.Add(ctx);
            }

            HasContexts = AvailableContexts.Count > 0;
            var existing = RefreshSearchPaths();
            _lastChainFingerprint = fingerprint;

            // The no-kubeconfig card and the status bar are both on screen in that state,
            // and they used to render the one `Status` property — the same sentence twice.
            // So the card has its own property (the diagnosis, which is where the parser's
            // message belongs) and the status bar says something shorter and different.
            // The obvious fix, shortening `Status`, silently replaced the card's heading
            // the first time it was tried (ENG-29); `KubeconfigDiagnosisTests` pins both.
            var unreadable = failures.Count switch
            {
                0 => "",
                1 => $"Could not read {failures[0].Path}: {failures[0].Message}",
                _ => $"Could not read {failures.Count} kubeconfig files — first, {failures[0].Path}: {failures[0].Message}",
            };
            KubeconfigDiagnosis = (HasContexts, failures.Count) switch
            {
                (true, _) => "",
                (false, 0) => "No kubeconfig contexts found.",
                (false, _) => $"Failed to read kubeconfig. {unreadable}",
            };
            Status = (HasContexts, failures.Count, existing) switch
            {
                (true, 0, _) => $"{AvailableContexts.Count} context(s) available.",
                (true, _, _) => $"{AvailableContexts.Count} context(s) available. {unreadable}",
                (false, 0, 0) => KubeconfigSearchPathCount == 1
                    ? "No clusters: the kubeconfig location searched does not exist."
                    : $"No clusters: none of the {KubeconfigSearchPathCount} kubeconfig locations searched exists.",
                (false, 0, _) => $"No clusters: {existing} kubeconfig file(s) found, none with a context.",
                (false, 1, _) => "No clusters: the kubeconfig could not be read.",
                (false, _, _) => $"No clusters: {failures.Count} kubeconfig files could not be read.",
            };
            AddNewTabCommand.NotifyCanExecuteChanged();
            return HasContexts || failures.Count == 0;
        }
        catch (Exception ex)
        {
            // Still refresh the search list: a file that failed to parse is exactly
            // when "here is what was read" matters, and leaving the previous list on
            // screen would name the wrong file (UI rule 9).
            RefreshSearchPaths();
            KubeconfigDiagnosis = $"Failed to read kubeconfig: {ex.Message}";
            Status = "No clusters: the kubeconfig could not be read.";
            return false;
        }
    }

    /// <summary>
    /// The no-kubeconfig card's heading: what was wrong with the search, including the
    /// parser's own message for a file that would not read. Empty while contexts exist —
    /// the card is hidden then. Not <see cref="Status"/>, which the status bar under the
    /// card shows at the same time; see <c>LoadContextsAsync</c>.
    /// </summary>
    [ObservableProperty]
    private string _kubeconfigDiagnosis = "";

    /// <summary>What <see cref="Kubeconfig.ChainFingerprint"/> said at the last load, for <see cref="RescanIfChangedAsync"/>.</summary>
    private string? _lastChainFingerprint;

    private bool _rescanning;

    /// <summary>
    /// Reloads the context list if any kubeconfig file the search reads — including every
    /// file in a picked folder — was added, removed or rewritten since the last load. The
    /// window calls this when it regains focus, which is the moment someone comes back from
    /// <c>aws eks update-kubeconfig</c> in a terminal or from dropping a file into a synced
    /// folder. Metadata only until something has changed, so an ordinary Alt+Tab costs a
    /// handful of <c>stat</c> calls.
    /// </summary>
    /// <remarks>
    /// Rescan-on-focus rather than a <c>FileSystemWatcher</c>: a watcher on <c>~/.kube</c>
    /// fires on every write every tool makes there (kubectl's discovery cache, kubectx's
    /// state file, lock files), and each of those would re-parse the whole chain while the
    /// app is in the background and nobody is looking. Open tabs are never touched — a
    /// context that disappears from the files keeps its live tab, and simply is not
    /// restorable next launch.
    /// </remarks>
    public async Task RescanIfChangedAsync()
    {
        if (_rescanning || _lastChainFingerprint is null)
        {
            return;
        }

        _rescanning = true;
        try
        {
            var picked = _pickedKubeconfigPaths.ToArray();
            var current = await Task.Run(() => Kubeconfig.ChainFingerprint(picked));
            if (current != _lastChainFingerprint)
            {
                await LoadContextsAsync();
                if (Switcher.IsOpen)
                {
                    Switcher.Refresh();
                }
            }
        }
        finally
        {
            _rescanning = false;
        }
    }

    /// <summary>
    /// Rebuilds the empty state's "Searched:" list. Picked files carry their own
    /// source label, so a config the user chose is distinguishable from one the app
    /// found — and a picked file that has since been moved shows as <c>missing</c>
    /// instead of vanishing without explanation.
    /// </summary>
    private int RefreshSearchPaths()
    {
        var candidates = Kubeconfig.CandidatePaths(_pickedKubeconfigPaths).ToList();
        KubeconfigSearchPathCount = candidates.Count(c => !c.IsFolder);
        KubeconfigSearchPaths = string.Join(
            Environment.NewLine,
            candidates.Select(c =>
                $"{(c.IsFolder ? "folder " : c.Exists ? "found  " : "missing")}  {c.Path}   ({c.Source})"));
        return candidates.Count(c => c.Exists && !c.IsFolder);
    }

    /// <summary>
    /// How many file locations the last load looked in — the status bar's count in the
    /// no-kubeconfig state, where the card above it lists them. A picked folder is not
    /// one; the files found in it are.
    /// </summary>
    [ObservableProperty]
    private int _kubeconfigSearchPathCount;

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
            var refused = $"No clusters in {Path.GetFileName(path)} — it has no contexts, or it isn't a kubeconfig file.";
            if (HasContexts)
            {
                Status = refused;
            }
            else
            {
                // The card is on screen: the reason goes in its heading, and the status
                // bar keeps its own shorter line (ENG-29).
                KubeconfigDiagnosis = refused;
                Status = "The picked file was not added.";
            }

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
    /// "Add kubeconfig folder…" — a folder instead of a file, searched on every rescan,
    /// so a kubeconfig dropped into it later (a CI job's output, a teammate's handoff, a
    /// tool that writes one file per cluster) turns up without being picked itself. The
    /// window's rescan-on-focus is what makes "later" mean "when you next look".
    /// </summary>
    /// <remarks>
    /// Kept even when it holds no kubeconfig yet, unlike a picked file that yields no
    /// contexts: an empty folder is the ordinary starting state of the thing this is for,
    /// and it cannot poison a start the way a mis-picked file could — it simply
    /// contributes nothing until something is in it. Only the path is stored (rule 4).
    /// </remarks>
    [RelayCommand]
    private async Task AddKubeconfigFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add kubeconfig folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }

        await AddKubeconfigFolderPathAsync(path);
    }

    /// <summary>The folder half of <see cref="AddKubeconfigFolderCommand"/>, after the picker.</summary>
    internal async Task AddKubeconfigFolderPathAsync(string path)
    {
        _pickedKubeconfigPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _pickedKubeconfigPaths.Insert(0, path);
        SaveWorkspace();

        await LoadContextsAsync();
        if (Switcher.IsOpen)
        {
            Switcher.Refresh();
        }

        var found = Kubeconfig.FolderKubeconfigs(path).Count;
        if (found == 0)
        {
            Status = $"No kubeconfig in {Path.GetFileName(path.TrimEnd('/', '\\'))} yet — files added there are picked up when kubeNimbus is next focused.";
        }

        if (Tabs.Count == 0 && AvailableContexts.Count > 0)
        {
            await AddTabAsync(AvailableContexts.FirstOrDefault(c => c.IsCurrentContext) ?? AvailableContexts[0]);
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
        var connects = new List<Task>();
        _isRestoring = true;
        try
        {
            foreach (var snapshot in settings.Tabs)
            {
                // The demo cluster is not a kubeconfig context, so it is never in
                // AvailableContexts and the name+path match below can't find it. The
                // sentinel path is what identifies it — that is the whole reason it is a
                // path rather than a new field on TabSnapshot.
                var context = snapshot.KubeconfigPath == ClusterContext.DemoKubeconfigPath
                    ? ClusterContext.Demo
                    : AvailableContexts.FirstOrDefault(c =>
                        c.Name == snapshot.ContextName && c.KubeconfigPath == snapshot.KubeconfigPath);
                if (context is not null)
                {
                    // Every tab connects at once. They used to connect one after another,
                    // so one cluster behind a slow VPN or a hung credential plugin held
                    // every tab after it on "Connecting…" — the whole window paced by
                    // its slowest cluster, on the one launch path that is supposed to
                    // be this app's reason to exist.
                    connects.Add(AddTabAsync(context, snapshot));
                }
            }

            if (Tabs.Count == 0 && AvailableContexts.Count > 0)
            {
                // No workspace yet: open the kubeconfig's current-context — the cluster
                // kubectl would talk to — rather than whichever context the merge
                // happened to list first.
                var current = AvailableContexts.FirstOrDefault(c => c.IsCurrentContext);
                connects.Add(AddTabAsync(current ?? AvailableContexts[0]));
            }
            else if (settings.SelectedTabIndex is { } index && index >= 0 && index < Tabs.Count)
            {
                SelectedTab = Tabs[index];
            }
        }
        finally
        {
            _isRestoring = false;
        }

        SaveWorkspace();
        await Task.WhenAll(connects);
    }

    /// <summary>
    /// True while <see cref="RestoreWorkspaceAsync"/> is adding tabs, so each one
    /// becoming selected in turn does not rewrite the saved selection before the real
    /// one has been read back.
    /// </summary>
    private bool _isRestoring;

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
        OnPropertyChanged(nameof(SwitcherTooltip));
    }

    private async Task AddTabAsync(ClusterContext context, TabSnapshot? snapshot = null)
    {
        var tab = new ClusterTabViewModel(context)
        {
            FleetMembersProvider = FleetMembers,
            RestoreKindKey = snapshot?.KindKey,
            RestoreNamespace = snapshot?.Namespace,
            RestoreNamespaces = snapshot?.Namespaces,
        };
        tab.ViewStateChanged += (_, _) => SaveWorkspace();
        Tabs.Add(tab);
        SelectedTab = tab;
        RecordRecent(context.Name);
        if (!_isRestoring)
        {
            SaveWorkspace();
        }

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

            // The member's own environment travels with it, so a fleet row's delete is gated
            // and coloured by the cluster it is on rather than the tab showing it.
            members.Add(new FleetMember(name, client, tab.Environment));
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
        if (_isRestoring)
        {
            return;
        }

        var settings = WorkspaceStore.Load();

        // A tab that has not finished connecting has no kind or namespace of its own
        // yet; saving its nulls would forget the ones it is about to restore.
        var tabs = Tabs.Select(t => new TabSnapshot(
            t.Context.Name, t.Context.KubeconfigPath,
            t.ViewKindKey ?? t.RestoreKindKey,
            t.SelectedKind is null ? t.RestoreNamespace : t.SelectedNamespace,
            t.SelectedKind is null ? t.RestoreNamespaces : t.SelectedNamespaces is { Count: > 1 } several ? [.. several] : null)).ToList();
        WorkspaceStore.Save(settings with
        {
            Tabs = tabs,
            SelectedTabIndex = SelectedTab is { } selected ? Tabs.IndexOf(selected) : null,
            PinnedContexts = [.. _pinned],
            RecentContexts = [.. _recent],
            EnvironmentOverrides = _environmentOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            ShellMode = Mode.ToString(),
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
    /// wrote ThemeVariant names into the workspace. The preferences page, when it is
    /// open, is told so its dropdown shows the theme the app now has.
    /// </summary>
    public void PersistTheme(string? theme)
    {
        theme ??= "system";
        App.SetTheme(theme);
        Preferences?.SyncTheme(theme);
    }

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

        // The answer to an expired session, by name: re-read the kubeconfig, re-run the
        // credential plugin, restart the list. Only where it can run (not the demo tab,
        // not mid-connect) — a palette row that refuses is worse than no row.
        if (SelectedTab is { IsDemo: false } reconnectTab && reconnectTab.ReconnectCommand.CanExecute(null))
        {
            yield return Catalog(
                CommandId.ReconnectCluster,
                $"Re-read the kubeconfig and run its credential plugin again for {reconnectTab.Header}",
                () => reconnectTab.ReconnectCommand.Execute(null));
        }

        // The ☰ menu's kubeconfig rows, so the palette carries the same commands (UI rule 15).
        yield return Catalog(CommandId.OpenKubeconfigFile, "Add a kubeconfig file's clusters — only its path is kept",
            () => OpenKubeconfigFileCommand.Execute(null));
        yield return Catalog(CommandId.AddKubeconfigFolder, "Every kubeconfig in a folder, including ones added later",
            () => AddKubeconfigFolderCommand.Execute(null));
        yield return Catalog(CommandId.RescanKubeconfig, "Read $KUBECONFIG, ~/.kube/config and picked files and folders again",
            () => ReloadContextsCommand.Execute(null));

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
            advancedTarget ? "Advanced view: show every resource kind" : "Advanced view: show only everyday kinds",
            advancedTarget
                ? "Adds the API machinery and CRDs to the sidebar"
                : "Pods, Deployments, Services, Nodes… — about 20 kinds",
            "EyePlusIconGeometry",
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
        // The mode not on screen — offering the one you are already in would be a row that
        // matches and does nothing.
        yield return IsApplicationsMode
            ? Catalog(CommandId.ShowResources, "Kinds, lists and the inspector dock", () => Mode = ShellMode.Resources)
            : Catalog(CommandId.ShowApplications, "Every application's health and the reason, first", () => Mode = ShellMode.Applications);

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

        // Unhealthy only. Offered to switch on where the kind has a health verdict to
        // filter by, and to switch off wherever it is on — including a kind where the
        // disabled chip cannot, so the palette is always a way out of the mode. Explicit
        // target, captured now, for the same reason as the rows above.
        if (SelectedTab is { IsResourceListVisible: true } healthTab
            && (healthTab.CanFilterUnhealthy || healthTab.IsUnhealthyOnly))
        {
            var descriptor = CommandCatalog.Get(CommandId.ToggleUnhealthyOnly);
            var unhealthyTarget = !healthTab.IsUnhealthyOnly;
            var kind = healthTab.SelectedKind?.DisplayName ?? "rows";
            var shortcut = descriptor.ShortcutLabel(Hotkeys.PrimaryLabel);
            yield return new PaletteItem(
                unhealthyTarget ? descriptor.Title : "Show every row, healthy ones too",
                unhealthyTarget
                    ? $"{kind} · warnings and errors only · {shortcut} in the list"
                    : $"{kind} · leave unhealthy-only · {shortcut} in the list",
                descriptor.IconKey,
                () => healthTab.IsUnhealthyOnly = unhealthyTarget);
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
                // Through Catalog() so each row carries its list key ("· L"): the palette
                // is where somebody who reached for the mouse learns there was a key.
                yield return Catalog(CommandId.PodLogs, where,
                    () => rowTab.OpenLogsCommand.Execute(null));

                yield return Catalog(CommandId.PreviousLogs, $"{where} · the crashed instance",
                    () => rowTab.OpenPreviousLogsCommand.Execute(null));

                yield return Catalog(CommandId.Exec, where,
                    () => rowTab.ExecIntoSelectedCommand.Execute(null));

                yield return Catalog(CommandId.PortForward, where,
                    () => rowTab.PortForwardSelectedCommand.Execute(null));
            }
            else if (rowTab.SelectedEventPod is { } eventPod)
            {
                // An Event about a pod: "Logs" is that pod's, as it is for the list's L and
                // the row's logs icon. The subtitle names the pod, not the event.
                yield return Catalog(CommandId.PodLogs,
                    $"{row.Resource.InvolvedObjectNamespace() ?? row.Namespace}/{eventPod.Name} · the pod this event is about",
                    () => rowTab.OpenLogsCommand.Execute(null));
            }

            // Offered on whatever names the pods it owns, which is the same evidence the
            // menu item is gated on — never on a list of kinds.
            if (rowTab.CanAggregateLogsForSelectedRow)
            {
                yield return new PaletteItem(
                    "Logs (all pods)", $"{where} · one stream across every pod", LogPaletteRows.Icon,
                    () => rowTab.OpenWorkloadLogsCommand.Execute(null));
            }

            // The same logs, opened full-size — for a pod and a workload alike, which is
            // where the list's Shift+L works.
            if (rowTab.CanOpenLogsForSelectedRow)
            {
                yield return Catalog(CommandId.PodLogsMaximized, where,
                    () => rowTab.OpenLogsMaximizedCommand.Execute(null));
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
                yield return Catalog(CommandId.RolloutRestart, $"{where} · roll its pods",
                    () => rowTab.RestartSelectedCommand.Execute(null));
            }

            // Node actions, gated the same way: cordon/uncordon on which of the two the
            // node's own spec.unschedulable makes meaningful, drain additionally on the
            // server serving pods/eviction.
            if (rowTab.CanCordonSelectedRow)
            {
                yield return new PaletteItem(
                    "Cordon node…", $"{where} · stop scheduling new pods here", "CordonIconGeometry",
                    () => rowTab.CordonSelectedCommand.Execute(null));
            }

            if (rowTab.CanUncordonSelectedRow)
            {
                yield return new PaletteItem(
                    "Uncordon node…", $"{where} · put it back into service", "CheckIconGeometry",
                    () => rowTab.UncordonSelectedCommand.Execute(null));
            }

            if (rowTab.CanDrainSelectedRow)
            {
                yield return new PaletteItem(
                    "Drain node…", $"{where} · cordon, then evict its pods", "DrainIconGeometry",
                    () => rowTab.DrainSelectedCommand.Execute(null));
            }

            // A CronJob's run-now and its suspend/resume pair, gated like the node actions:
            // on a Job template and a creatable Job kind, and on the CronJob's own
            // spec.suspend for which of the pair applies.
            if (rowTab.CanTriggerSelectedRow)
            {
                yield return new PaletteItem(
                    "Run CronJob now…", $"{where} · create a Job from its template", "PlayIconGeometry",
                    () => rowTab.TriggerSelectedCommand.Execute(null));
            }

            if (rowTab.CanSuspendSelectedRow)
            {
                yield return new PaletteItem(
                    "Suspend CronJob…", $"{where} · stop scheduling new Jobs", "PauseIconGeometry",
                    () => rowTab.SuspendSelectedCommand.Execute(null));
            }

            if (rowTab.CanResumeSelectedRow)
            {
                yield return new PaletteItem(
                    "Resume CronJob…", $"{where} · back on its schedule", "ClockOutlineIconGeometry",
                    () => rowTab.ResumeSelectedCommand.Execute(null));
            }

            // The other end of a PV/PVC binding (FEAT-47).
            if (rowTab.BoundObjectLabel is { } bound)
            {
                yield return new PaletteItem(
                    bound, $"{where} · the other end of its binding", "LinkIconGeometry",
                    () => rowTab.OpenBoundObjectCommand.Execute(null));
            }

            yield return Catalog(CommandId.EditYaml, where,
                () => rowTab.EditSelectedYamlCommand.Execute(null));

            if (rowTab.CanDeleteSelectedRow)
            {
                yield return Catalog(CommandId.DeleteResource, $"{where} · asks to confirm",
                    () => rowTab.DeleteSelectedCommand.Execute(null));
            }
        }

        // Argo CD's two actions. Their own block rather than one inside the selected-row
        // section above, because they have two sources: the GitOps dashboard's selected
        // Application (where the resource list has no selection at all) and an ordinary
        // Applications list's selected row. The tab resolves whichever is showing, so this
        // asks it rather than testing for a row.
        if (SelectedTab is { CanSyncSelectedArgoApplication: true } argoTab
            && argoTab.ArgoActionLabel is { } argoWhere)
        {
            yield return new PaletteItem(
                "Argo CD: sync…", $"{argoWhere} · apply the revision Git declares", "SyncIconGeometry",
                () => argoTab.SyncArgoApplicationCommand.Execute(null));

            yield return new PaletteItem(
                "Argo CD: refresh from Git…", $"{argoWhere} · re-compare, change nothing", "RefreshIconGeometry",
                () => argoTab.RefreshArgoApplicationCommand.Execute(null));
        }

        // IsDemo excluded: the access review is three real API-server calls
        // (SelfSubjectRulesReview, the RBAC object scan, SubjectAccessReview) with no
        // honest offline stand-in, and a palette entry that matches a search and then
        // refuses to run is worse than no match.
        if (SelectedTab is { IsConnected: true, IsDemo: false } connected)
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

        foreach (var item in LogPaletteItems())
        {
            yield return item;
        }

        if (SelectedTab is { } current)
        {
            // Namespaces, so switching one is Ctrl/Cmd+K and a few letters instead of
            // opening a dropdown and scrolling a list that runs to hundreds on a shared
            // cluster (kubens, k9s `:ns`). Offered only where the picker itself is live —
            // a cluster-scoped kind ignores the namespace, and an entry that then changed
            // nothing visible would be the "matches and does nothing" failure again.
            if (current.SelectedKind?.Descriptor is { Namespaced: true })
            {
                foreach (var ns in current.NamespaceOptions)
                {
                    if (ns == current.SelectedNamespace)
                    {
                        continue;
                    }

                    var kindName = current.SelectedKind.DisplayName;
                    yield return new PaletteItem(
                        ns == ClusterTabViewModel.AllNamespaces ? "Namespace: all" : $"Namespace: {ns}",
                        $"Show {kindName} in {(ns == ClusterTabViewModel.AllNamespaces ? "every namespace" : ns)} · {current.Header}",
                        "LayersIconGeometry",
                        () => current.SelectedNamespace = ns);
                }
            }

            // Every section, including the ones the advanced view hides from the
            // sidebar. The palette is a search: somebody typing "CustomResourceDefinition"
            // has said which kind they want, and a match that then refuses to appear is
            // the failure this app's own palette rules name. Same reasoning as the
            // sidebar's own filter reaching into a hidden section.
            foreach (var section in current.SidebarSections)
            {
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

    /// <summary>
    /// The palette's log rows: an entry that narrows the palette to logs, then a
    /// <c>Logs: …</c> row per pod and per Deployment/StatefulSet/DaemonSet in the selected
    /// tab's namespace, then the notes that say what the rows cannot (still loading, not
    /// allowed to list, capped). Last in the source, so in the unfiltered palette the
    /// commands come first and a namespace of pods does not bury them — the same reason the
    /// switcher, not the palette, lists every kubeconfig context.
    /// </summary>
    private IEnumerable<PaletteItem> LogPaletteItems()
    {
        if (SelectedTab is not { } tab)
        {
            yield break;
        }

        var descriptor = CommandCatalog.Get(CommandId.LogsPalette);
        var shortcut = descriptor.ShortcutLabel(Hotkeys.PrimaryLabel);
        var state = tab.LogTargetsState;
        yield return new PaletteItem(
            descriptor.Title,
            $"Every pod and workload in {state.Scope} · {shortcut}",
            descriptor.IconKey,
            () => Palette.Query = CommandPaletteViewModel.LogsPrefix)
        {
            KeepsPaletteOpen = true,
        };

        foreach (var row in tab.LogTargetRows)
        {
            yield return row;
        }

        foreach (var note in LogPaletteRows.Notes(state))
        {
            yield return note;
        }
    }
}
