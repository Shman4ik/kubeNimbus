using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Keyboard contracts that need a real window, so they cannot live in
/// <c>KubeNimbus.App.Tests</c>, which deliberately starts no Avalonia application. This
/// harness already runs one under <c>Avalonia.Headless</c> and is run by CI on every push,
/// and a check here throws — failing the render step — the same way the other <c>ux-</c>
/// checks do. That is the answer to the design question VER-19 and ENG-20 both raised:
/// one Avalonia.Headless host rather than a second one bolted onto the unit tests.
/// </summary>
internal static class KeyboardChecks
{
    /// <summary>
    /// VER-19, the window's half: after a scheme change the window's own key bindings
    /// follow it — the new chord opens the palette and the old one no longer does. The
    /// rebuild itself is unit-tested (<c>HotkeySchemeTests</c>); what only a real window can
    /// show is that <c>MainWindow</c> is still subscribed to <c>Hotkeys.Changed</c>. Delete
    /// that subscription and the Cmd+K step below throws.
    /// </summary>
    internal static void HotkeyScheme(Window window)
    {
        var shell = (MainWindowViewModel)window.DataContext!;
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("windows");
            Dispatcher.UIThread.RunJobs();
            if (!OpensPalette(window, shell, RawInputModifiers.Control))
                throw new InvalidOperationException("Ctrl+K did not open the palette under the Ctrl scheme.");

            Nimbus.Ui.Hotkeys.Initialize("mac");
            Dispatcher.UIThread.RunJobs();
            if (!OpensPalette(window, shell, RawInputModifiers.Meta))
                throw new InvalidOperationException(
                    "Cmd+K did not open the palette after switching to the Cmd scheme — is MainWindow still subscribed to Hotkeys.Changed?");
            if (OpensPalette(window, shell, RawInputModifiers.Control))
                throw new InvalidOperationException("Ctrl+K still opened the palette after switching to the Cmd scheme.");
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine("Hotkey scheme interaction passed (Ctrl+K, then Cmd+K and not Ctrl+K).");
    }

    private static bool OpensPalette(Window window, MainWindowViewModel shell, RawInputModifiers modifiers)
    {
        shell.Palette.Close();
        Dispatcher.UIThread.RunJobs();
        window.Focus();
        window.KeyPress(Key.K, modifiers, PhysicalKey.K, "k");
        Dispatcher.UIThread.RunJobs();
        var opened = shell.Palette.IsOpen;
        shell.Palette.Close();
        Dispatcher.UIThread.RunJobs();
        return opened;
    }

    /// <summary>
    /// ENG-20: the exec terminal sends a terminal's bytes for the keys a shell cannot do
    /// without — Ctrl+C is 0x03 (interrupt), Ctrl+D is 0x04 (end of input), Tab is 0x09
    /// (completion) — and Ctrl+Shift+C is the clipboard, not an interrupt. FEAT-10's
    /// acceptance of these was a scratch probe from one session; this is it re-running.
    /// The bytes are read off the view model's own terminal model, i.e. exactly what
    /// <c>ExecTabViewModel</c> forwards to the pod's stdin.
    /// </summary>
    internal static void ExecKeys(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ExecView>().First();
        var exec = (ExecTabViewModel)view.DataContext!;
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().First();

        var sent = new List<byte>();
        void OnInput(object? sender, TerminalUserInputEventArgs e) => sent.AddRange(e.Data.ToArray());
        exec.Terminal.UserInput += OnInput;
        try
        {
            byte[] Press(Key key, RawInputModifiers modifiers, PhysicalKey physical, string? symbol)
            {
                sent.Clear();
                terminal.Focus();
                Dispatcher.UIThread.RunJobs();
                window.KeyPress(key, modifiers, physical, symbol);
                window.KeyRelease(key, modifiers, physical, symbol);
                Dispatcher.UIThread.RunJobs();
                return [.. sent];
            }

            Expect(Press(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c"), [0x03], "Ctrl+C");
            Expect(Press(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d"), [0x04], "Ctrl+D");
            Expect(Press(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"), [0x09], "Tab");

            var copy = Press(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
            if (copy.Contains((byte)0x03))
                throw new InvalidOperationException("Ctrl+Shift+C reached the pod as ^C; it is the terminal's Copy.");
        }
        finally
        {
            exec.Terminal.UserInput -= OnInput;
        }

        Console.WriteLine("Exec terminal key mapping passed (^C 0x03, ^D 0x04, Tab 0x09, Ctrl+Shift+C not ^C).");
    }

    private static void Expect(byte[] actual, byte[] expected, string gesture)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{gesture} sent [{string.Join(" ", actual.Select(b => $"0x{b:X2}"))}] to the pod, expected "
                + $"[{string.Join(" ", expected.Select(b => $"0x{b:X2}"))}].");
    }
}
