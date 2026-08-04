namespace KubeNimbus.Core.Commands;

/// <summary>
/// Stable identity for every command and documented gesture in the app. The name is
/// the id used in generated docs, so renaming one is a doc-visible change — treat it
/// like renaming a public API.
/// </summary>
public enum CommandId
{
    // --- Clusters ---
    ClusterSwitcher,
    NewClusterTab,
    CloseClusterTab,
    GoToTabByNumber,
    OpenDemoCluster,
    OpenKubeconfigFile,
    RescanKubeconfig,
    ToggleFleetView,
    SetEnvironment,
    ReorderTabs,

    // --- Resources ---
    FilterList,
    FilterSidebar,
    OpenResource,
    QuickPeek,
    DefaultAction,
    RowContextMenu,
    EditYaml,
    ApplyYaml,
    DeleteResource,
    RefreshList,

    // --- Pods ---
    PodLogs,
    PreviousLogs,
    FollowLogs,
    Exec,
    PortForward,
    ExecInterrupt,
    ExecEndInput,
    ExecComplete,

    // --- Cluster tools ---
    HelmReleases,
    AccessReview,
    WhoCan,

    // --- View & app ---
    CommandPalette,
    ToggleAdvancedView,
    ToggleSidebar,
    MaximizeInspector,
    ToggleTheme,
    Preferences,
    ShortcutsWindow,
    About,
}

/// <summary>
/// Which keyboard context owns a gesture. Global gestures reach the main window;
/// the others are handled by their pane's own key handler and only have to be unique
/// within that pane — Enter means "open the selected resource" in the list and
/// "send the line" in the exec terminal, and both are correct.
/// </summary>
public enum CommandScope
{
    Global,
    List,
    Exec,
    Editor,
}

/// <summary>Cheat-sheet / documentation section a command is filed under.</summary>
public enum CommandCategory
{
    Clusters,
    Resources,
    Pods,
    Tools,
    View,
}

/// <summary>
/// Where a descriptor surfaces. Note there is no flag for the pane-level gestures
/// (Ctrl+C in the exec terminal, Space to peek, double-click to open): those are
/// handled inside their own pane's key handler, so no surface projection applies to
/// them — they only need <see cref="CheatSheet"/> so they appear in F1 and the docs.
/// </summary>
[Flags]
public enum CommandSurface
{
    None = 0,

    /// <summary>Gets a <c>KeyBinding</c> on the main window.</summary>
    WindowBinding = 1,

    /// <summary>Listed in the Ctrl/Cmd+K command palette.</summary>
    Palette = 2,

    /// <summary>Listed in the F1 cheat sheet and the generated docs.</summary>
    CheatSheet = 4,
}

/// <summary>
/// One row of the app's command catalog: what it is called, where it shows up, and
/// which keys invoke it. Everything that used to be duplicated across the window's
/// key bindings, the palette and the F1 sheet is stated here exactly once.
/// </summary>
public sealed record CommandDescriptor
{
    public required CommandId Id { get; init; }

    /// <summary>The command-palette label — the long, searchable one.</summary>
    public required string Title { get; init; }

    /// <summary>A shorter label for the cheat sheet and docs; falls back to <see cref="Title"/>.</summary>
    public string? CheatTitle { get; init; }

    public required CommandCategory Category { get; init; }

    /// <summary>Which key handler owns this gesture; see <see cref="CommandScope"/>.</summary>
    public CommandScope Scope { get; init; } = CommandScope.Global;

    /// <summary>
    /// The icon geometry key for the palette row, resolved against the app's merged
    /// resource dictionaries. A key rather than a glyph because this app draws MDI
    /// vectors, not emoji — see the note on Icons.axaml about tofu boxes on Linux.
    /// </summary>
    public string IconKey { get; init; } = "CubeOutlineIconGeometry";

    /// <summary>The primary key combination; null for palette-only actions.</summary>
    public Chord? Chord { get; init; }

    /// <summary>A second accepted combination.</summary>
    public Chord? AltChord { get; init; }

    /// <summary>
    /// Free text for gestures that are not a chord at all ("Double-click", "Drag tab")
    /// or a range too wide to enumerate ("{cmd}+1 … {cmd}+9"). Rendered as quiet text
    /// next to (or instead of) the key caps; "{cmd}" is substituted with the resolved
    /// Ctrl/Cmd label.
    /// </summary>
    public string? GestureNote { get; init; }

    public CommandSurface Surfaces { get; init; } = CommandSurface.CheatSheet;

    /// <summary>The label to show outside the palette.</summary>
    public string DisplayName => CheatTitle ?? Title;

    public bool In(CommandSurface surface) => Surfaces.HasFlag(surface);

    /// <summary><see cref="GestureNote"/> with "{cmd}" resolved to "Ctrl" or "Cmd".</summary>
    public string? GestureNoteFor(string commandLabel) =>
        GestureNote?.Replace("{cmd}", commandLabel, StringComparison.Ordinal);

    /// <summary>
    /// The one-line shortcut text for the palette's trailing column:
    /// "Ctrl+F / Alt+F", or null when there is nothing to show.
    /// </summary>
    public string? ShortcutLabel(string commandLabel)
    {
        var parts = new List<string>(3);
        if (Chord is { } chord)
        {
            parts.Add(chord.Label(commandLabel));
        }

        if (AltChord is { } alt)
        {
            parts.Add(alt.Label(commandLabel));
        }

        if (GestureNoteFor(commandLabel) is { Length: > 0 } note)
        {
            parts.Add(note);
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}
