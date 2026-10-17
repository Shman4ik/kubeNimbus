using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit.Editing;
using KubeNimbus.App.Platform;
using KubeNimbus.Core.Commands;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The macOS text keys' table (<see cref="MacTextKeys.Classify"/>) and the text arithmetic
/// behind it. The keys driven into a real YAML editor and a real TextBox are the screenshot
/// harness's <c>ux-mac-text-keys</c> check, because that needs a running Avalonia
/// application, which this project does not start.
///
/// <para>
/// The central rows are ⌘↑/⌘↓ as caret moves to the document's ends. Before this they did
/// nothing in the YAML editor on a real Mac (Avalonia's keymap has no binding for them), so
/// "go to the top" left the caret where it was and the next keystroke landed there.
/// </para>
/// </summary>
[NotInParallel]
public class MacTextKeysTests
{
    public const KeyModifiers Ctrl = KeyModifiers.Control;
    public const KeyModifiers Cmd = KeyModifiers.Meta;
    public const KeyModifiers Opt = KeyModifiers.Alt;
    public const KeyModifiers Shift = KeyModifiers.Shift;

    [Test]
    [Arguments(Key.Up, Cmd, MacTextAction.DocumentStart, false)]
    [Arguments(Key.Up, Cmd | Shift, MacTextAction.DocumentStart, true)]
    [Arguments(Key.Down, Cmd, MacTextAction.DocumentEnd, false)]
    [Arguments(Key.Down, Cmd | Shift, MacTextAction.DocumentEnd, true)]
    [Arguments(Key.Left, Cmd, MacTextAction.LineStart, false)]
    [Arguments(Key.Left, Cmd | Shift, MacTextAction.LineStart, true)]
    [Arguments(Key.Right, Cmd, MacTextAction.LineEnd, false)]
    [Arguments(Key.Right, Cmd | Shift, MacTextAction.LineEnd, true)]
    [Arguments(Key.Back, Opt, MacTextAction.DeleteWordBackward, false)]
    [Arguments(Key.Delete, Opt, MacTextAction.DeleteWordForward, false)]
    [Arguments(Key.Back, Cmd, MacTextAction.DeleteToLineStart, false)]
    [Arguments(Key.Delete, Cmd, MacTextAction.DeleteToLineEnd, false)]
    [Arguments(Key.A, Ctrl, MacTextAction.LineStart, false)]
    [Arguments(Key.E, Ctrl | Shift, MacTextAction.LineEnd, true)]
    [Arguments(Key.K, Ctrl, MacTextAction.KillToLineEnd, false)]
    [Arguments(Key.Y, Ctrl, MacTextAction.Yank, false)]
    [Arguments(Key.T, Ctrl, MacTextAction.Transpose, false)]
    public async Task A_cocoa_text_key_is_classified(Key key, KeyModifiers modifiers, MacTextAction action, bool extend)
    {
        var gesture = MacTextKeys.Classify(key, modifiers);

        await Assert.That(gesture.Action).IsEqualTo(action);
        await Assert.That(gesture.Extend).IsEqualTo(extend);
    }

    [Test]
    [Arguments(Key.A, Cmd)] // select all
    [Arguments(Key.C, Cmd)] // copy
    [Arguments(Key.S, Cmd)] // apply the YAML
    [Arguments(Key.Left, Opt)] // word left: the platform keymap's own
    [Arguments(Key.Tab, Ctrl)] // leave the editor
    [Arguments(Key.Z, Ctrl)] // unhealthy only, on the list
    [Arguments(Key.C, Ctrl)] // the exec terminal's interrupt
    public async Task Other_chords_are_left_alone(Key key, KeyModifiers modifiers)
    {
        await Assert.That(MacTextKeys.Classify(key, modifiers).Action).IsEqualTo(MacTextAction.None);
    }

    /// <summary>
    /// No catalog chord, resolved the way the Cmd scheme resolves it, is a text key — or a
    /// command would stop working whenever a text box has focus. The exec terminal's own
    /// chords are the exception the catalog states by scope: ^D is the terminal's end of
    /// input and also Cocoa's delete-forward, which is harmless only because the terminal is
    /// neither of the two control types the text keys are installed on. That is asserted too,
    /// so a terminal that one day derives from TextBox fails here rather than eating ^D.
    /// The list keys are the second exception: they are answered by the resource grid's own
    /// handler, and ⌘⌫ there deletes the row (asking first) while in a text box it deletes to
    /// the line's start. The grid being neither control type is asserted the same way.
    /// </summary>
    [Test]
    public async Task No_command_chord_on_the_cmd_scheme_is_a_text_key()
    {
        var clashes = new List<string>();
        foreach (var descriptor in CommandCatalog.All.Where(d => d.Scope is not (CommandScope.Exec or CommandScope.List)))
        {
            foreach (var c in descriptor.ChordsFor(ChordScheme.Cmd))
            {

                var modifiers = KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Command) ? KeyModifiers.Meta : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Control) ? KeyModifiers.Control : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Shift) ? KeyModifiers.Shift : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Alt) ? KeyModifiers.Alt : KeyModifiers.None;

                if (MacTextKeys.Classify(CommandBindings.ToKey(c.Key), modifiers).Action != MacTextAction.None)
                {
                    clashes.Add($"{descriptor.Id} ({c.Label(ChordScheme.Cmd)})");
                }
            }
        }

        await Assert.That(clashes).IsEmpty();
        await Assert.That(typeof(TextBox).IsAssignableFrom(typeof(TerminalControl))).IsFalse();
        await Assert.That(typeof(TextArea).IsAssignableFrom(typeof(TerminalControl))).IsFalse();
        await Assert.That(typeof(TextBox).IsAssignableFrom(typeof(DataGrid))).IsFalse();
    }

    [Test]
    public async Task The_keys_are_off_unless_forced_or_on_a_mac_under_the_cmd_scheme()
    {
        var box = new TextBox();
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(MacTextKeys.IsActive(box)).IsFalse();

            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(MacTextKeys.IsActive(box)).IsEqualTo(OperatingSystem.IsMacOS());

            MacTextKeys.SetForce(box, true);
            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(MacTextKeys.IsActive(box)).IsTrue();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    [Arguments("first\nsecond", 9, 6, 12)]
    [Arguments("one line", 3, 0, 8)]
    [Arguments("a\r\nb", 4, 3, 4)]
    public async Task Line_bounds_stop_at_the_line_breaks(string text, int caret, int start, int end)
    {
        await Assert.That(MacTextKeys.TextLineStart(text, caret)).IsEqualTo(start);
        await Assert.That(MacTextKeys.TextLineEnd(text, caret)).IsEqualTo(end);
    }

    [Test]
    [Arguments("name: cache-0", 0, 4)]
    [Arguments("name: cache-0", 4, 5)]
    [Arguments("a   b", 1, 5)]
    public async Task Option_delete_forward_stops_at_the_end_of_the_next_word(string text, int caret, int end)
    {
        await Assert.That(MacTextKeys.WordEndAfter(text, caret)).IsEqualTo(end);
    }

    [Test]
    public async Task Ctrl_t_swaps_around_the_caret_and_at_the_line_end_the_two_before_it()
    {
        await Assert.That(MacTextKeys.TransposeRange("abc", 1)).IsEqualTo((0, "ba"));
        await Assert.That(MacTextKeys.TransposeRange("abc", 3)).IsEqualTo((1, "cb"));
        await Assert.That(MacTextKeys.TransposeRange("a\nb", 1)).IsNull();
    }
}
