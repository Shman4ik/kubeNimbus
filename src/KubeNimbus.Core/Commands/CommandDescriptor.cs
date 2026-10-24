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
    AddKubeconfigFolder,
    ReconnectCluster,
    OpenTerminal,
    ToggleFleetView,
    SetEnvironment,
    ReorderTabs,

    // --- Resources ---
    NamespacePicker,
    FilterList,
    FilterSidebar,
    OpenResource,
    QuickPeek,
    DefaultAction,
    RowContextMenu,
    EditYaml,
    ApplyYaml,
    DeleteResource,
    RolloutRestart,
    FilterListFromRows,
    ToggleUnhealthyOnly,
    RefreshList,
    NewYaml,
    OpenYamlFile,
    ResetListColumns,
    ChooseListColumns,

    // --- Pods ---
    PodLogs,
    PodLogsMaximized,
    LogsPalette,
    PreviousLogs,
    FollowLogs,
    LogSearchStep,
    LogSearchOptions,
    LogProblemStep,
    LogLineKeys,
    CopyLogLine,
    Exec,
    PortForward,
    PortForwards,
    ExecInterrupt,
    ExecEndInput,
    ExecComplete,
    ExecCopy,
    ExecPaste,

    // --- Cluster tools ---
    HelmReleases,
    AccessReview,
    WhoCan,

    // --- View & app ---
    ShowApplications,
    ShowResources,
    BackToApplications,
    CommandPalette,
    ToggleAdvancedView,
    ToggleSidebar,
    MaximizeInspector,
    CloseFocusedTab,
    LeaveEditor,
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

    /// <summary>An application's page in the Applications mode (its own key handler).</summary>
    Page,
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

    /// <summary>
    /// A second gesture listed after the first: the other half of a pair (Ctrl+Shift+Tab
    /// beside Ctrl+Tab for leaving the editor backwards). Not accepted by
    /// <c>CommandBindings.Matches</c>, which answers the primary action only.
    /// </summary>
    public Chord? AltChord { get; init; }

    /// <summary>
    /// Further synonyms of <see cref="Chord"/>, each optionally confined to one scheme —
    /// the place for a platform's own convention that the other platform does not share
    /// (⌘⌫ to delete a row, ⌘[ to go back). One marked <see cref="SchemeChord.Primary"/>
    /// is listed first on its scheme and is the gesture tooltips, menus and the search pill
    /// show there; <see cref="Chord"/> then stays accepted as a synonym. The same shape as
    /// pgNimbus's.
    /// </summary>
    public IReadOnlyList<SchemeChord> MoreChords { get; init; } = [];

    /// <summary>
    /// Free text for gestures that are not a chord at all ("Double-click", "Drag tab")
    /// or a range too wide to enumerate ("{cmd}+1 … {cmd}+9"). Rendered as quiet text
    /// next to (or instead of) the key caps. "{cmd}+" is spelled per scheme ("Ctrl+" or
    /// "⌘"), and "{key:Alt+R}" is a chord written with the catalog's own names
    /// (<see cref="Chord.TryParse"/>), spelled per scheme ("Alt+R" or "⌥R").
    /// </summary>
    public string? GestureNote { get; init; }

    public CommandSurface Surfaces { get; init; } = CommandSurface.CheatSheet;

    /// <summary>The label to show outside the palette.</summary>
    public string DisplayName => CheatTitle ?? Title;

    public bool In(CommandSurface surface) => Surfaces.HasFlag(surface);

    /// <summary>
    /// Every chord that invokes this entry on <paramref name="scheme"/>, in display order:
    /// a scheme's promoted chords, then <see cref="Chord"/>, then <see cref="AltChord"/>,
    /// then the remaining synonyms.
    /// </summary>
    public IReadOnlyList<Chord> ChordsFor(ChordScheme scheme)
    {
        var chords = new List<Chord>(2 + MoreChords.Count);
        foreach (var more in MoreChords)
        {
            if (more.Primary && more.AppliesTo(scheme))
            {
                chords.Add(more.Chord);
            }
        }

        if (Chord is { } chord)
        {
            chords.Add(chord);
        }

        if (AltChord is { } alt)
        {
            chords.Add(alt);
        }

        foreach (var more in MoreChords)
        {
            if (!more.Primary && more.AppliesTo(scheme))
            {
                chords.Add(more.Chord);
            }
        }

        return chords;
    }

    /// <summary>
    /// The gesture to name when there is room for one — a tooltip, a menu caption, the
    /// search pill — and the one a window binding and a menu item are built on. Null for
    /// palette-only actions.
    /// </summary>
    public Chord? PrimaryChordFor(ChordScheme scheme)
    {
        foreach (var more in MoreChords)
        {
            if (more.Primary && more.AppliesTo(scheme))
            {
                return more.Chord;
            }
        }

        return Chord;
    }

    /// <summary>
    /// The synonyms of the primary action on <paramref name="scheme"/>: every chord except
    /// <see cref="AltChord"/>, which is a different action. What <c>CommandBindings.Matches</c>
    /// accepts.
    /// </summary>
    public IEnumerable<Chord> SynonymsFor(ChordScheme scheme)
    {
        if (Chord is { } chord)
        {
            yield return chord;
        }

        foreach (var more in MoreChords)
        {
            if (more.AppliesTo(scheme))
            {
                yield return more.Chord;
            }
        }
    }

    /// <summary>
    /// <see cref="GestureNote"/> spelled for <paramref name="scheme"/>: "Ctrl+1 … Ctrl+9" or
    /// "⌘1 … ⌘9", "Alt+R" or "⌥R". A key name longer than one character keeps a visible join
    /// on the Cmd scheme ("⌘ + wheel"), where running the glyph into a word would read as
    /// one token.
    /// </summary>
    public string? GestureNoteFor(ChordScheme scheme) =>
        GestureNote is { } note ? SpellNote(note, scheme) : null;

    /// <summary>
    /// Free text naming keys, spelled for <paramref name="scheme"/> — the rule
    /// <see cref="GestureNoteFor"/> follows, for hints outside the catalog (a footer, a
    /// tooltip) that name a key the catalog has no entry for.
    /// </summary>
    public static string SpellNote(string note, ChordScheme scheme)
    {
        const string Placeholder = "{cmd}+";
        note = SpellKeyPlaceholders(note, scheme);
        if (scheme == ChordScheme.Ctrl)
        {
            return note.Replace("{cmd}", "Ctrl", StringComparison.Ordinal);
        }

        var result = new System.Text.StringBuilder(note.Length);
        var index = 0;
        while (true)
        {
            var at = note.IndexOf(Placeholder, index, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Append(note, index, note.Length - index);
                return result.Replace("{cmd}", "⌘").ToString();
            }

            result.Append(note, index, at - index);
            index = at + Placeholder.Length;

            var end = index;
            while (end < note.Length && !char.IsWhiteSpace(note[end]))
            {
                end++;
            }

            result.Append(end - index <= 1 ? "⌘" : "⌘ + ");
        }
    }

    // "{key:Alt+R}" → "Alt+R" or "⌥R".
    private static string SpellKeyPlaceholders(string note, ChordScheme scheme)
    {
        const string Open = "{key:";
        var at = note.IndexOf(Open, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = note.IndexOf('}', at);
            var text = note[(at + Open.Length)..end];
            var label = Commands.Chord.TryParse(text, out var chord)
                ? chord.Label(scheme)
                : throw new InvalidOperationException($"\"{text}\" in a gesture note is not a chord.");
            note = string.Concat(note.AsSpan(0, at), label, note.AsSpan(end + 1));
            at = note.IndexOf(Open, at + label.Length, StringComparison.Ordinal);
        }

        return note;
    }

    /// <summary>
    /// The one-line shortcut text for the palette's trailing column and the docs:
    /// "Ctrl+Shift+F / Alt+F" or "⇧⌘F / ⌥F", or null when there is nothing to show.
    /// </summary>
    public string? ShortcutLabel(ChordScheme scheme)
    {
        var parts = ChordsFor(scheme).Select(c => c.Label(scheme)).ToList();

        if (GestureNoteFor(scheme) is { Length: > 0 } note)
        {
            parts.Add(note);
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}

/// <summary>One of a descriptor's <see cref="CommandDescriptor.MoreChords"/>.</summary>
/// <param name="Chord">The gesture.</param>
/// <param name="Scheme">The one scheme it belongs to, or null for both.</param>
/// <param name="Primary">
/// Listed first on its scheme, ahead of the descriptor's own chord — the platform's
/// convention outranks the cross-platform default there.
/// </param>
public readonly record struct SchemeChord(Chord Chord, ChordScheme? Scheme = null, bool Primary = false)
{
    public bool AppliesTo(ChordScheme scheme) => Scheme is null || Scheme == scheme;
}
