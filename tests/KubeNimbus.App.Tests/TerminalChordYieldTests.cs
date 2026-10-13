using System.Windows.Input;
using Avalonia.Input;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #222: while the exec terminal has focus, a window chord the shell reads as a control
/// character stands aside, so Ctrl+Shift+L is the shell's <c>^L</c> there rather than the
/// logs palette (QA G-14 saw the palette open). Everywhere else the chord still runs. The
/// rendered half — real key events on a real pane — is the harness's <c>ux-exec-keys</c>.
/// <para>
/// <see cref="Nimbus.Ui.Hotkeys.Primary"/> is process-global, so this class is
/// <c>[NotInParallel]</c> and restores "auto".
/// </para>
/// </summary>
[NotInParallel]
public class TerminalChordYieldTests
{
    private sealed class CountingCommand : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Runs++;
    }

    private static KeyBinding Binding(IEnumerable<KeyBinding> bindings, Key key, KeyModifiers modifiers) =>
        bindings.Single(b => b.Gesture is { } g && g.Key == key && g.KeyModifiers == modifiers);

    [Test]
    public async Task Ctrl_Shift_L_stands_aside_only_while_the_terminal_has_focus()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            var inTerminal = true;
            var bindings = new List<KeyBinding>();
            var command = new CountingCommand();
            CommandBindings.RebuildWindowBindings(bindings, _ => command, _ => new CountingCommand(), () => inTerminal);

            var logs = Binding(bindings, Key.L, KeyModifiers.Control | KeyModifiers.Shift).Command!;
            await Assert.That(logs.CanExecute(null)).IsFalse();
            logs.Execute(null);
            await Assert.That(command.Runs).IsEqualTo(0);

            inTerminal = false;
            await Assert.That(logs.CanExecute(null)).IsTrue();
            logs.Execute(null);
            await Assert.That(command.Runs).IsEqualTo(1);
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    public async Task A_chord_the_shell_does_not_read_never_stands_aside()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            var bindings = new List<KeyBinding>();
            CommandBindings.RebuildWindowBindings(bindings, _ => new CountingCommand(), _ => new CountingCommand(), () => true);

            // Ctrl+1 jumps to a tab from anywhere, the terminal included.
            await Assert.That(Binding(bindings, Key.D1, KeyModifiers.Control).Command!.CanExecute(null)).IsTrue();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    public async Task Under_the_Cmd_scheme_the_palette_chord_is_the_window_s_in_the_terminal_too()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            var bindings = new List<KeyBinding>();
            CommandBindings.RebuildWindowBindings(bindings, _ => new CountingCommand(), _ => new CountingCommand(), () => true);

            await Assert.That(Binding(bindings, Key.L, KeyModifiers.Meta | KeyModifiers.Shift).Command!.CanExecute(null)).IsTrue();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    [Test]
    public async Task The_shell_owns_literal_Ctrl_on_a_letter_only()
    {
        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift))).IsTrue();
        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.K, KeyModifiers.Control))).IsTrue();
        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.L, KeyModifiers.Meta | KeyModifiers.Shift))).IsFalse();
        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.Tab, KeyModifiers.Control))).IsFalse();
        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.D1, KeyModifiers.Control))).IsFalse();
    }

    [Test]
    public async Task The_developer_tools_key_is_neither_the_shell_s_nor_a_command_s()
    {
        // #369: developer tools read their key from the raw input stream, ahead of every
        // control, so on F12 they opened from inside the exec terminal, where F12 is the
        // shell's. The key is checked against both schemes: no command may sit on it either.
        foreach (var scheme in new[] { "windows", "mac" })
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize(scheme);
                var devTools = Hotkeys.DeveloperTools;
                await Assert.That(CommandBindings.ShellOwns(devTools)).IsFalse();

                var bindings = new List<KeyBinding>();
                CommandBindings.RebuildWindowBindings(bindings, _ => new CountingCommand(), _ => new CountingCommand(), () => true);
                await Assert.That(bindings.Any(b => b.Gesture is { } g && g.Key == devTools.Key && g.KeyModifiers == devTools.KeyModifiers)).IsFalse();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        }
    }

    [Test]
    public async Task The_shell_owns_every_function_key()
    {
        for (var key = Key.F1; key <= Key.F12; key++)
        {
            await Assert.That(CommandBindings.ShellOwns(new KeyGesture(key))).IsTrue();
            await Assert.That(CommandBindings.ShellOwns(new KeyGesture(key, KeyModifiers.Shift))).IsTrue();
        }

        await Assert.That(CommandBindings.ShellOwns(new KeyGesture(Key.F13))).IsFalse();
    }

    [Test]
    public async Task F1_stands_aside_only_while_the_terminal_has_focus()
    {
        // #337: htop's F5, mc's F1 and vim mappings need the key; the cheat sheet is one
        // Ctrl+Tab away.
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            var inTerminal = true;
            var bindings = new List<KeyBinding>();
            var command = new CountingCommand();
            CommandBindings.RebuildWindowBindings(bindings, _ => command, _ => new CountingCommand(), () => inTerminal);

            var sheet = Binding(bindings, Key.F1, KeyModifiers.None).Command!;
            await Assert.That(sheet.CanExecute(null)).IsFalse();

            inTerminal = false;
            await Assert.That(sheet.CanExecute(null)).IsTrue();
            sheet.Execute(null);
            await Assert.That(command.Runs).IsEqualTo(1);
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }
}
