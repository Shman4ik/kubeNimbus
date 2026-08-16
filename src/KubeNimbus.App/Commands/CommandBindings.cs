using System.Windows.Input;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App;

/// <summary>
/// The App-side half of <see cref="CommandCatalog"/>: it turns the catalog's UI-free
/// descriptors into real Avalonia gestures, and resolves each <see cref="CommandId"/>
/// that needs one to the view-model command that runs it. Everything that used to
/// hardcode a gesture — the window's key bindings, the palette's shortcut captions,
/// the F1 sheet — goes through here, so a shortcut is stated once in Core and rendered
/// everywhere from that.
/// </summary>
public static class CommandBindings
{
    static CommandBindings()
    {
        // The catalog lives in Core and cannot see this map, so the "every bound
        // command has a resolver" half of the contract is checked here, once, at first
        // use — a missing entry fails loudly at startup instead of as a dead key
        // someone reports months later.
        //
        // Checked for WindowBinding only, and that is narrower than pgNimbus's
        // equivalent on purpose. Most of this app's palette rows are *conditional*:
        // logs, exec, port-forward, YAML and delete exist only while a pod row is
        // selected, the fleet toggle only with more than one cluster connected. They
        // are built as closures over the selected tab in BuildPaletteItems rather than
        // as shell-level ICommands, because a palette entry that matches a search and
        // then refuses to run is worse than no match. What they take from the catalog
        // is their title and shortcut text, not their invocation.
        var unmapped = CommandCatalog.All
            .Where(d => d.In(CommandSurface.WindowBinding) && !Resolvers.ContainsKey(d.Id))
            .Select(d => d.Id.ToString())
            .ToList();

        if (unmapped.Count > 0)
        {
            throw new InvalidOperationException(
                "CommandCatalog entries bound to a key but with no resolver in CommandBindings: "
                + string.Join(", ", unmapped));
        }
    }

