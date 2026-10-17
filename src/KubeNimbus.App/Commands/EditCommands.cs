using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace KubeNimbus.App;

/// <summary>The standard Edit-menu verbs.</summary>
public enum EditCommand
{
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll,
}

/// <summary>
/// A view that answers an Edit-menu verb itself, because what the verb means there is not
/// what a text control would do: the exec terminal copies its trimmed selection and pastes
/// through the filtered, bracketed path (<c>ExecView</c>). Returning false passes the verb on
/// to the next ancestor.
/// </summary>
public interface IEditCommandTarget
{
    bool TryExecute(EditCommand command);
}

/// <summary>
/// Routes an Edit-menu verb to whatever holds keyboard focus. Ported from pgNimbus
/// (<c>PgNimbus.App.EditCommands</c>).
///
/// <para>
/// <b>This exists because of how the macOS menu bar works, and it is not optional.</b> A
/// menu item's key equivalent is matched by AppKit before the key reaches Avalonia, so once
/// the Edit menu carries ⌘C, the text box that used to receive ⌘C as a key press receives a
/// menu click instead. Each verb therefore has to be handed to the focused control here, or
/// adding the menu breaks copy and paste everywhere in the app.
/// </para>
///
/// <para>
/// The walk starts at the focused element and goes up the visual tree to the window. The
/// first thing on the way that can take the verb does: an <see cref="IEditCommandTarget"/>
/// (the exec terminal), a <see cref="TextBox"/> (the palette, the list filter, the log
/// search boxes, every form field), an AvaloniaEdit <see cref="TextEditor"/> (the YAML
/// editor) or a <see cref="SelectableTextBlock"/> (log lines, detail values — Copy and Select
/// All only). When nothing on the way takes it — the resource grid, whose own ⌘C copies the
/// selected rows — the key the menu consumed is raised again on the focused element, so a
/// control that answered the key itself before the menu existed still does.
/// </para>
/// </summary>
public static class EditCommands
{
    /// <summary>
    /// The gesture a verb shows in the Edit menu: always ⌘, whatever the hotkey scheme. The
    /// menu is macOS-only, the text controls answer ⌘ there under either scheme (Avalonia's
    /// text keymap is the platform's), and a ⌃C key equivalent would be taken by AppKit before
    /// the exec terminal could send its interrupt.
    /// </summary>
    public static KeyGesture GestureFor(EditCommand command) => command switch
    {
        EditCommand.Undo => new KeyGesture(Key.Z, KeyModifiers.Meta),
        EditCommand.Redo => new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift),
        EditCommand.Cut => new KeyGesture(Key.X, KeyModifiers.Meta),
        EditCommand.Copy => new KeyGesture(Key.C, KeyModifiers.Meta),
        EditCommand.Paste => new KeyGesture(Key.V, KeyModifiers.Meta),
        EditCommand.SelectAll => new KeyGesture(Key.A, KeyModifiers.Meta),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    /// <summary>
    /// Performs <paramref name="command"/> on the focused element of
    /// <paramref name="topLevel"/>. Returns false when nothing took it and there was no
    /// focused element to hand the key back to, which is a no-op, not an error.
    /// </summary>
    public static bool Execute(TopLevel topLevel, EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        var focused = topLevel.FocusManager?.GetFocusedElement();
        var start = focused as Visual ?? topLevel;
        for (var node = start; node is not null; node = node.GetVisualParent())
        {
            if (TryExecuteOn(node, command))
            {
                return true;
            }

            if (ReferenceEquals(node, topLevel))
            {
                break;
            }
        }

        return ReRaiseKey(focused as Interactive, command);
    }

    private static bool TryExecuteOn(Visual node, EditCommand command)
    {
        if (node is IEditCommandTarget target && target.TryExecute(command))
        {
            return true;
        }

        switch (node)
        {
            case TextBox textBox:
                Execute(textBox, command);
                return true;
            case TextEditor editor:
                Execute(editor, command);
                return true;
            case SelectableTextBlock block when command is EditCommand.Copy or EditCommand.SelectAll:
                if (command == EditCommand.Copy)
                {
                    block.Copy();
                }
                else
                {
                    block.SelectAll();
                }

                return true;
            default:
                return false;
        }
    }

    // The menu took the key; give it back to whatever has focus, as the key it was.
    private static bool ReRaiseKey(Interactive? focused, EditCommand command)
    {
        if (focused is null)
        {
            return false;
        }

        var gesture = GestureFor(command);
        focused.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers,
            Source = focused,
        });
        return true;
    }

    private static void Execute(TextBox textBox, EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo:
                textBox.Undo();
                break;
            case EditCommand.Redo:
                textBox.Redo();
                break;
            case EditCommand.Cut:
                textBox.Cut();
                break;
            case EditCommand.Copy:
                textBox.Copy();
                break;
            case EditCommand.Paste:
                textBox.Paste();
                break;
            case EditCommand.SelectAll:
                textBox.SelectAll();
                break;
        }
    }

    private static void Execute(TextEditor editor, EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo:
                editor.Undo();
                break;
            case EditCommand.Redo:
                editor.Redo();
                break;
            case EditCommand.Cut:
                editor.Cut();
                break;
            case EditCommand.Copy:
                editor.Copy();
                break;
            case EditCommand.Paste:
                editor.Paste();
                break;
            case EditCommand.SelectAll:
                editor.SelectAll();
                break;
        }
    }
}
