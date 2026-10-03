namespace KubeNimbus.Core.Settings;

/// <summary>
/// Persisted, cross-session app preferences — the things a user chooses once and
/// expects to still be true next launch. Deliberately separate from
/// <c>WorkspaceSettings</c> (the App layer's <c>workspace.json</c>), which holds
/// what the user's *session* looked like: which tabs were open, which clusters are
/// pinned, which contexts were recent. Deleting the workspace should lose your tabs
/// and nothing else; deleting the settings should reset your preferences and not
/// close your clusters.
///
/// <para>
/// A record with defaulted properties so a settings file written by an older build —
/// missing a field added later — still loads, with the new field falling back to its
/// default. The properties are <c>set</c>, not <c>init</c>, and that is load-bearing:
/// the source-generated JSON deserializer bypasses property initializers for
/// init-only setters, so an <c>init</c> flag defaulting to true would silently read
/// false from any settings file predating it. Same trap, same rule, as pgNimbus's
/// copy of this type.
/// </para>
///
/// <para>
/// Nothing here is a credential, and nothing here may become one (CLAUDE.md rule 4).
/// <see cref="KubeconfigPaths"/> is the closest it comes, and it is paths only —
/// re-resolved through the kubeconfig chain at connect time, never the file contents.
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// The chosen theme: <c>"light"</c>, <c>"dark"</c>, or <c>"system"</c> (follow the
    /// OS). Kept as a plain string so <c>KubeNimbus.Core</c> stays free of any
    /// UI-framework types (rule 1); the App maps it to Avalonia's ThemeVariant.
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Which modifier the app's command shortcuts use: <c>"auto"</c> (Cmd on macOS,
    /// Ctrl elsewhere — the default), <c>"windows"</c> (always Ctrl), or <c>"mac"</c>
    /// (always Cmd). A plain string for the same reason as <see cref="Theme"/>; the
    /// App hands it to <c>Nimbus.Ui.Hotkeys.Initialize</c>.
    ///
    /// <para>
    /// The shared hotkey resolver has supported this override since it was extracted,
    /// but kubeNimbus never called <c>Initialize</c> — so the setting existed in code
    /// and was unreachable from the app. This is the property that connects it.
    /// </para>
    /// </summary>
    public string HotkeyScheme { get; set; } = "auto";

    /// <summary>
    /// The face the interface draws in (DESIGN.md rule 22): <c>"auto"</c> (the default,
    /// which is the platform's own face), <c>"system"</c> or <c>"inter"</c>. A plain
    /// string for the same reason as <see cref="Theme"/>; the App maps it to
    /// <c>Nimbus.Ui.Fonts.InterfaceFont</c>.
    ///
    /// <para>
    /// "auto" is the system face, as in pgNimbus, and that was a decision rather than a
    /// carry-over: kubeNimbus had been drawn in Inter on every platform until this setting
    /// existed (through Fluent's own default, not by choice: its main window named a font
    /// resource nothing defined). The two apps sit side by side on one desktop, and one
    /// family in two faces there is the drift the shared design system exists to stop.
    /// Inter stays one choice away for anyone who preferred it.
    /// </para>
    /// </summary>
    public string InterfaceFont { get; set; } = "auto";

    /// <summary>
    /// The monospace family for code, values and identifiers, by name. Null (the default)
    /// is the bundled JetBrains Mono NL. A name that is no longer installed falls back to
    /// the bundled face rather than to a proportional one (<c>NimbusFonts.Mono</c>).
    /// </summary>
    public string? CodeFont { get; set; }

    /// <summary>
    /// The single global "advanced view" switch: whether the sidebar lists every resource
    /// kind, or only the everyday built-ins (no API machinery, no CRDs). <b>On by
    /// default</b>, so nothing is missing until somebody asks for a shorter list.
    ///
    /// <para>
    /// It used to hide content-area controls as well — the list's usage columns, pod
    /// detail's Usage tab, the fleet toggle, both log toolbars, YAML force-apply, the
    /// Helm and RBAC palette entries and a CRD's own priority-1 columns. That answered
    /// a complaint about the sidebar by hiding things everywhere else, and what it hid
    /// was mostly what somebody had deliberately gone looking for; the switch is the
    /// sidebar's alone now.
    /// </para>
    ///
    /// <para>
    /// Moved here from <c>workspace.json</c>: it is a preference, not a description of
    /// the open session.
    /// </para>
    /// </summary>
    public bool IsAdvancedView { get; set; } = true;

    /// <summary>
    /// Whether the cluster tab's resource-catalog sidebar is shown. On by default —
    /// it is the app's primary navigation. Hiding it gives the resource list the whole
    /// content width, which is worth having on a narrow window or when reading a wide
    /// list; the command bar's toggle and the palette both reach it, so it can never
    /// be hidden with no way back (rule 7).
    /// </summary>
    public bool IsSidebarVisible { get; set; } = true;

    /// <summary>
    /// The sidebar's width in DIPs, as the reader last dragged it.
    ///
    /// <para>
    /// A fixed width rather than the proportion of the content area it used to be. A
    /// star column re-divides on every window resize, so a sidebar sized to hold a
    /// kind name at 1280px became a third of a 3840px window holding the same names in
    /// the same 200px of text; the resource list is what should absorb the extra width,
    /// which is what an absolute width gives. It lives beside
    /// <see cref="IsSidebarVisible"/> — same control, same one global value — rather
    /// than in the workspace's per-kind grid layouts, which are keyed by what is being
    /// looked at where this is not.
    /// </para>
    ///
    /// <para>
    /// Clamped by <see cref="Normalized"/> like every other number here: the file is
    /// user-writable, and a width past the window's own is a sidebar with no list
    /// beside it and no visible way back.
    /// </para>
    /// </summary>
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;

    /// <summary>
    /// Sidebar sections expanded on connect, by title. Empty means "use each section's
    /// own default" (<c>SidebarGrouping.IsExpandedByDefault</c> — Config, Cluster and
    /// CRDs start collapsed, because a bare cluster's catalog runs past 100 kinds and
    /// three of the six sections are machinery nobody opened the app to read).
    /// Recording an explicit set here lets someone who *does* live in CRDs stop
    /// re-opening that section every session.
    /// </summary>
    public List<string> ExpandedSidebarSections { get; set; } = [];

    /// <summary>
    /// Kubeconfig files the user pointed the app at through "Open kubeconfig file…",
    /// so the choice survives a restart. <b>Paths only</b> — never the file's contents
    /// and never anything read out of it (CLAUDE.md rule 4): the chain is re-resolved
    /// at load and at connect time exactly as it is for <c>$KUBECONFIG</c> and
    /// <c>~/.kube/config</c>, so a rotated cert or an exec plugin keeps working and
    /// nothing is copied into app storage. A path that has since gone away is reported
    /// as missing by <c>Kubeconfig.CandidatePaths</c> rather than failing the load.
    ///
    /// <para>
    /// This is the one setting with a real UI beyond a switch: the preferences page
    /// lists the paths, adds and removes them, and rescans. It matters because
    /// <c>$KUBECONFIG</c> is not inherited by a GUI launched from Explorer, a shortcut
    /// or the Store, so for many users a picked path is the only route to a cluster.
    /// </para>
    /// </summary>
    public List<string> KubeconfigPaths { get; set; } = [];

    /// <summary>
    /// How many log lines a pod's log pane keeps before trimming the oldest. Was a
    /// fixed 4000. It is a memory/scrollback trade the app cannot make for everyone:
    /// someone reading a crash loop wants far more scrollback than someone watching a
    /// chatty ingress. Clamped by <see cref="Normalized"/> rather than trusted, since
    /// a hand-edited settings file reaches this directly.
    /// </summary>
    public int LogBufferLines { get; set; } = DefaultLogBufferLines;

    /// <summary>
    /// Seconds between <c>metrics.k8s.io</c> polls. Was a fixed 15. This is the one
    /// thing the app polls at all — the metrics API is a point-in-time aggregate over
    /// a ~30s window with no watch endpoint, so there is nothing to stream. Lowering
    /// it does not produce more resolution than metrics-server itself has; raising it
    /// is the useful direction on a large cluster or a metered link.
    /// </summary>
    public int MetricsPollSeconds { get; set; } = DefaultMetricsPollSeconds;

    /// <summary>
    /// Whether deleting a resource requires the two-step confirm. On by default, and
    /// the default is not neutral: this app deletes things in someone's cluster, and a
    /// misclick on a production Deployment is not undoable. Turning it off is a
    /// deliberate choice by someone who has decided they want the speed.
    /// </summary>
    public bool ConfirmDeletes { get; set; } = true;

    /// <summary>
    /// Whether Apply first asks the server what it would do — a <c>dryRun=All</c> apply,
    /// diffed against the object as it stands — and shows that before anything changes.
    /// On by default, for the same reason <see cref="ConfirmDeletes"/> is: a blind apply
    /// into someone's cluster is the mutating action this app performs most often, and
    /// the preview is the only thing that can show a defaulting webhook or another field
    /// manager's conflict *before* the object moves rather than after.
    ///
    /// <para>
    /// It costs one extra round trip per apply and one click. Turning it off restores the
    /// straight-to-apply behaviour, which is a deliberate choice by someone who has
    /// decided they want the speed — again exactly as with the delete confirm.
    /// </para>
    /// </summary>
    public bool PreviewApplies { get; set; } = true;

    /// <summary>
    /// Whether opening logs — L on a row, its logs icon, the context menu, a palette
    /// <c>Logs: …</c> row — maximizes the inspector over the list rather than docking it
    /// in the ~300px split. Off by default, because the split keeps the list in view and
    /// the list is where you chose the pod; on for someone who reads logs far back and on
    /// long lines, for whom the maximize was a second click every single time. Shift+L and
    /// a Shift+click on the logs icon maximize whatever this says.
    ///
    /// <para>
    /// Read by <c>ClusterTabViewModel.OpenLogsForAsync</c> at the moment logs open, not
    /// cached, so turning it on applies to the next open with nothing to restart.
    /// </para>
    /// </summary>
    public bool OpenLogsMaximized { get; set; }

    /// <summary>
    /// Whether the log panes print each line's timestamp. It used to be a per-pane toggle
    /// that reset on every pod opened, so someone who always reads with timestamps turned
    /// them on again for every pane. The last choice made in any log pane — pod detail's,
    /// the multi-pod pane's, the application page's — is what the next one opens with.
    ///
    /// <para>
    /// These three are the log panes' <em>display</em> toggles, and the line is drawn
    /// deliberately: nothing that changes <em>which</em> lines are read is persisted.
    /// Previous (the crashed run) is the case that proves it — Freelens persisted it and
    /// had to take it back (freelens#2095, #2096), because it made a crashed run's logs
    /// the default view of every healthy pod. The search text and the level filter are not
    /// persisted either: a filter carried into the next pane hides lines in a pane that
    /// never showed you it was filtering.
    /// </para>
    /// </summary>
    public bool LogShowTimestamps { get; set; }

    /// <summary>Whether the log panes wrap long lines. See <see cref="LogShowTimestamps"/>.</summary>
    public bool LogWrapLines { get; set; }

    /// <summary>
    /// Whether log timestamps are printed as the server sent them (RFC3339 UTC) rather than
    /// in this machine's local time. Local is the default because that is the clock the
    /// reader's incident timeline, chat and dashboards are in; UTC is kept one click away
    /// because it is what every other system's logs are in. Copy and Download always write
    /// the server's own line, whichever this says. See <see cref="LogShowTimestamps"/>.
    /// </summary>
    public bool LogTimestampsUtc { get; set; }

    /// <summary>Default for <see cref="LogBufferLines"/>, and the value the app shipped with.</summary>
    public const int DefaultLogBufferLines = 4000;

    /// <summary>Default for <see cref="MetricsPollSeconds"/>, and the value the app shipped with.</summary>
    public const int DefaultMetricsPollSeconds = 15;

    /// <summary>
    /// A copy with every numeric setting clamped into a range the app can actually
    /// honour. The settings file is plain JSON in a user-writable directory, so these
    /// arrive unvalidated: a hand-edited <c>MetricsPollSeconds: 0</c> would spin a
    /// timer as fast as the dispatcher allows and hammer the API server, and a
    /// <c>LogBufferLines: 100000000</c> would exhaust memory on a chatty pod. Clamping
    /// on read (rather than rejecting the file) keeps every other setting in it.
    /// </summary>
    public AppSettings Normalized() => this with
    {
        Theme = Canonical(Theme, "system", "light", "dark", "system"),
        HotkeyScheme = Canonical(HotkeyScheme, "auto", "windows", "mac", "auto"),
        InterfaceFont = Canonical(InterfaceFont, "auto", "auto", "system", "inter"),
        // Blank is the bundled face, the same as null, so the preferences page shows one
        // selection for both. Anything else is a family name and is kept as written: one
        // that does not resolve falls back to the bundled face at draw time.
        CodeFont = string.IsNullOrWhiteSpace(CodeFont) ? null : CodeFont.Trim(),
        ExpandedSidebarSections = ExpandedSidebarSections ?? [],
        KubeconfigPaths = KubeconfigPaths ?? [],
        SidebarWidth = double.IsFinite(SidebarWidth)
            ? Math.Clamp(SidebarWidth, MinSidebarWidth, MaxSidebarWidth)
            : DefaultSidebarWidth,
        LogBufferLines = Math.Clamp(LogBufferLines, MinLogBufferLines, MaxLogBufferLines),
        MetricsPollSeconds = Math.Clamp(MetricsPollSeconds, MinMetricsPollSeconds, MaxMetricsPollSeconds),
    };

    /// <summary>
    /// Lower-cases <paramref name="value"/> and keeps it only if it is one of
    /// <paramref name="allowed"/>, falling back to <paramref name="fallback"/>.
    ///
    /// <para>
    /// Case-insensitive because the file is hand-editable and because this app has
    /// already written the wrong casing into it once: the command bar's theme toggle
    /// persisted the ThemeVariant names ("Dark"/"Light"), which this method rejected
    /// and so silently reset to "system". Reading "Dark" as dark costs nothing and
    /// means those files recover on the next launch instead of losing the choice.
    /// </para>
    /// </summary>
    private static string Canonical(string? value, string fallback, params string[] allowed)
    {
        if (value is null)
        {
            return fallback;
        }

        var lower = value.ToLowerInvariant();
        return Array.IndexOf(allowed, lower) >= 0 ? lower : fallback;
    }

    /// <summary>
    /// What the sidebar opens at. 224 DIPs holds the longest built-in kind label
    /// ("PodDisruptionBudgets") plus its icon and count badge, which is the width the
    /// panel exists to have; the star width it replaced took ~24% of the content area,
    /// i.e. over 900px on a 3840px window, to show the same text.
    /// </summary>
    public const double DefaultSidebarWidth = 224;

    /// <summary>Narrower than this and the filter box has no room for a query.</summary>
    public const double MinSidebarWidth = 150;

    /// <summary>Wider than this and the resource list is the panel, not the sidebar.</summary>
    public const double MaxSidebarWidth = 520;

    /// <summary>Below this the pane cannot hold one screen of a chatty container.</summary>
    public const int MinLogBufferLines = 200;

    /// <summary>Above this the pane's own memory becomes the problem it was meant to bound.</summary>
    public const int MaxLogBufferLines = 200_000;

    /// <summary>Faster than metrics-server's own ~15s scrape produces no new data, only load.</summary>
    public const int MinMetricsPollSeconds = 5;

    /// <summary>Ten minutes: past this the "live" readout is not live in any useful sense.</summary>
    public const int MaxMetricsPollSeconds = 600;
}
