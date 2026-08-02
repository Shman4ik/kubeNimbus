using System.Runtime.InteropServices;
using Avalonia.Input;

namespace KubeNimbus.App;

/// <summary>
/// Single source of truth for platform chord modifiers. No Ctrl gesture is
/// hardcoded in views: palette labels and the cheat sheet all derive from here,
/// so Cmd-on-macOS vs Ctrl-elsewhere stays consistent.
/// </summary>
public static class Hotkeys
{
    /// <summary>Cmd on macOS, Ctrl everywhere else.</summary>
    public static KeyModifiers Primary { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? KeyModifiers.Meta : KeyModifiers.Control;

    public static readonly KeyGesture CommandPalette = new(Key.K, Primary);

    /// <summary>
    /// Cluster switcher. Separate from the palette on purpose: switching cluster is
    /// the single most frequent navigation in a multi-cluster session, and every
    /// comparable tool gives it its own gesture (k9s <c>:ctx</c>, kubectx) rather
    /// than burying it among every other command.
    /// </summary>
    public static readonly KeyGesture ClusterSwitcher = new(Key.P, Primary);

    public static readonly KeyGesture ShortcutsHelp = new(Key.F1);

    /// <summary>"Cmd" on macOS, "Ctrl" elsewhere — for describing chords built by hand.</summary>
    public static string PrimaryLabel { get; } = Primary.HasFlag(KeyModifiers.Meta) ? "Cmd" : "Ctrl";

    /// <summary>Human-readable label for a gesture, for palette rows and the cheat sheet.</summary>
    public static string Describe(KeyGesture gesture)
    {
        var mod = gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "Cmd"
            : gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? "Ctrl"
            : "";
        return string.IsNullOrEmpty(mod) ? gesture.Key.ToString() : $"{mod}+{gesture.Key}";
    }

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
        new("Open the selected resource", "Enter"),
        new("Quick-peek the selected resource", "Space"),
        new("Default action (pod → logs, resource → YAML, …)", "Double-click"),
        new("Reorder cluster tabs", "Drag tab"),
        new("Filter the sidebar's resource kinds", "Type in the filter box"),
        new("Maximize the inspector over the list", "Inspector's expand icon"),
    ];
}
