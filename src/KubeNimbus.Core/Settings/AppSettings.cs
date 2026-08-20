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
    /// The single global "advanced view" switch: whether the sidebar shows the resource
    /// sections most sessions never open — Cluster (the API machinery) and CRDs (the
    /// catalog's long tail). <b>On by default</b>, so nothing is missing until somebody
    /// asks for a shorter list.
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
