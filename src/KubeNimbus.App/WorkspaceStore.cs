using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubeNimbus.App;

/// <summary>One remembered cluster tab. Only a context name + kubeconfig path — re-resolved
/// through the kubeconfig chain on restore, never a credential (CLAUDE.md rule #4).</summary>
public sealed record TabSnapshot(string ContextName, string KubeconfigPath);

/// <summary>
/// Persisted shell state. Everything added after the initial (Theme, Tabs) pair is
/// nullable and normalized in <see cref="WorkspaceStore.Load"/>: a workspace.json
/// written by an older build simply has no such property, and a non-nullable
/// collection would deserialize to null and NRE on first use.
///
/// Contexts are keyed by name alone — kubeconfig merge semantics already make
/// context names unique across the whole chain (<c>Kubeconfig.LoadContextsAsync</c>
/// dedupes on exactly that), so the path adds nothing here and would only break
/// the key when a file moves.
/// </summary>
public sealed record WorkspaceSettings(
    string? Theme,
    List<TabSnapshot> Tabs,
    /// <summary>Contexts pinned to the top of the switcher, in user order.</summary>
    List<string>? PinnedContexts = null,
    /// <summary>Most-recently-opened contexts, newest first. Capped on write.</summary>
    List<string>? RecentContexts = null,
    /// <summary>Context name → <see cref="KubeNimbus.Core.ClusterEnvironment"/> name, where
    /// the user has corrected (or supplied) the guess. Always wins over the heuristic.</summary>
    Dictionary<string, string>? EnvironmentOverrides = null,
    /// <summary>
    /// The single global "advanced view" switch: off (the default) hides the
    /// controls that only a fraction of sessions ever need — usage columns, the
    /// fleet toggle, the log toolbar's wrap/copy/download, exec's Send, YAML
    /// force-apply, the sidebar's count badges and the Helm/RBAC palette entries.
    /// One boolean rather than a preferences page of them, because the complaint
    /// it answers ("too much stuff for every Kubernetes type") is about the whole
    /// surface, not about any one control.
    ///
    /// Nullable with a null default like everything else added after
    /// <c>(Theme, Tabs)</c>: a workspace.json written before this shipped simply
    /// has no such property, and <see cref="WorkspaceStore.Normalize"/> settles it
    /// on <c>false</c> rather than letting the JSON layer decide.
    /// </summary>
    bool? IsAdvancedView = null,
    /// <summary>
    /// Kubeconfig files the user pointed the app at through "Open kubeconfig file…",
    /// so the choice survives a restart. <b>Paths only</b> — never the file's contents
    /// and never anything read out of it (CLAUDE.md rule #4): the chain is re-resolved
    /// at load and at connect time exactly as it is for $KUBECONFIG and ~/.kube/config,
    /// so a rotated cert or an exec plugin keeps working and nothing is copied into app
    /// storage. A path that has since gone away is reported as missing by
    /// <c>Kubeconfig.CandidatePaths</c> rather than failing the load.
    /// </summary>
    List<string>? KubeconfigPaths = null);

[JsonSerializable(typeof(WorkspaceSettings))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;

/// <summary>
/// Persists the theme choice and the open cluster tabs so a restart restores
/// the workspace. Source-generated JSON (<see cref="WorkspaceJsonContext"/>)
/// keeps this NativeAOT/trim-safe — no reflection-based serialization.
/// </summary>
public static class WorkspaceStore
{
    /// <summary>Recents past this are noise — the switcher's search covers the long tail.</summary>
    public const int MaxRecentContexts = 8;

    private static WorkspaceSettings Empty => new(null, [], [], [], [], false, []);

    /// <summary>
    /// Overrides where the workspace is read from and written to. Set by the
    /// screenshot harness, which builds real <c>MainWindowViewModel</c>s and would
    /// otherwise read — and, as soon as a scenario pins a cluster, overwrite — the
    /// developer's actual workspace while rendering fixtures.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    private static string FilePath => Path.Combine(
        DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kubeNimbus"),
        "workspace.json");

    public static WorkspaceSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Empty;
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceSettings);
            return settings is null ? Empty : Normalize(settings);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>
    /// Fills in the properties a file written by an older build has no entry
    /// for. <see cref="WorkspaceSettings.Tabs"/> is non-nullable in the record but
    /// still deserializes to null from such a file, so it is normalized too.
    /// </summary>
    private static WorkspaceSettings Normalize(WorkspaceSettings settings) => settings with
    {
        Tabs = settings.Tabs ?? [],
        PinnedContexts = settings.PinnedContexts ?? [],
        RecentContexts = settings.RecentContexts ?? [],
        EnvironmentOverrides = settings.EnvironmentOverrides ?? [],

        // Off is the default and the whole point: an existing workspace must not
        // silently opt into the busy layout just because it predates the switch.
        IsAdvancedView = settings.IsAdvancedView ?? false,
        KubeconfigPaths = settings.KubeconfigPaths ?? [],
    };

    public static void Save(WorkspaceSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, WorkspaceJsonContext.Default.WorkspaceSettings);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception)
        {
            // Best-effort persistence — a failed save shouldn't crash the app.
        }
    }
}
