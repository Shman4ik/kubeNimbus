using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
/// The one consequence worth knowing: while the terminal has focus it marks
/// Ctrl+&lt;letter&gt; handled, so the window's own Ctrl chords (the palette, the list
/// filter) do not fire there. That is what a terminal is for — ^C has to reach the
/// container — and it is why Copy and Paste are on Ctrl+Shift here, as they are in
/// every terminal emulator.
/// </para>
/// </remarks>
public partial class ExecView : UserControl
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
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

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
}
