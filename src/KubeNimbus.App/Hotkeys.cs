using Avalonia.Input;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App;

/// <summary>
/// This app's gestures. The modifier itself and the labelling come from
/// <see cref="Nimbus.Ui.Hotkeys"/>, shared with pgNimbus; the set of chords is
/// <see cref="CommandCatalog"/>, in Core.
///
/// <para>
/// <b>These are properties, not <c>static readonly</c> fields, and that is
/// load-bearing.</b> The Ctrl/Cmd scheme is now a user preference that can change
/// while the app is running, and a <c>KeyGesture</c> captured at type-initialization
/// outlives the setting that produced it — the shared resolver documents exactly this
/// trap, and the old fields here walked straight into it. Each read rebuilds the
/// gesture from the live modifier, and <see cref="Changed"/> is what makes an open
/// window rebuild its bindings.
/// </para>
///
/// <para>
/// No Ctrl gesture is hardcoded in a view — including the ones built in a loop
/// (Ctrl/Cmd+1…9 for tab jumps come from <see cref="Primary"/> in code-behind).
/// The hand-written cheat sheet that used to live here is gone: it is now a
/// projection of the catalog, like the key bindings and the docs page.
/// </para>
/// </summary>
public static class Hotkeys
{
    /// <summary>Cmd on macOS, Ctrl everywhere else — or whatever the preference forces.</summary>
    public static KeyModifiers Primary => Nimbus.Ui.Hotkeys.Primary;

    /// <summary>"Cmd" on macOS, "Ctrl" elsewhere — for describing chords built by hand.</summary>
    public static string PrimaryLabel => Nimbus.Ui.Hotkeys.PrimaryLabel;

    /// <summary>Raised when the scheme changes, so open windows rebuild their bindings.</summary>
    public static event Action? Changed
    {
        add => Nimbus.Ui.Hotkeys.Changed += value;
        remove => Nimbus.Ui.Hotkeys.Changed -= value;
    }

    public static KeyGesture CommandPalette => Gesture(CommandId.CommandPalette);

    /// <summary>
    /// Cluster switcher. Separate from the palette on purpose: switching cluster is the
    /// single most frequent navigation in a multi-cluster session, and every comparable
    /// tool gives it its own gesture (k9s <c>:ctx</c>, kubectx) rather than burying it
    /// among every other command.
    /// </summary>
    public static KeyGesture ClusterSwitcher => Gesture(CommandId.ClusterSwitcher);

    /// <summary>
    /// Focus the resource list's search box. Its own gesture rather than a palette
    /// entry because it is the find-in-list every application has bound to this chord,
    /// and typing a pod name is the fastest way through a 200-row namespace.
    /// </summary>
    public static KeyGesture FilterList => Gesture(CommandId.FilterList);

    public static KeyGesture ShortcutsHelp => Gesture(CommandId.ShortcutsWindow);

    /// <summary>Human-readable label for a gesture, for palette rows and the cheat sheet.</summary>
    public static string Describe(KeyGesture gesture) => Nimbus.Ui.Hotkeys.Describe(gesture);

    /// <summary>
    /// The catalog's gesture for a command. Throws when it has none — a caller naming a
    /// command here is asserting that it has a chord, and a silent null would ship as a
    /// dead key rather than as a build or startup failure.
    /// </summary>
    private static KeyGesture Gesture(CommandId id) =>
        CommandBindings.GestureFor(id)
        ?? throw new InvalidOperationException($"CommandId.{id} has no chord in CommandCatalog.");
}
