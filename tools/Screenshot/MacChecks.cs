using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using KubeNimbus.App;
using KubeNimbus.App.Platform;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The macOS text keys and the Edit menu's routing, against the real YAML editor, a real
/// TextBox and the real exec terminal. The macOS path is forced on the window
/// (<see cref="MacTextKeys.ForceProperty"/>) and the Cmd scheme set, so this runs on every OS;
/// what only a Mac can show — that AppKit hands the Edit menu the key before Avalonia sees it —
/// was checked on one and is recorded in the PR. The menu verbs are driven through
/// <see cref="EditCommands.Execute"/>, which is exactly what the menu items call.
/// </summary>
internal static class MacChecks
{
    internal static void TextKeys(Window window)
    {
        var view = window.GetVisualDescendants().OfType<YamlEditorView>().First();
        var editor = view.FindControl<TextEditor>("Editor")!;
        var scheme = Nimbus.Ui.Hotkeys.Primary == KeyModifiers.Meta ? "mac" : "windows";
        MacTextKeys.SetForce(window, true);
        Nimbus.Ui.Hotkeys.Initialize("mac");
        try
        {
            const string yaml = "apiVersion: v1\nkind: Pod\nmetadata:\n  name: cache-0\n";
            ((YamlEditorTabViewModel)view.DataContext!).YamlText = yaml;
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.Focus();
            editor.CaretOffset = yaml.IndexOf("Pod", StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();

            // The incident: ⌘↑ and ⌘↓ did nothing on a Mac. Each of the four must move the
            // caret and leave nothing selected.
            Press(window, Key.Up, RawInputModifiers.Meta);
            Expect(editor.CaretOffset == 0 && editor.SelectionLength == 0, $"⌘↑ left the caret at {editor.CaretOffset} with {editor.SelectionLength} selected");
            Press(window, Key.Right, RawInputModifiers.Meta);
            Expect(editor.CaretOffset == "apiVersion: v1".Length && editor.SelectionLength == 0, $"⌘→ left the caret at {editor.CaretOffset} with {editor.SelectionLength} selected");
            Press(window, Key.Left, RawInputModifiers.Meta);
            Expect(editor.CaretOffset == 0 && editor.SelectionLength == 0, $"⌘← left the caret at {editor.CaretOffset}");
            Press(window, Key.Down, RawInputModifiers.Meta);
            Expect(editor.CaretOffset == editor.Document.TextLength && editor.SelectionLength == 0, $"⌘↓ left the caret at {editor.CaretOffset} with {editor.SelectionLength} selected");
            Press(window, Key.Up, RawInputModifiers.Meta | RawInputModifiers.Shift);
            Expect(editor.SelectionLength == editor.Document.TextLength, "⌘⇧↑ did not select to the start");
            Expect(editor.Text == yaml, "the document changed under caret keys");

            // ⌘⌫ deletes to the line start, as one undo step.
            editor.TextArea.ClearSelection();
            editor.CaretOffset = yaml.IndexOf("v1", StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            Press(window, Key.Back, RawInputModifiers.Meta);
            Expect(editor.Text.StartsWith("v1\n", StringComparison.Ordinal), $"⌘⌫ left \"{editor.Text.Split('\n')[0]}\"");
            editor.Undo();
            Expect(editor.Text == yaml, "⌘⌫ was not one undo step");

            // A TextBox: the list filter takes ⌘⌫ and ⌃A/⌃E.
            var box = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "RowFilterBox" && b.IsEffectivelyVisible);
            box.Focus();
            box.Text = "cache customer";
            box.CaretIndex = 5;
            Dispatcher.UIThread.RunJobs();
            Press(window, Key.E, RawInputModifiers.Control);
            Expect(box.CaretIndex == box.Text!.Length, $"⌃E left the caret at {box.CaretIndex}");
            Press(window, Key.Back, RawInputModifiers.Meta);
            Expect(string.IsNullOrEmpty(box.Text), $"⌘⌫ left \"{box.Text}\" in the filter box");
        }
        finally
        {
            MacTextKeys.SetForce(window, null);
            Nimbus.Ui.Hotkeys.Initialize(scheme);
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine("macOS text keys passed (⌘↑↓←→ move, ⌘⇧↑ selects, ⌘⌫ is one undo step, ⌃E and ⌘⌫ in a TextBox).");
    }

    /// <summary>
    /// The Edit menu hands each verb to what has focus. Without it, the menu's key
    /// equivalents would take ⌘C/⌘V from every text control in the app.
    /// </summary>
    internal static void EditRouting(Window window)
    {
        var view = window.GetVisualDescendants().OfType<YamlEditorView>().First();
        var editor = view.FindControl<TextEditor>("Editor")!;
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard
                        ?? throw new InvalidOperationException("The headless window has no clipboard.");

        ((YamlEditorTabViewModel)view.DataContext!).YamlText = "kind: Pod\n";
        Dispatcher.UIThread.RunJobs();
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        EditCommands.Execute(window, EditCommand.SelectAll);
        EditCommands.Execute(window, EditCommand.Copy);
        Expect(Read(clipboard) == "kind: Pod\n", $"Copy in the YAML editor put \"{Read(clipboard)}\" on the clipboard");

        var box = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "RowFilterBox" && b.IsEffectivelyVisible);
        box.Focus();
        box.Text = "ab";
        box.CaretIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Write(clipboard, "XY");
        EditCommands.Execute(window, EditCommand.Paste);
        for (var i = 0; i < 100 && box.Text == "ab"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Expect(box.Text == "aXYb", $"Paste in the filter box gave \"{box.Text}\"");
        EditCommands.Execute(window, EditCommand.Undo);
        Expect(box.Text == "ab", $"Undo in the filter box gave \"{box.Text}\"");

        Console.WriteLine("Edit menu routing passed (Select All and Copy in the YAML editor, Paste and Undo in the filter box).");
    }

    /// <summary>
    /// In the exec terminal the Edit menu's Paste goes through the pane's own filtered paste
    /// and Undo sends nothing — the menu must never turn a verb into bytes the shell reads.
    /// </summary>
    internal static void EditRoutingInTerminal(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ExecView>().First();
        var exec = (ExecTabViewModel)view.DataContext!;
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().First();
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard
                        ?? throw new InvalidOperationException("The headless window has no clipboard.");

        var sent = new List<byte>();
        void OnInput(object? sender, TerminalUserInputEventArgs e) => sent.AddRange(e.Data.ToArray());
        exec.Terminal.UserInput += OnInput;
        try
        {
            terminal.Focus();
            Dispatcher.UIThread.RunJobs();
            EditCommands.Execute(window, EditCommand.Undo);
            EditCommands.Execute(window, EditCommand.Cut);
            Dispatcher.UIThread.RunJobs();
            Expect(sent.Count == 0, $"Undo/Cut in the terminal sent {sent.Count} bytes");

            exec.Terminal.Feed("\u001b[?2004l");
            Write(clipboard, "echo menu");
            EditCommands.Execute(window, EditCommand.Paste);
            for (var i = 0; i < 200 && sent.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            var text = System.Text.Encoding.UTF8.GetString([.. sent]);
            Expect(text == "echo menu", $"Paste in the terminal sent \"{text}\"");
        }
        finally
        {
            exec.Terminal.UserInput -= OnInput;
        }

        Console.WriteLine("Edit menu routing in the exec terminal passed (Paste goes through the pane's paste, Undo and Cut send nothing).");
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static string? Read(Avalonia.Input.Platform.IClipboard clipboard)
    {
        var read = clipboard.TryGetTextAsync();
        while (!read.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
        }

        return read.Result;
    }

    private static void Write(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        var write = clipboard.SetTextAsync(text);
        while (!write.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException(failure);
    }
}
