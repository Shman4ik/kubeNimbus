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
    /// The single global "advanced view" switch. Off by default: it hides the controls
    /// only a fraction of sessions need (usage columns and sparklines, the fleet
    /// toggle, the log toolbar's wrap/copy/download, exec's Send, YAML force-apply,
    /// the sidebar's count badges, the Helm/RBAC palette entries).
    ///
    /// <para>
    /// Moved here from <c>workspace.json</c>: it is a preference, not a description of
    /// the open session. <c>WorkspaceStore</c> still reads its old value once, so
    /// nobody's existing choice is lost in the move.
    /// </para>
    /// </summary>
    public bool IsAdvancedView { get; set; }

    /// <summary>
    /// Whether the cluster tab's resource-catalog sidebar is shown. On by default —
    /// it is the app's primary navigation. Hiding it gives the resource list the whole
    /// content width, which is worth having on a narrow window or when reading a wide
    /// list; the command bar's toggle and the palette both reach it, so it can never
    /// be hidden with no way back (rule 7).
    /// </summary>
    public bool IsSidebarVisible { get; set; } = true;

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
        Theme = Theme is "light" or "dark" or "system" ? Theme : "system",
        HotkeyScheme = HotkeyScheme is "windows" or "mac" or "auto" ? HotkeyScheme : "auto",
        ExpandedSidebarSections = ExpandedSidebarSections ?? [],
        KubeconfigPaths = KubeconfigPaths ?? [],
        LogBufferLines = Math.Clamp(LogBufferLines, MinLogBufferLines, MaxLogBufferLines),
        MetricsPollSeconds = Math.Clamp(MetricsPollSeconds, MinMetricsPollSeconds, MaxMetricsPollSeconds),
    };

    /// <summary>Below this the pane cannot hold one screen of a chatty container.</summary>
    public const int MinLogBufferLines = 200;

    /// <summary>Above this the pane's own memory becomes the problem it was meant to bound.</summary>
    public const int MaxLogBufferLines = 200_000;

    /// <summary>Faster than metrics-server's own ~15s scrape produces no new data, only load.</summary>
    public const int MinMetricsPollSeconds = 5;

    /// <summary>Ten minutes: past this the "live" readout is not live in any useful sense.</summary>
    public const int MaxMetricsPollSeconds = 600;
}
