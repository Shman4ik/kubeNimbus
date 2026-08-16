using System.Windows.Input;
using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Ctrl/Cmd scheme is a user preference that changes while the app is running, and
/// four surfaces have to follow it: the window's key bindings, the F1 cheat sheet, every
/// <see cref="CommandTip"/> tooltip, and — the one that fails silently — the *old*
/// gesture, which has to stop working.
///
/// <para>
/// That last one is why these exist. Every other failure here is visible the moment you
/// look: a sheet or a tooltip naming the wrong modifier is on screen, wrong. A rebuild
/// that appends instead of replacing renders nothing at all — the new chord works, the
/// preference looks applied, and the old chord quietly keeps working beside it, which is
/// indistinguishable from the preference doing nothing until someone presses the key they
/// just stopped choosing. VER-3 drove all four against the running app under Xvfb and
/// found them correct; these tests are what stops that going quietly wrong again, since a
/// screenshot cannot show a key that still fires.
/// </para>
///
/// <para>
/// They drive the real methods rather than reproductions:
/// <see cref="CommandBindings.RebuildWindowBindings"/> is the body of
/// <c>MainWindow.BuildKeyBindings</c> (a <c>MainWindow</c> needs a running Application
/// and cannot be constructed here), <see cref="ShortcutsViewModel"/> is what the F1
/// overlay binds, and <see cref="CommandTip.Compose(string?, CommandId?)"/> is what sets
/// every tooltip's text.
/// </para>
///
/// <para>
/// <see cref="Nimbus.Ui.Hotkeys.Primary"/> is process-global, so this class is
/// <c>[NotInParallel]</c> and every test restores "auto" in a finally.
/// </para>
/// </summary>
[NotInParallel]
public class HotkeySchemeTests
{
    /// <summary>A stand-in for the window's own commands: these tests are about gestures.</summary>
    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    private static void Rebuild(IList<KeyBinding> bindings) =>
        CommandBindings.RebuildWindowBindings(bindings, _ => new NoopCommand(), _ => new NoopCommand());

    private static IEnumerable<KeyGesture> Gestures(IEnumerable<KeyBinding> bindings) =>
        bindings.Select(b => b.Gesture).OfType<KeyGesture>();

    private static bool Has(IEnumerable<KeyBinding> bindings, Key key, KeyModifiers modifiers) =>
        Gestures(bindings).Any(g => g.Key == key && g.KeyModifiers == modifiers);

    // ------------------------------------------------ the bindings, and the old gesture

