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
    /// <summary>
    /// A store over the file <see cref="AppSettingsStore.DefaultPath"/> names <em>now</em>,
    /// not the one it named when this type was first touched. It used to be a static field,
    /// which fixed the path at type initialization: in the app that is the same thing, but
    /// the screenshot harness and the view-model tests set
    /// <see cref="AppSettingsStore.DirectoryOverride"/> and expect every later read and
    /// write to follow it — and a field initialized before the redirect would have read
    /// and written the settings of whoever was running them (ENG-37). The store holds
    /// nothing but its path, so building one per call costs a string.
    /// </summary>
    private static AppSettingsStore SettingsStore => new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // A class handler, so every element that carries a tooltip answers the pointer
        // (DESIGN.md rule 21). Here rather than in OnFrameworkInitializationCompleted so
        // the screenshot harness, which never gets a lifetime, runs the same wiring.
        Nimbus.Ui.Controls.ToolTipHitTesting.Install();
    }

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

            // No-op unless the process was started with `--smoke-test`. Called from
            // here, on the window the app really builds, so CI's launch check cannot
            // drift into being a second startup path that passes while the real one is
            // broken — which is the failure it exists to catch.
            SmokeTest.Attach(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // One lock around every read and every read-modify-write of settings.json. The app
    // calls these from the UI thread, so it never raced there — but nothing enforces
    // that, and the view-model tests call them from parallel test threads against one
    // file, where two unlocked updates lost each other's change (a persisted log toggle
    // read back as its default in about one run in two).
    private static readonly Lock SettingsLock = new();

    /// <summary>The saved settings, for view-models to initialize from.</summary>
    internal static AppSettings LoadSettings()
    {
        lock (SettingsLock)
        {
            return SettingsStore.Load();
        }
    }

    /// <summary>
    /// Read-modify-write of one setting. Every setter below goes through this rather
    /// than holding a cached snapshot: the preferences window, the command palette and
    /// an inline toggle can all be live at once, and a stale snapshot written back
    /// would silently revert whatever the other one just changed.
    /// </summary>
    internal static void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (SettingsLock)
        {
            SettingsStore.Save(change(SettingsStore.Load()));
        }
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
        if (workspace is { Theme: null } &&
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
            // Deliberately not migrated. The workspace's flag answered a question that
            // no longer exists — it gated usage columns, log toolbars, force-apply and
            // the Helm/RBAC palette entries, none of which the advanced view touches
            // any more — so carrying the value forward would apply an old answer to a
            // new question. It takes AppSettings' own default (on) instead.
            KubeconfigPaths = [.. workspace.KubeconfigPaths ?? []],
        });
    }

    // Lower-cased first: AppSettings.Normalized() already canonicalizes what comes off
    // disk, but SetTheme takes a string straight from a caller, and a mismatch here
    // reads as Default — i.e. as "follow the OS", which is indistinguishable from the
    // toggle not working at all.
    private static ThemeVariant ThemeFromString(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>The inverse of <see cref="ThemeFromString"/>, for the top bar's light/dark toggle.</summary>
    internal static string ThemeToString(ThemeVariant variant) =>
        variant == ThemeVariant.Dark ? "dark" : variant == ThemeVariant.Light ? "light" : "system";
}
