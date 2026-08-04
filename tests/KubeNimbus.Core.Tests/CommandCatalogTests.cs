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
            .Select(g => $"{g.Key.Scope}/{g.Key.Chord.Label("Ctrl")}: {string.Join(", ", g.Select(x => x.Id))}")
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
                .Select(g => $"{pane.Chord.Label("Ctrl")}: {pane.Id} vs {g.Id}"))
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

            // And it must say "Ctrl" under both schemes, not just under the Ctrl one.
            await Assert.That(chord.Value.Label("Cmd")).StartsWith("Ctrl");
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
    }
}
