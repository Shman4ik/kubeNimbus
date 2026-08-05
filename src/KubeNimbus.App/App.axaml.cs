using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App;

public partial class App : Application
{
    private static readonly AppSettingsStore SettingsStore = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Order matters. The migration has to run before anything reads settings, and
        // the hotkey scheme has to be resolved before the first window builds a key
        // binding from it — a gesture captured against the wrong modifier outlives the
        // setting that produced it (see Nimbus.Ui.Hotkeys.Primary).
        MigrateWorkspacePreferences();

        var settings = SettingsStore.Load();
        RequestedThemeVariant = ThemeFromString(settings.Theme);
        Nimbus.Ui.Hotkeys.Initialize(settings.HotkeyScheme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The saved settings, for view-models to initialize from.</summary>
    internal static AppSettings LoadSettings() => SettingsStore.Load();

    /// <summary>
    /// Read-modify-write of one setting. Every setter below goes through this rather
    /// than holding a cached snapshot: the preferences window, the command palette and
    /// an inline toggle can all be live at once, and a stale snapshot written back
    /// would silently revert whatever the other one just changed.
    /// </summary>
    internal static void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        SettingsStore.Save(change(SettingsStore.Load()));
    }

    /// <summary>Applies and persists a theme chosen on the preferences page ("system"/"light"/"dark").</summary>
    internal static void SetTheme(string theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = ThemeFromString(theme);
        }

        Update(s => s with { Theme = theme });
    }

    /// <summary>
    /// Persists the hotkey scheme and re-resolves the live command modifier. The
    /// shared resolver raises <c>Hotkeys.Changed</c>, which is what makes an already-
    /// open window rebuild its bindings and relabel its palette rows rather than
    /// showing the other platform's chord until restart.
    /// </summary>
    internal static void SetHotkeyScheme(string scheme)
    {
        Update(s => s with { HotkeyScheme = scheme });
        Nimbus.Ui.Hotkeys.Initialize(scheme);
    }

    /// <summary>
    /// Moves the preferences that used to live in <c>workspace.json</c> into
    /// <c>settings.json</c>, once, on the first launch after this shipped. Without it
    /// everyone who had already chosen a theme, turned the advanced view on, or picked
    /// a kubeconfig file would silently find those reset — which is exactly the kind of
    /// "the update ate my settings" bug that makes people distrust an update.
    ///
    /// <para>
    /// Guarded on the settings file not existing yet, so it runs at most once and can
    /// never overwrite a later choice. The workspace keeps its own copies untouched:
    /// they are ignored from here on, and leaving them costs a few bytes and means a
    /// downgrade still finds what it expects.
    /// </para>
    /// </summary>
    private static void MigrateWorkspacePreferences()
    {
        if (SettingsStore.Exists())
        {
            return;
        }

        var workspace = WorkspaceStore.Load();

        // A brand-new install has nothing to migrate. Writing a file here anyway would
        // be harmless, but not writing one keeps "no settings file" meaning "never
        // configured anything", which is what the migration guard reads next launch.
        if (workspace is { Theme: null, IsAdvancedView: not true } &&
            (workspace.KubeconfigPaths is null || workspace.KubeconfigPaths.Count == 0))
        {
            return;
        }

        SettingsStore.Save(new AppSettings
        {
            // The workspace spelled these "Dark"/"Light" (ThemeVariant names); settings
            // uses the lowercase strings pgNimbus already persists, so the two apps'
            // files say the same thing.
            Theme = workspace.Theme switch { "Dark" => "dark", "Light" => "light", _ => "system" },
            IsAdvancedView = workspace.IsAdvancedView ?? false,
            KubeconfigPaths = [.. workspace.KubeconfigPaths ?? []],
        });
    }

    private static ThemeVariant ThemeFromString(string? theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>The inverse of <see cref="ThemeFromString"/>, for the top bar's light/dark toggle.</summary>
    internal static string ThemeToString(ThemeVariant variant) =>
        variant == ThemeVariant.Dark ? "dark" : variant == ThemeVariant.Light ? "light" : "system";
}
