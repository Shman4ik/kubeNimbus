using KubeNimbus.Core.Commands;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Guards the one property the catalog exists to provide: that every surface (key
/// bindings, palette, F1 sheet, docs) is describing the same set of commands, with no
/// two gestures fighting over the same keys.
///
/// <para>
/// These need no cluster, so unlike most of this suite they run everywhere.
/// </para>
/// </summary>
public class CommandCatalogTests
{
    [Test]
    public async Task EveryIdAppearsExactlyOnce()
    {
        var duplicates = CommandCatalog.All
            .GroupBy(d => d.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();

        await Assert.That(duplicates).IsEmpty();
    }

    [Test]
    public async Task EveryCommandIdIsInTheCatalog()
    {
        // A new CommandId with no descriptor would be invisible on every surface —
        // no binding, no palette row, no line in the docs — and nothing else would say so.
        var missing = Enum.GetValues<CommandId>()
            .Where(id => CommandCatalog.All.All(d => d.Id != id))
            .Select(id => id.ToString())
            .ToList();

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task NoTwoCommandsInTheSameScopeShareAChord()
    {
        var clashes = CommandCatalog.All
            .SelectMany(d => Chords(d).Select(c => (d.Scope, Chord: c, d.Id)))
            .GroupBy(x => (x.Scope, x.Chord))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Scope}/{g.Key.Chord.Label(ChordScheme.Ctrl)}: {string.Join(", ", g.Select(x => x.Id))}")
            .ToList();

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    public async Task PaneChordsCarryingTheCommandModifierDontShadowGlobalOnes()
    {
        // A pane-scoped gesture that includes Ctrl/Cmd bubbles up to the window's
        // KeyBindings, so it must not collide with a global one even though the scopes
        // differ. A bare key (Enter in the list, Tab in the terminal) does not.
        var global = CommandCatalog.All
            .Where(d => d.Scope == CommandScope.Global)
            .SelectMany(d => Chords(d).Select(c => (Chord: c, d.Id)))
            .ToList();

        var clashes = CommandCatalog.All
            .Where(d => d.Scope != CommandScope.Global)
            .SelectMany(d => Chords(d).Select(c => (Chord: c, d.Id)))
            .Where(pane => pane.Chord.Modifiers.HasFlag(ChordModifiers.Command))
            .SelectMany(pane => global
                .Where(g => g.Chord == pane.Chord)
                .Select(g => $"{pane.Chord.Label(ChordScheme.Ctrl)}: {pane.Id} vs {g.Id}"))
            .ToList();

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    public async Task EveryBoundCommandHasAChord()
    {
        // WindowBinding says "this gets a KeyBinding". Without a chord there is nothing
        // to bind, and the App layer would throw at startup building the bindings.
        var chordless = CommandCatalog.On(CommandSurface.WindowBinding)
            .Where(d => d.Chord is null)
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(chordless).IsEmpty();
    }

    [Test]
    public async Task EveryCheatSheetRowSaysHowToInvokeIt()
    {
        // A cheat-sheet row with neither a chord nor a gesture note is a line that
        // names an action and then does not say what to press — which is the one thing
        // a cheat sheet must never do.
        var silent = CommandCatalog.On(CommandSurface.CheatSheet)
            .Where(d => d.Chord is null && d.AltChord is null && string.IsNullOrEmpty(d.GestureNote))
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(silent).IsEmpty();
    }

    [Test]
    public async Task TerminalControlKeysStayLiteralCtrl()
    {
        // ^C and ^D are terminal control characters, not app shortcuts: they are
        // Control on macOS too, and Cmd+C there is Copy. Rendering them through the
        // primary modifier would print "Cmd+C" in the sheet for a key that sends an
        // interrupt — and would claim Copy's chord.
        foreach (var id in new[] { CommandId.ExecInterrupt, CommandId.ExecEndInput })
        {
            var chord = CommandCatalog.Get(id).Chord;

            await Assert.That(chord.HasValue).IsTrue();
            await Assert.That(chord!.Value.Modifiers.HasFlag(ChordModifiers.Control)).IsTrue();
            await Assert.That(chord.Value.Modifiers.HasFlag(ChordModifiers.Command)).IsFalse();

            // And it must say Control under both schemes: "Ctrl+C", and "⌃C" on the Cmd
            // scheme, never "⌘C".
            await Assert.That(chord.Value.Label(ChordScheme.Ctrl)).StartsWith("Ctrl");
            await Assert.That(chord.Value.Label(ChordScheme.Cmd)).StartsWith("⌃");
        }
    }

    [Test]
    public async Task EveryCategoryRendersAtLeastOneRow()
    {
        var sections = CommandCatalog.CheatSheetSections().ToList();

        await Assert.That(sections).IsNotEmpty();
        foreach (var (_, rows) in sections)
        {
            await Assert.That(rows).IsNotEmpty();
        }
    }

    private static IEnumerable<Chord> Chords(CommandDescriptor descriptor)
    {
        if (descriptor.Chord is { } chord)
        {
            yield return chord;
        }

        if (descriptor.AltChord is { } alt)
        {
            yield return alt;
        }

        foreach (var more in descriptor.MoreChords)
        {
            yield return more.Chord;
        }
    }

    [Test]
    public async Task The_cmd_scheme_spells_chords_with_apple_glyphs_in_apple_order()
    {
        // The 2026-10-17 macOS pass: every label said "Cmd+K", "Cmd+1…9", "(Cmd+S)". A Mac
        // writes ⌘K, and modifiers run ⌃⌥⇧⌘ before the key.
        await Assert.That(CommandCatalog.Get(CommandId.CommandPalette).ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⌘K");
        await Assert.That(CommandCatalog.Get(CommandId.NamespacePicker).ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⇧⌘N");
        await Assert.That(CommandCatalog.Get(CommandId.GoToTabByNumber).ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⌘1 … ⌘9");
        await Assert.That(new Chord(CommandKey.F, ChordModifiers.Command | ChordModifiers.Shift | ChordModifiers.Alt | ChordModifiers.Control)
            .Label(ChordScheme.Cmd)).IsEqualTo("⌃⌥⇧⌘F");

        await Assert.That(CommandCatalog.Get(CommandId.CommandPalette).ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Ctrl+K");
        await Assert.That(CommandCatalog.Get(CommandId.NamespacePicker).ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Ctrl+Shift+N");
        await Assert.That(CommandCatalog.Get(CommandId.GoToTabByNumber).ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Ctrl+1 … Ctrl+9");
    }

    [Test]
    public async Task Gesture_notes_spell_their_keys_per_scheme()
    {
        await Assert.That(CommandCatalog.Get(CommandId.LogSearchOptions).GestureNoteFor(ChordScheme.Cmd))
            .IsEqualTo("⌥R / ⌥C / ⌥P in a log pane's search box");
        await Assert.That(CommandCatalog.Get(CommandId.LogSearchOptions).GestureNoteFor(ChordScheme.Ctrl))
            .IsEqualTo("Alt+R / Alt+C / Alt+P in a log pane's search box");
        await Assert.That(CommandCatalog.Get(CommandId.LogSearchStep).GestureNoteFor(ChordScheme.Cmd))
            .IsEqualTo("↩ / ⇧↩ in a log pane's search box");

        // Every note's placeholders resolve, on both schemes: a typo throws here, not at render.
        foreach (var descriptor in CommandCatalog.All)
        {
            foreach (var scheme in Enum.GetValues<ChordScheme>())
            {
                await Assert.That(descriptor.GestureNoteFor(scheme) ?? "").DoesNotContain("{");
            }
        }
    }

    [Test]
    public async Task Mac_conventions_are_chords_on_the_cmd_scheme_only()
    {
        // ⌘⌫ deletes the selected row on a Mac (it has no Delete key; that is fn+⌫) and is
        // what the menus and the sheet name first there; Delete is still accepted.
        var delete = CommandCatalog.Get(CommandId.DeleteResource);
        await Assert.That(delete.PrimaryChordFor(ChordScheme.Cmd)).IsEqualTo(new Chord(CommandKey.Backspace, ChordModifiers.Command));
        await Assert.That(delete.SynonymsFor(ChordScheme.Cmd)).Contains(new Chord(CommandKey.Delete));
        await Assert.That(delete.ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⌘⌫ / ⌦");
        await Assert.That(delete.ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Delete");

        // ⌘[ goes back from an application's page, beside Esc; nothing on the Ctrl scheme.
        var back = CommandCatalog.Get(CommandId.BackToApplications);
        await Assert.That(back.SynonymsFor(ChordScheme.Cmd)).Contains(new Chord(CommandKey.OpenBracket, ChordModifiers.Command));
        await Assert.That(back.SynonymsFor(ChordScheme.Ctrl).Count()).IsEqualTo(1);
        await Assert.That(back.ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⎋ / ⌘[");
    }
}
