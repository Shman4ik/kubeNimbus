using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core.Settings;
using Nimbus.Ui.Fonts;

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

        // The chosen faces (DESIGN.md rule 22), before any window lays its text out, so
        // none lays it out twice. Here rather than in OnFrameworkInitializationCompleted
        // for the same reason as the handler below: the screenshot harness renders what
        // the app shows by default, not Fluent's Inter.
        ApplyFonts(LoadSettings());

        // A class handler, so every element that carries a tooltip answers the pointer
        // (DESIGN.md rule 21). Here rather than in OnFrameworkInitializationCompleted so
        // the screenshot harness, which never gets a lifetime, runs the same wiring.
        Nimbus.Ui.Controls.ToolTipHitTesting.Install();
        AutomationNames.Install();
        Views.HeaderDragGuard.Install();

        // The Cocoa text keys (⌘↑, ⌘→, ⌥⌫, ⌃K …) on every TextBox and AvaloniaEdit editor.
        // Class handlers, installed here so the screenshot harness's ux-mac-text-keys check
        // runs the same wiring; inert off macOS and under the Windows scheme.
        Platform.MacTextKeys.Install();

        // The macOS app menu has to exist before the platform's AfterSetup step builds the
        // menu exporter, which reads it once and never again — set any later and macOS keeps
        // Avalonia's "About Avalonia" placeholder for good. That is exactly what happened when
        // it was set from the main window (see Platform.MacAppMenu).
        if (OperatingSystem.IsMacOS())
        {
            Avalonia.Controls.NativeMenu.SetMenu(this, Platform.MacAppMenu.Create(
                Name ?? "kubeNimbus",
                about: () => MainViewModel()?.ShowAboutCommand.Execute(null),
                settings: () => MainViewModel()?.ShowPreferencesCommand.Execute(null)));
        }
    }

    /// <summary>
    /// The main window's view model, for the app menu: the app menu is the application's,
    /// not the window's, so its items find their target at click time.
    /// </summary>
    private MainWindowViewModel? MainViewModel() =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainWindowViewModel;

    /// <summary>
    /// macOS only: corrects the standard block Avalonia.Native appended to the app menu
    /// during platform setup (⌥⌘H for Hide Others, "Quit kubeNimbus"), and keeps Settings…'s
    /// chord in step with the hotkey scheme.
    /// </summary>
    private void FinishMacAppMenu()
    {
        if (!OperatingSystem.IsMacOS() || Avalonia.Controls.NativeMenu.GetMenu(this) is not { } appMenu)
        {
            return;
        }

        Platform.MacAppMenu.FixStandardItems(appMenu, Name ?? "kubeNimbus");
        Platform.MacAppMenu.RefreshGestures(appMenu);
        Nimbus.Ui.Hotkeys.Changed += () => Platform.MacAppMenu.RefreshGestures(appMenu);
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
        FinishMacAppMenu();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // macOS: a finished shutdown ends the process with _exit rather than handing it
            // back to exit(), which aborts in libAvaloniaNative's static destructors (see
            // Platform.MacShutdown). The smoke test's verdict goes through it, because once
            // this has run, nothing after the message loop does.
            Platform.MacShutdown.ExitProcessOnShutdown(desktop, SmokeTest.IsRequested ? SmokeTest.Verdict : null);

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
    /// Writes the typography resources from <paramref name="settings"/> into the
    /// application's own resources (DESIGN.md rule 22). Every use site reads them as
    /// <c>DynamicResource</c> (the <c>mono</c> class, Fluent's window font), so open
    /// windows follow a change with nothing to reopen. A no-op with no application,
    /// which is how the view-model tests run.
    /// </summary>
    internal static void ApplyFonts(AppSettings settings)
    {
        if (Current is { } app)
        {
            NimbusFonts.Apply(app.Resources, InterfaceFontFromString(settings.InterfaceFont), settings.CodeFont);
        }
    }

    /// <summary>The face a stored <see cref="AppSettings.InterfaceFont"/> means on this platform ("auto" is the platform's default).</summary>
    internal static InterfaceFont InterfaceFontFromString(string? value) => value?.ToLowerInvariant() switch
    {
        "system" => InterfaceFont.System,
        "inter" => InterfaceFont.Inter,
        _ => NimbusFonts.PlatformDefault,
    };

    /// <summary>Applies and persists the interface face chosen on the preferences page ("system"/"inter").</summary>
    internal static void SetInterfaceFont(string value)
    {
        Update(s => s with { InterfaceFont = value });
        ApplyFonts(LoadSettings());
    }

    /// <summary>Applies and persists the code face chosen on the preferences page (null: the bundled one).</summary>
    internal static void SetCodeFont(string? value)
    {
        Update(s => s with { CodeFont = value });
        ApplyFonts(LoadSettings());
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
