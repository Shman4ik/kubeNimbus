using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.App.Views;

/// <summary>
/// The exec pane's host: focus, the clipboard gestures, and the right-click menu.
/// </summary>
/// <remarks>
/// Everything else about input now belongs to <see cref="TerminalControl"/>, which
/// encodes keystrokes the way a terminal does — Ctrl+C as <c>0x03</c>, Tab as
/// <c>0x09</c>, arrows and function keys as escape sequences — and raises them as
/// bytes on the model. The pane used to hand-roll a subset of that on a
/// <c>TextBox</c>, in a Tunnel handler, because a focused TextBox eats Ctrl+C as
/// Copy and Tab as focus navigation.
/// <para>
/// The one consequence worth knowing: while the terminal has focus, Ctrl+&lt;letter&gt; and
/// Ctrl+Shift+&lt;letter&gt; are the shell's, so the window's own Ctrl chords (the palette,
/// the logs palette, the switcher) do not fire there, and so are F1–F12 (#337: F1 is the
/// shell's, not the cheat sheet). A window key binding runs before the
/// focused control sees the key, so that takes <c>CommandBindings.ShellOwns</c> standing the
/// window's binding aside (#222) as well as the control marking the key handled. That is
/// what a terminal is for — ^C has to reach the container — and it is why Copy and Paste
/// are on Ctrl+Shift here, as they are in every terminal emulator.
/// </para>
/// </remarks>
public partial class ExecView : UserControl, IEditCommandTarget
{
    public ExecView()
    {
        InitializeComponent();

        // Tunnel: the control's own OnKeyDown runs in the bubble phase and would turn
        // Ctrl+Shift+C into a plain ^C (its control-character mapping ignores Shift),
        // so the clipboard pair has to be taken before it.
        Terminal.AddHandler(KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
        Terminal.ContextRequested += OnTerminalContextRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A terminal you have to click before typing into is a terminal that looks
        // broken for the first second of every session.
        Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);
    }

    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        // A modifier on its own stops here. The control clears its selection at the top of its
        // OnKeyDown for every key, and a keyboard sends Ctrl and Shift as key-downs of their own
        // before the C of Ctrl+Shift+C, so the selection was gone by the time the copy below ran
        // and nothing reached the clipboard (#265). A modifier sends no bytes to the shell, so
        // the control loses nothing by not seeing it.
        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            switch (e.Key)
            {
                case Key.C:
                    _ = CopySelectionAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                    return;
            }
        }

        // The rest of the chords the control gets wrong, encoded here (#315, #222).
        if (EncodeKey(e.Key, e.KeyModifiers, altIsMeta: !OperatingSystem.IsMacOS()) is { } bytes && DataContext is ExecTabViewModel session)
        {
            session.Terminal.Send(bytes);
            e.Handled = true;
        }
    }

    /// <summary>True when <paramref name="element"/> is an exec terminal or inside one.</summary>
    internal static bool IsInTerminal(object? element) =>
        element is Visual visual && (visual is TerminalControl || visual.FindAncestorOfType<TerminalControl>() is not null);

    internal static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>
    /// What the terminal sends for the chords <see cref="TerminalControl"/> encodes wrongly, or
    /// <c>null</c> to leave the key to the control.
    /// <list type="bullet">
    /// <item><b>Alt+letter is ESC and the letter in the case Shift gives it</b> (#315). The control
    /// sends ESC followed by the key event's <c>KeySymbol</c>, and on Windows that symbol is the
    /// upper-case letter for an Alt chord whatever Shift says, so Alt+B reached the shell as
    /// <c>ESC B</c> — not readline's Meta-b (backward-word) — and the line editor inserted a
    /// stray letter. Ctrl+Alt is left alone: on Windows it is AltGr, whose characters arrive as
    /// text input. Only where Alt is Meta: on macOS Option types characters (Option+B is
    /// <c>∫</c>), and the control's <c>OptionAsMetaKey</c> decides that there.</item>
    /// <item><b>Ctrl+Shift+letter is the letter's control character</b>, as xterm sends it (#222).
    /// The control encodes Ctrl+letter but not with Shift held, so Ctrl+Shift+L reached no shell
    /// and went on to the window, where it opened the logs palette. The terminal owns its keys:
    /// the chord is <c>^L</c> here, and Ctrl+Tab leaves the terminal for the palette.
    /// Ctrl+Shift+C and V are Copy and Paste and never get here.</item>
    /// <item><b>F11 and F12 are xterm's <c>CSI 23 ~</c> and <c>CSI 24 ~</c></b> (#337). The control
    /// encodes F1–F10 but sends nothing for F11 and F12 (read off a rendered pane by
    /// <c>ux-exec-keys</c>, on 2.1.0 and 2.2.2). A modifier goes in xterm's parameter form,
    /// <c>CSI 24 ; 1+mask ~</c> with Shift 1, Alt 2 and Ctrl 4.</item>
    /// </list>
    /// </summary>
    internal static string? EncodeKey(Key key, KeyModifiers modifiers, bool altIsMeta)
    {
        if (key is Key.F11 or Key.F12)
        {
            var code = key == Key.F11 ? 23 : 24;
            var mask = (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0)
                       + (modifiers.HasFlag(KeyModifiers.Alt) ? 2 : 0)
                       + (modifiers.HasFlag(KeyModifiers.Control) ? 4 : 0);
            return mask == 0 ? $"\u001b[{code}~" : $"\u001b[{code};{mask + 1}~";
        }

        if (key is < Key.A or > Key.Z)
        {
            return null;
        }

        var offset = key - Key.A;
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            return null;
        }

        if (alt && !control && altIsMeta)
        {
            return "\u001b" + (char)((shift ? 'A' : 'a') + offset);
        }

        if (control && shift && !alt)
        {
            return ((char)(offset + 1)).ToString();
        }

        return null;
    }

    /// <summary>
    /// Copies the selection with each line's trailing blank cells and the trailing blank lines
    /// removed (<see cref="ExecCopy.Trim"/>, #267). Never the control's own
    /// <c>CopySelectionAsync</c>, which copies the selected cells as they are: every line
    /// padded with spaces to the terminal's width.
    /// </summary>
    private async Task CopySelectionAsync()
    {
        var text = ExecCopy.Trim(Terminal.SelectedText);
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // A clipboard that cannot be written copies nothing; the selection is still there.
        }
    }
    /// <summary>
    /// Reads the clipboard and hands the text to <see cref="ExecTabViewModel.Paste"/>. Never
    /// the control's own <c>PasteFromClipboardAsync</c>, which sends the clipboard raw — no
    /// bracketed-paste markers and no filtering of the escape sequences in it.
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        if (DataContext is not ExecTabViewModel exec || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            exec.Paste(await clipboard.TryGetTextAsync());
        }
        catch (Exception)
        {
            // An unreadable clipboard pastes nothing, as an empty one does.
        }

        if (exec.HasPendingPaste)
        {
            Dispatcher.UIThread.Post(() => PasteConfirmButton.Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>Esc on the armed paste strip cancels it; either way focus goes back to the terminal.</summary>
    private void OnPasteStripKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ExecTabViewModel exec)
        {
            exec.CancelPasteCommand.Execute(null);
            e.Handled = true;
            Terminal.Focus();
        }
    }

    private void OnPasteStripButtonClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);

    private void OnTerminalContextRequested(object? sender, TerminalContextRequestedEventArgs e)
    {
        if (Resources["TerminalMenu"] is ContextMenu menu)
        {
            menu.Open(Terminal);
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync();

    private void OnPasteClick(object? sender, RoutedEventArgs e) => _ = PasteFromClipboardAsync();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => Terminal.SelectAll();

    /// <summary>
    /// The macOS Edit menu, while the terminal has focus. AppKit takes ⌘C and ⌘V as the
    /// menu's key equivalents before the terminal sees them (see <see cref="EditCommands"/>),
    /// so they arrive here as menu verbs and do what Terminal.app does with them: Copy the
    /// trimmed selection and Paste through the filtered, bracketed path — the same two methods
    /// as Ctrl+Shift+C/V and the right-click menu, never the control's raw ones. Undo, Redo
    /// and Cut mean nothing to a shell and are swallowed, so they cannot fall through as a key
    /// the shell would read. A focused control elsewhere in the pane (the shell picker) is not
    /// the terminal and is left to its own handling.
    /// </summary>
    public bool TryExecute(EditCommand command)
    {
        if (!IsInTerminal(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
        {
            return false;
        }

        switch (command)
        {
            case EditCommand.Copy:
                _ = CopySelectionAsync();
                break;
            case EditCommand.Paste:
                _ = PasteFromClipboardAsync();
                break;
            case EditCommand.SelectAll:
                Terminal.SelectAll();
                break;
        }

        return true;
    }
}