    /// <summary>
    /// The headline case. Rebuilding for a new scheme over a list that already holds the
    /// old one's bindings must <em>replace</em> them: same count, the new modifier
    /// present, the old modifier gone. Appending instead is the silent failure — it
    /// leaves Ctrl+K working after someone chose Cmd, and nothing on screen says so.
    /// </summary>
    [Test]
    public async Task Rebuilding_for_a_new_scheme_replaces_the_old_schemes_bindings()
    {
        try
        {
            var bindings = new List<KeyBinding>();

            Nimbus.Ui.Hotkeys.Initialize("windows");
            Rebuild(bindings);
            var underCtrl = bindings.Count;

            await Assert.That(underCtrl).IsGreaterThan(0);
            await Assert.That(Has(bindings, Key.K, KeyModifiers.Control)).IsTrue();

            Nimbus.Ui.Hotkeys.Initialize("mac");
            Rebuild(bindings);

            // Same list, same size: nothing accumulated.
            await Assert.That(bindings.Count).IsEqualTo(underCtrl);

            // The new chord answers…
            await Assert.That(Has(bindings, Key.K, KeyModifiers.Meta)).IsTrue();

            // …and the old one does not. This is the assertion the item is about.
            await Assert.That(Has(bindings, Key.K, KeyModifiers.Control)).IsFalse();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// Not just the palette: <em>every</em> command-modifier gesture has to move, and
    /// none of the old ones may survive anywhere in the list. A rebuild that missed one
    /// descriptor would leave exactly one stale chord, which is the hardest version of
    /// this bug to notice.
    /// </summary>
    [Test]
    public async Task No_gesture_keeps_the_previous_schemes_modifier()
    {
        try
        {
            var bindings = new List<KeyBinding>();

            Nimbus.Ui.Hotkeys.Initialize("windows");
            Rebuild(bindings);
            var ctrlChords = Gestures(bindings)
                .Where(g => g.KeyModifiers == KeyModifiers.Control)
                .Select(g => g.Key)
                .ToList();

            // The catalog is meant to carry several of these; a zero here would make the
            // rest of this test vacuous.
            await Assert.That(ctrlChords).IsNotEmpty();

            Nimbus.Ui.Hotkeys.Initialize("mac");
            Rebuild(bindings);

            await Assert.That(Gestures(bindings).Any(g => g.KeyModifiers == KeyModifiers.Control)).IsFalse();

            // …and each of them came back under the new modifier, rather than vanishing.
            foreach (var key in ctrlChords)
            {
                await Assert.That(Has(bindings, key, KeyModifiers.Meta)).IsTrue();
            }
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// Ctrl/Cmd+1…9 is built in a loop rather than from a descriptor (a range is a
    /// gesture *note*, not a chord), so it is the one part of the rebuild that could be
    /// left reading a stale modifier while everything else moved — UI rule 4's specific
    /// warning.
    /// </summary>
    [Test]
    public async Task The_tab_jump_range_follows_the_scheme_too()
    {
        try
        {
            var bindings = new List<KeyBinding>();

            Nimbus.Ui.Hotkeys.Initialize("windows");
            Rebuild(bindings);
            await Assert.That(Has(bindings, Key.D1, KeyModifiers.Control)).IsTrue();
            await Assert.That(Has(bindings, Key.D9, KeyModifiers.Control)).IsTrue();

            Nimbus.Ui.Hotkeys.Initialize("mac");
            Rebuild(bindings);
            await Assert.That(Has(bindings, Key.D1, KeyModifiers.Meta)).IsTrue();
            await Assert.That(Has(bindings, Key.D9, KeyModifiers.Meta)).IsTrue();
            await Assert.That(Has(bindings, Key.D1, KeyModifiers.Control)).IsFalse();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// Command-catalog rule 5, App side: <see cref="ChordModifiers.Control"/> is literal
    /// Ctrl on every platform and under every scheme. The exec pane's ^C and ^D are
    /// terminal control characters — Control on macOS too — and Cmd+C there is Copy.
    /// Core already asserts they still *read* as "Ctrl"; this asserts the modifier the
    /// gesture is actually built with.
    /// </summary>
    [Test]
    public async Task Literal_Control_stays_Control_under_the_Cmd_scheme()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");

            await Assert.That(CommandBindings.ToModifiers(ChordModifiers.Control))
                .IsEqualTo(KeyModifiers.Control);

            // …while the command modifier moved, so the two are genuinely distinguished.
            await Assert.That(CommandBindings.ToModifiers(ChordModifiers.Command))
                .IsEqualTo(KeyModifiers.Meta);

            await Assert.That(CommandBindings.GestureFor(CommandId.ExecInterrupt)?.KeyModifiers)
                .IsEqualTo(KeyModifiers.Control);
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    // ---------------------------------------------------------------- the cheat sheet

    /// <summary>
    /// The F1 sheet spells the modifier out on a key cap, so it is rebuilt rather than
    /// cached across a scheme change. A cap still reading "Ctrl" under the Cmd scheme is
    /// the visible half of this item.
    /// </summary>
    [Test]
    public async Task The_cheat_sheet_caps_follow_the_scheme()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(Caps(new ShortcutsViewModel(), "Switch or open a cluster"))
                .IsEqualTo("Ctrl, P");

            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(Caps(new ShortcutsViewModel(), "Switch or open a cluster"))
                .IsEqualTo("Cmd, P");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>The same rule 5 exception, on the surface a user actually reads it from.</summary>
    [Test]
    public async Task The_cheat_sheets_exec_rows_stay_Ctrl_under_the_Cmd_scheme()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");

            await Assert.That(Caps(new ShortcutsViewModel(), "Interrupt a command in the exec pane"))
                .IsEqualTo("Ctrl, C");
            await Assert.That(Caps(new ShortcutsViewModel(), "End input / exit the shell"))
                .IsEqualTo("Ctrl, D");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    // -------------------------------------------------------------------- the tooltips

    /// <summary>
    /// Every <c>CommandTip</c> tooltip is composed against the live label, so an open
    /// window relabels rather than naming the other platform's chord until restart.
    /// </summary>
    [Test]
    public async Task Command_tooltips_follow_the_scheme()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(CommandTip.Compose("Show or hide the resource sidebar", CommandId.ToggleSidebar))
                .IsEqualTo("Show or hide the resource sidebar (Ctrl+B)");

            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(CommandTip.Compose("Show or hide the resource sidebar", CommandId.ToggleSidebar))
                .IsEqualTo("Show or hide the resource sidebar (Cmd+B)");

            // A tip with no command is plain text and must not gain a chord.
            await Assert.That(CommandTip.Compose("Just a sentence", command: null))
                .IsEqualTo("Just a sentence");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    // ------------------------------------------------------------------ the event itself

    /// <summary>
    /// Everything above is re-rendered from <c>Hotkeys.Changed</c>, so the event firing
    /// on a real change — and <em>not</em> firing when the scheme resolves to what it
    /// already was — is the hinge the whole path hangs on.
    /// </summary>
    [Test]
    public async Task Changed_fires_on_a_real_change_and_not_on_a_no_op()
    {
        var fired = 0;

        void OnChanged() => fired++;

        Nimbus.Ui.Hotkeys.Changed += OnChanged;
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            fired = 0;

            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(fired).IsEqualTo(0);

            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(fired).IsEqualTo(1);
            await Assert.That(Hotkeys.Primary).IsEqualTo(KeyModifiers.Meta);

            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(fired).IsEqualTo(2);
            await Assert.That(Hotkeys.Primary).IsEqualTo(KeyModifiers.Control);
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Changed -= OnChanged;
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>The caps of one cheat-sheet row, found by the action text it renders.</summary>
    private static string Caps(ShortcutsViewModel sheet, string action)
    {
        var row = sheet.Sections
            .SelectMany(s => s.Rows)
            .FirstOrDefault(r => r.Action == action)
            ?? throw new InvalidOperationException($"No cheat-sheet row reads \"{action}\".");

        return string.Join(", ", row.Tokens.Where(t => t.IsKey).Select(t => t.Text));
    }
}
