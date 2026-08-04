using Avalonia.Input;

namespace KubeNimbus.App;

/// <summary>
/// This app's gestures. The modifier itself and the labelling come from
/// <see cref="Nimbus.Ui.Hotkeys"/>, shared with pgNimbus; what lives here is the
/// set of chords kubeNimbus binds and the cheat sheet describing them.
/// <para>
/// No Ctrl gesture is hardcoded in a view — including the ones built in a loop
/// (Ctrl/Cmd+1…9 for tab jumps come from <see cref="Primary"/> in code-behind).
/// </para>
/// </summary>
public static class Hotkeys
{
    /// <summary>Cmd on macOS, Ctrl everywhere else. Forwarded from the shared resolver.</summary>
    public static KeyModifiers Primary => Nimbus.Ui.Hotkeys.Primary;

    public static readonly KeyGesture CommandPalette = new(Key.K, Primary);

    /// <summary>
    /// Cluster switcher. Separate from the palette on purpose: switching cluster is
    /// the single most frequent navigation in a multi-cluster session, and every
    /// comparable tool gives it its own gesture (k9s <c>:ctx</c>, kubectx) rather
    /// than burying it among every other command.
    /// </summary>
    public static readonly KeyGesture ClusterSwitcher = new(Key.P, Primary);

    /// <summary>
    /// Focus the resource list's search box. Its own gesture rather than a palette
    /// entry because it is the find-in-list every application has bound to this chord,
    /// and typing a pod name is the fastest way through a 200-row namespace.
    /// </summary>
    public static readonly KeyGesture FilterList = new(Key.F, Primary);

    public static readonly KeyGesture ShortcutsHelp = new(Key.F1);

    /// <summary>"Cmd" on macOS, "Ctrl" elsewhere — for describing chords built by hand.</summary>
    public static string PrimaryLabel => Nimbus.Ui.Hotkeys.PrimaryLabel;

    /// <summary>Human-readable label for a gesture, for palette rows and the cheat sheet.</summary>
    public static string Describe(KeyGesture gesture) => Nimbus.Ui.Hotkeys.Describe(gesture);

    /// <summary>
    /// One row of the cheat sheet. Re-exported from the shared type so views can keep
    /// saying <c>Hotkeys.ShortcutEntry</c> without also knowing where it comes from.
    /// </summary>
    public sealed record ShortcutEntry(string Action, string Keys);

    /// <summary>Single source of truth for the F1 cheat sheet (ShortcutsOverlay) —
    /// every entry here is a real gesture or interaction handled somewhere in the
    /// app, not aspirational documentation.</summary>
    public static readonly IReadOnlyList<ShortcutEntry> CheatSheet =
    [
        new("Switch or open a cluster", Describe(ClusterSwitcher)),
        new("Jump to cluster tab 1–9", $"{PrimaryLabel}+1…9"),
        new("Open the command palette", Describe(CommandPalette)),
        new("Show this cheat sheet", Describe(ShortcutsHelp)),
        new("Search the resource list by name", Describe(FilterList)),
        new("Open the selected resource", "Enter"),
        new("Quick-peek the selected resource", "Space"),
        new("Default action (pod → logs, resource → YAML, …)", "Double-click"),
        new("Logs, exec, port-forward, YAML, delete", "Right-click a row"),
        // Literal "Ctrl", NOT PrimaryLabel — the one place in this file where the
        // platform rule is deliberately not applied. ^C and ^D are terminal control
        // characters, and a terminal's interrupt is Control on macOS too; Cmd+C there
        // is Copy, which is exactly what ExecView leaves it as.
        new("Interrupt a command in the exec pane", "Ctrl+C"),
        new("End input / exit the shell", "Ctrl+D"),
        new("Complete a command in the exec pane", "Tab"),
        new("Reorder cluster tabs", "Drag tab"),
        new("Filter the sidebar's resource kinds", "Type in the filter box"),
        new("Show or hide the advanced controls", "The sliders icon, top of the sidebar"),
        new("Maximize the inspector over the list", "Inspector's expand icon"),
    ];
}
