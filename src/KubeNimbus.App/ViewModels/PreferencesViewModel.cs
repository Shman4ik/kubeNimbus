using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.ViewModels;

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
    public string KubeconfigStatus => _main.Status;

    public PreferencesViewModel(MainWindowViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));

        var settings = App.LoadSettings();
        _themeIndex = settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _hotkeySchemeIndex = settings.HotkeyScheme switch { "windows" => 1, "mac" => 2, _ => 0 };
        _logBufferLines = settings.LogBufferLines;
        _metricsPollSeconds = settings.MetricsPollSeconds;
        _confirmDeletes = settings.ConfirmDeletes;

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
            case nameof(MainWindowViewModel.Status):
                OnPropertyChanged(nameof(KubeconfigStatus));
                break;
        }
    }

    partial void OnThemeIndexChanged(int value) =>
        App.SetTheme(value switch { 1 => "light", 2 => "dark", _ => "system" });

    partial void OnHotkeySchemeIndexChanged(int value) =>
        App.SetHotkeyScheme(value switch { 1 => "windows", 2 => "mac", _ => "auto" });

    partial void OnLogBufferLinesChanged(int value) =>
        App.Update(s => s with { LogBufferLines = value });

    partial void OnMetricsPollSecondsChanged(int value) =>
        App.Update(s => s with { MetricsPollSeconds = value });

    partial void OnConfirmDeletesChanged(bool value) =>
        App.Update(s => s with { ConfirmDeletes = value });
}