    /// <summary>
    /// Maps a catalog key onto Avalonia's. All but a handful share a name with their
    /// <see cref="Key"/> counterpart, so only the exceptions are listed; an unmapped
    /// key throws loudly rather than silently producing a dead shortcut.
    /// </summary>
    public static Key ToKey(CommandKey key) => key switch
    {
        CommandKey.Backspace => Key.Back,
        CommandKey.Comma => Key.OemComma,
        CommandKey.Slash => Key.OemQuestion,
        CommandKey.Plus => Key.OemPlus,
        CommandKey.Minus => Key.OemMinus,
        _ => Enum.TryParse<Key>(key.ToString(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"No Avalonia Key mapping for CommandKey.{key}."),
    };

    /// <summary>
    /// Resolves the abstract modifiers against the live Ctrl/Cmd scheme.
    /// <see cref="ChordModifiers.Control"/> stays literal Ctrl on every platform — the
    /// exec pane's interrupt and end-of-input are terminal control characters, not app
    /// shortcuts.
    /// </summary>
    public static KeyModifiers ToModifiers(ChordModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(ChordModifiers.Command))
        {
            result |= Hotkeys.Primary;
        }

        if (modifiers.HasFlag(ChordModifiers.Control))
        {
            result |= KeyModifiers.Control;
        }

        if (modifiers.HasFlag(ChordModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ChordModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }

        return result;
    }

    public static KeyGesture ToGesture(Chord chord) => new(ToKey(chord.Key), ToModifiers(chord.Modifiers));

    /// <summary>The primary gesture for a command, or null when it has none.</summary>
    public static KeyGesture? GestureFor(CommandId id) =>
        CommandCatalog.ChordFor(id) is { } chord ? ToGesture(chord) : null;

    /// <summary>
    /// Whether a key event is exactly this command's primary chord. Used where a
    /// <c>KeyBinding</c> cannot express the behaviour — focus moves, panes that bind
    /// the physical key themselves — and the gesture still has to match the catalog.
    /// </summary>
    public static bool Matches(CommandId id, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return CommandCatalog.ChordFor(id) is { } chord
               && e.Key == ToKey(chord.Key)
               && e.KeyModifiers == ToModifiers(chord.Modifiers);
    }

    /// <summary>
    /// The one-line shortcut caption for a command, resolved against the live scheme —
    /// "Ctrl+P" or "Cmd+P", or a gesture note like "Double-click".
    /// </summary>
    public static string? ShortcutLabel(CommandId id) =>
        CommandCatalog.Get(id).ShortcutLabel(Hotkeys.PrimaryLabel);

    /// <summary>The command a catalog entry invokes; null while its target isn't available yet.</summary>
    public static ICommand? Resolve(CommandId id, MainWindowViewModel vm) =>
        Resolvers.TryGetValue(id, out var resolve)
            ? resolve(vm)
            : throw new InvalidOperationException(
                $"CommandId.{id} is bound to a key but has no resolver in CommandBindings.");

    /// <summary>Every command that gets a window-level key binding, with its gesture.</summary>
    public static IEnumerable<(CommandId Id, KeyGesture Gesture)> WindowBindings()
    {
        foreach (var descriptor in CommandCatalog.On(CommandSurface.WindowBinding))
        {
            if (GestureFor(descriptor.Id) is { } gesture)
            {
                yield return (descriptor.Id, gesture);
            }
        }
    }

    /// <summary>
    /// Fills a window's <c>KeyBindings</c> from the catalog, resolved against the live
    /// Ctrl/Cmd scheme. Called once at construction and again on every
    /// <c>Hotkeys.Changed</c>.
    ///
    /// <para>
    /// <b>The list is cleared, never appended to</b>, and that is the whole reason this
    /// is a method of its own rather than a loop inside the window. A rebuild that added
    /// to what was already there would leave the old modifier's gestures registered
    /// beside the new ones, so Ctrl+K would keep working after someone chose Cmd — a
    /// preference that reads as having done nothing, with no error anywhere. Pulling it
    /// out of the window is what lets <c>HotkeySchemeTests</c> drive two rebuilds over
    /// one list and assert the first scheme's chords are gone; a <c>MainWindow</c> needs
    /// a running Application and cannot be constructed from a view-model test.
    /// </para>
    ///
    /// <para>
    /// The commands come from the caller because several of them act on the window
    /// itself (moving focus into a control is not something an <c>ICommand</c> on a view
    /// model can express). Ctrl/Cmd+1…9 is built here in a loop rather than as nine XAML
    /// bindings, for UI rule 4 — the modifier has to come from <see cref="Hotkeys.Primary"/>
    /// on every rebuild, and a range is a catalog gesture *note* rather than a chord, so
    /// it has no descriptor to iterate.
    /// </para>
    /// </summary>
    public static void RebuildWindowBindings(
        IList<KeyBinding> bindings,
        Func<CommandId, ICommand> commandFor,
        Func<int, ICommand> tabJumpCommandFor)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(commandFor);
        ArgumentNullException.ThrowIfNull(tabJumpCommandFor);

        bindings.Clear();

        foreach (var (id, gesture) in WindowBindings())
        {
            bindings.Add(new KeyBinding { Gesture = gesture, Command = commandFor(id) });
        }

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            bindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D0 + ordinal, Hotkeys.Primary),
                Command = tabJumpCommandFor(ordinal),
            });
        }
    }

    // Deliberately a lookup rather than a switch over the whole enum: the catalog also
    // holds documentation-only rows (double-click, Ctrl+C in the terminal, drag a tab)
    // that have no view-model command at all, and those must never reach Resolve.
    private static readonly Dictionary<CommandId, Func<MainWindowViewModel, ICommand?>> Resolvers = new()
    {
        [CommandId.ClusterSwitcher] = vm => vm.OpenSwitcherCommand,
        [CommandId.ToggleSidebar] = vm => vm.ToggleSidebarCommand,
        [CommandId.Preferences] = vm => vm.ShowPreferencesCommand,
        [CommandId.ShortcutsWindow] = vm => vm.ToggleShortcutsCommand,

        // SelectedTab settles after construction and changes on every tab switch, so
        // these resolve through it each time rather than being captured.
        [CommandId.RefreshList] = vm => vm.SelectedTab?.RefreshCommand,

        // Handled by the window's own key handler rather than a bound ICommand: both
        // move focus into a control, which is not something a command can express. The
        // entries exist so the gesture is still stated once, in the catalog.
        [CommandId.CommandPalette] = _ => null,
        [CommandId.FilterList] = _ => null,
    };
}
