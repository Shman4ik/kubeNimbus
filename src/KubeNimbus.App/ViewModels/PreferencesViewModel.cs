using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core.Settings;
using Nimbus.Ui.Fonts;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row of the code font list: a family by name, or the bundled face (<see cref="Name"/>
/// null, which is what <see cref="AppSettings.CodeFont"/> stores for it). <see cref="Family"/>
/// is the face the row draws its own name in. The same record as pgNimbus's.
/// </summary>
public sealed record CodeFontOption(string? Name)
{
    /// <summary>The bundled JetBrains Mono NL.</summary>
    public static CodeFontOption Bundled { get; } = new((string?)null);

    /// <summary>What the list shows.</summary>
    public string Label => Name ?? "JetBrains Mono (built in)";

    /// <summary>The face the row is drawn in, so the list previews each one.</summary>
    public FontFamily Family => NimbusFonts.Mono(Name);
}

/// <summary>
/// Backs the preferences window. Every change applies immediately and persists through
/// the same per-setting <see cref="App"/> helpers the inline toggles use, so the page
/// and the rest of the UI cannot disagree — there is no OK/Cancel and nothing is
/// batched. Same shape as pgNimbus's copy, deliberately: someone who uses both should
/// find the same page doing the same thing.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private bool _syncingTheme;

    /// <summary>0 = system (follow the OS), 1 = light, 2 = dark.</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>0 = auto (Cmd on macOS, Ctrl elsewhere), 1 = always Ctrl, 2 = always Cmd.</summary>
    [ObservableProperty]
    private int _hotkeySchemeIndex;

    /// <summary>Log scrollback cap, in lines. Clamped on save by <see cref="AppSettings.Normalized"/>.</summary>
    [ObservableProperty]
    private int _logBufferLines;

    /// <summary>Seconds between metrics.k8s.io polls.</summary>
    [ObservableProperty]
    private int _metricsPollSeconds;

    /// <summary>Whether deleting a resource requires the two-step confirm.</summary>
    [ObservableProperty]
    private bool _confirmDeletes;

    /// <summary>Whether Apply shows the server's own dry-run diff before changing anything.</summary>
    [ObservableProperty]
    private bool _previewApplies;

    /// <summary>Whether opening logs maximizes the inspector over the list.</summary>
    [ObservableProperty]
    private bool _openLogsMaximized;

    /// <summary>0 = the system face, 1 = Inter. Opens on what "auto" means on this platform.</summary>
    [ObservableProperty]
    private int _interfaceFontIndex;

    /// <summary>The code font choices: the bundled face first, then the installed monospace families once they are read.</summary>
    public RangeObservableCollection<CodeFontOption> CodeFonts { get; } = [];

    /// <summary>The chosen code font. Never null once the page is built.</summary>
    [ObservableProperty]
    private CodeFontOption? _selectedCodeFont;

    // Set while the list is rebuilt: the ComboBox reports a selection change for the old
    // item leaving, which is not a choice.
    private bool _loadingCodeFonts;

    /// <summary>The page's tab: 0 General, 1 Appearance, 2 Logs and metrics, 3 Changes.</summary>
    [ObservableProperty]
    private int _selectedTab;

    partial void OnSelectedTabChanged(int value) => _main.PreferencesTab = value;

    /// <summary>
    /// The kubeconfig files the user has pointed the app at, newest last. Paths only
    /// (CLAUDE.md rule 4) — this list is what gets re-resolved through the kubeconfig
    /// chain at connect time, never a copy of anything inside those files.
    /// </summary>
    public ObservableCollection<string> KubeconfigPaths { get; } = [];

    /// <summary>The path selected in the list, for <see cref="RemoveKubeconfigCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveKubeconfigCommand))]
    private string? _selectedKubeconfigPath;

    /// <summary>
    /// Whether the app found any cluster at all. Shown beside the kubeconfig list
    /// because "I added a file and nothing happened" is the failure this page most
    /// needs to be able to explain (rule 7).
    /// </summary>
    /// <summary>
    /// The search's result for this page: the full diagnosis when nothing was found (the
    /// card's heading, with the parser's message in it), the shell's status line otherwise.
    /// </summary>
    public string KubeconfigStatus => _main.HasContexts || _main.KubeconfigDiagnosis.Length == 0
        ? _main.Status
        : _main.KubeconfigDiagnosis;

    public PreferencesViewModel(MainWindowViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _selectedTab = main.PreferencesTab;

        var settings = App.LoadSettings();
        _themeIndex = ThemeIndexOf(settings.Theme);
        _hotkeySchemeIndex = settings.HotkeyScheme switch { "windows" => 1, "mac" => 2, _ => 0 };
        _logBufferLines = settings.LogBufferLines;
        _metricsPollSeconds = settings.MetricsPollSeconds;
        _confirmDeletes = settings.ConfirmDeletes;
        _previewApplies = settings.PreviewApplies;
        _openLogsMaximized = settings.OpenLogsMaximized;
        _interfaceFontIndex = App.InterfaceFontFromString(settings.InterfaceFont) == InterfaceFont.System ? 0 : 1;
        SetCodeFonts([], settings.CodeFont);
        _ = LoadInstalledCodeFontsAsync();

        RefreshKubeconfigPaths();
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>
    /// Proxies the shell's own switch rather than duplicating it, so its persistence
    /// hook runs and the sidebar chip, the palette entry and this checkbox stay in
    /// sync while the window is open. Same pattern for <see cref="IsSidebarVisible"/>.
    /// </summary>
    public bool IsAdvancedView
    {
        get => _main.IsAdvancedView;
        set => _main.IsAdvancedView = value;
    }

    /// <summary>Whether the resource-catalog sidebar is shown. Proxies the shell, as above.</summary>
    public bool IsSidebarVisible
    {
        get => _main.IsSidebarVisible;
        set => _main.IsSidebarVisible = value;
    }

    /// <summary>Unhooks from the shell when the window closes.</summary>
    public void Detach() => _main.PropertyChanged -= OnMainPropertyChanged;

    /// <summary>
    /// Fills <see cref="CodeFonts"/> with the installed monospace families, read once per
    /// process off the UI thread (<see cref="MonospaceFonts"/>). The page opens with the
    /// bundled face and the saved one already listed, so the box never shows empty while
    /// the scan runs.
    /// </summary>
    public async Task LoadInstalledCodeFontsAsync()
    {
        IReadOnlyList<string> installed;
        try
        {
            installed = await MonospaceFonts.InstalledAsync();
        }
        catch (Exception)
        {
            // The list then offers the bundled face and the saved one, which is enough to
            // keep working: a platform whose font list cannot be read is no reason to lose
            // the page.
            return;
        }

        SetCodeFonts(installed, SelectedCodeFont?.Name);
    }

    private void SetCodeFonts(IReadOnlyList<string> installed, string? selected)
    {
        var options = new List<CodeFontOption> { CodeFontOption.Bundled };
        options.AddRange(installed.Select(name => new CodeFontOption(name)));

        // A saved family that is not installed (any more) stays listed, so the box shows
        // what is saved; the face itself falls back to the bundled one.
        if (selected is not null && !options.Any(option => option.Name == selected))
        {
            options.Add(new CodeFontOption(selected));
        }

        _loadingCodeFonts = true;
        try
        {
            CodeFonts.ReplaceAll(options);
            SelectedCodeFont = options.First(option => option.Name == selected);
        }
        finally
        {
            _loadingCodeFonts = false;
        }
    }

    /// <summary>
    /// Adds a kubeconfig file through the shell's own picker, so the path is validated,
    /// persisted and rescanned by exactly the code the empty state's button uses —
    /// including the rule that a pick yielding no contexts is deliberately not
    /// remembered, so a mis-pick cannot poison every subsequent start.
    /// </summary>
    [RelayCommand]
    private async Task AddKubeconfigAsync()
    {
        await _main.OpenKubeconfigFileCommand.ExecuteAsync(null);
        RefreshKubeconfigPaths();
    }

    /// <summary>
    /// Adds a folder whose kubeconfigs are all searched, including ones added later —
    /// through the shell's own command, same reason as <see cref="AddKubeconfigAsync"/>.
    /// </summary>
    [RelayCommand]
    private async Task AddKubeconfigFolderAsync()
    {
        await _main.AddKubeconfigFolderCommand.ExecuteAsync(null);
        RefreshKubeconfigPaths();
    }

    /// <summary>
    /// Forgets a picked kubeconfig path. Only the app's own memory of the path is
    /// dropped — the file itself is untouched, which is worth being obvious about on a
    /// page that lists other people's cluster credentials by filename.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveKubeconfig))]
    private async Task RemoveKubeconfigAsync()
    {
        if (SelectedKubeconfigPath is not { } path)
        {
            return;
        }

        await _main.ForgetKubeconfigPathAsync(path);
        RefreshKubeconfigPaths();
    }

    private bool CanRemoveKubeconfig() => SelectedKubeconfigPath is not null;

    /// <summary>Re-reads the kubeconfig chain, for a file that appeared since launch.</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        await _main.ReloadContextsCommand.ExecuteAsync(null);
        OnPropertyChanged(nameof(KubeconfigStatus));
    }

    private void RefreshKubeconfigPaths()
    {
        KubeconfigPaths.Clear();
        foreach (var path in App.LoadSettings().KubeconfigPaths)
        {
            KubeconfigPaths.Add(path);
        }

        OnPropertyChanged(nameof(KubeconfigStatus));
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsAdvancedView):
                OnPropertyChanged(nameof(IsAdvancedView));
                break;
            case nameof(MainWindowViewModel.IsSidebarVisible):
                OnPropertyChanged(nameof(IsSidebarVisible));
                break;
            case nameof(MainWindowViewModel.Status) or nameof(MainWindowViewModel.KubeconfigDiagnosis):
                OnPropertyChanged(nameof(KubeconfigStatus));
                break;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_syncingTheme)
        {
            App.SetTheme(value switch { 1 => "light", 2 => "dark", _ => "system" });
        }
    }

    private static int ThemeIndexOf(string? theme) => theme switch { "light" => 1, "dark" => 2, _ => 0 };

    /// <summary>
    /// Follows a theme chosen somewhere else while this page is open: the command bar's
    /// toggle, the palette's entry or the macOS View menu. The page is built once when it
    /// opens and read the setting then, so without this the dropdown kept naming the theme
    /// the page opened on while the app showed another one. Moves the selection without
    /// writing the setting back, because the other writer has just stored it.
    /// </summary>
    internal void SyncTheme(string? theme)
    {
        _syncingTheme = true;
        try
        {
            ThemeIndex = ThemeIndexOf(theme);
        }
        finally
        {
            _syncingTheme = false;
        }
    }

    partial void OnHotkeySchemeIndexChanged(int value) =>
        App.SetHotkeyScheme(value switch { 1 => "windows", 2 => "mac", _ => "auto" });

    partial void OnLogBufferLinesChanged(int value) =>
        App.Update(s => s with { LogBufferLines = value });

    partial void OnMetricsPollSecondsChanged(int value) =>
        App.Update(s => s with { MetricsPollSeconds = value });

    partial void OnConfirmDeletesChanged(bool value) =>
        App.Update(s => s with { ConfirmDeletes = value });

    partial void OnPreviewAppliesChanged(bool value) =>
        App.Update(s => s with { PreviewApplies = value });

    partial void OnOpenLogsMaximizedChanged(bool value) =>
        App.Update(s => s with { OpenLogsMaximized = value });

    partial void OnInterfaceFontIndexChanged(int value) =>
        App.SetInterfaceFont(value == 0 ? "system" : "inter");

    partial void OnSelectedCodeFontChanged(CodeFontOption? value)
    {
        if (!_loadingCodeFonts && value is not null)
        {
            App.SetCodeFont(value.Name);
        }
    }
}
