using Avalonia.Input;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Mac conventions the catalog adds on the Cmd scheme only, as the key handlers match
/// them: ⌘⌫ arms the delete strip on a selected row (a Mac keyboard sends Delete only as
/// fn+⌫), ⌘[ goes back from an application's page. Found on the 2026-10-17 macOS pass, where
/// neither did anything. Under the Windows scheme neither chord means anything, so Ctrl+⌫ and
/// Ctrl+[ stay unclaimed there.
///
/// <para><c>[NotInParallel]</c>: the scheme is process-global.</para>
/// </summary>
[NotInParallel]
public class MacChordTests
{
    private static KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { Key = key, KeyModifiers = modifiers };

    [Test]
    public async Task Cmd_backspace_deletes_a_row_and_cmd_bracket_goes_back_on_the_cmd_scheme_only()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(CommandBindings.Matches(CommandId.DeleteResource, Key(Avalonia.Input.Key.Back, KeyModifiers.Meta))).IsTrue();
            await Assert.That(CommandBindings.Matches(CommandId.DeleteResource, Key(Avalonia.Input.Key.Delete))).IsTrue();
            await Assert.That(CommandBindings.Matches(CommandId.DeleteResource, Key(Avalonia.Input.Key.Back))).IsFalse();
            await Assert.That(CommandBindings.Matches(CommandId.BackToApplications, Key(Avalonia.Input.Key.OemOpenBrackets, KeyModifiers.Meta))).IsTrue();
            await Assert.That(CommandBindings.Matches(CommandId.BackToApplications, Key(Avalonia.Input.Key.Escape))).IsTrue();
            await Assert.That(CommandBindings.ShortcutLabel(CommandId.DeleteResource)).IsEqualTo("⌘⌫ / ⌦");

            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(CommandBindings.Matches(CommandId.DeleteResource, Key(Avalonia.Input.Key.Back, KeyModifiers.Control))).IsFalse();
            await Assert.That(CommandBindings.Matches(CommandId.DeleteResource, Key(Avalonia.Input.Key.Delete))).IsTrue();
            await Assert.That(CommandBindings.Matches(CommandId.BackToApplications, Key(Avalonia.Input.Key.OemOpenBrackets, KeyModifiers.Control))).IsFalse();
            await Assert.That(CommandBindings.Matches(CommandId.BackToApplications, Key(Avalonia.Input.Key.Escape))).IsTrue();
            await Assert.That(CommandBindings.ShortcutLabel(CommandId.DeleteResource)).IsEqualTo("Delete");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// <c>Nimbus.Ui.Hotkeys.Describe</c>, shared with pgNimbus, dropped Shift and Alt ("Cmd+N"
    /// for ⇧⌘N) and printed raw key names ("D1", "OemComma").
    /// </summary>
    [Test]
    public async Task Describe_names_every_modifier_and_spells_per_scheme()
    {
        var gesture = new KeyGesture(Avalonia.Input.Key.N, KeyModifiers.Meta | KeyModifiers.Shift);
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            await Assert.That(Hotkeys.Describe(gesture)).IsEqualTo("⇧⌘N");
            await Assert.That(Hotkeys.Describe(new KeyGesture(Avalonia.Input.Key.D1, KeyModifiers.Meta | KeyModifiers.Alt | KeyModifiers.Control))).IsEqualTo("⌃⌥⌘1");

            Nimbus.Ui.Hotkeys.Initialize("windows");
            await Assert.That(Hotkeys.Describe(new KeyGesture(Avalonia.Input.Key.N, KeyModifiers.Control | KeyModifiers.Shift))).IsEqualTo("Ctrl+Shift+N");
            await Assert.That(Hotkeys.Describe(new KeyGesture(Avalonia.Input.Key.OemComma, KeyModifiers.Control))).IsEqualTo("Ctrl+,");
            await Assert.That(Hotkeys.Describe(new KeyGesture(Avalonia.Input.Key.Pause))).IsEqualTo("Pause");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }
}
