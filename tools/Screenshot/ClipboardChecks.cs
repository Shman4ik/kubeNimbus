using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Copy gestures that are only proven by reading the clipboard back after the gesture a person
/// makes: a click on a menu item inside a flyout (#288), and Ctrl+Shift+C pressed the way a
/// keyboard sends it, one key-down per key (#265).
/// </summary>
internal static class ClipboardChecks
{
    /// <summary>
    /// #288: the log pane's ⋯ menu Copy (shown when the bar is too narrow for its own Copy)
    /// writes exactly what the bar's Copy writes. It used to write nothing: the item's Click
    /// handler hid the flyout, which takes the popup out of the logical tree, so the item's
    /// <c>{Binding CopyLogsCommand}</c> lost its DataContext and the Button found no command to
    /// run after its Click event. Clear and Save in the same menu had the same defect. The item
    /// is clicked with the pointer, on the popup it is drawn in, after the menu is opened with a
    /// click on ⋯.
    /// </summary>
    internal static void LogMenuCopy(Window window)
    {
        var pane = window.GetVisualDescendants().OfType<UserControl>()
            .First(c => c is PodDetailView or WorkloadLogsView && c.IsEffectivelyVisible);
        var menuButton = pane.FindControl<Button>("LogMenuButton") ?? throw new InvalidOperationException("No LogMenuButton.");
        var menuCopy = pane.FindControl<Button>("MenuCopy") ?? throw new InvalidOperationException("No MenuCopy.");
        if (!menuCopy.IsVisible)
            throw new InvalidOperationException("The bar still has its own Copy at this width, so the menu's Copy is not offered and nothing was checked.");

        var command = pane.DataContext switch
        {
            PodDetailTabViewModel pod => pod.CopyLogsCommand,
            WorkloadLogsTabViewModel workload => workload.CopyLogsCommand,
            _ => throw new InvalidOperationException($"The log pane's DataContext is {pane.DataContext?.GetType().Name ?? "null"}."),
        };

        // The view models copy through the desktop lifetime's main window, which the harness
        // has none of; give them this one for the length of the check.
        MainWindowAccess.Override = window;
        try
        {
            var clipboard = window.Clipboard ?? throw new InvalidOperationException("The headless window has no clipboard.");

            // What the bar's Copy writes.
            Write(clipboard, "");
            Wait(command.ExecuteAsync(null));
            var expected = Read(clipboard);
            if (string.IsNullOrEmpty(expected))
                throw new InvalidOperationException("The bar's Copy wrote nothing, so the menu's Copy has nothing to be compared with.");

            Write(clipboard, "");
            Click(window, menuButton);
            if (TopLevel.GetTopLevel(menuCopy) is not { } popup || !menuCopy.IsEffectivelyVisible)
                throw new InvalidOperationException("A click on ⋯ did not open the log pane's menu.");
            Click(popup, menuCopy);
            for (var i = 0; i < 20 && command.IsRunning; i++)
                Dispatcher.UIThread.RunJobs();

            var copied = Read(clipboard);
            if (string.IsNullOrEmpty(copied))
                throw new InvalidOperationException("The ⋯ menu's Copy wrote nothing to the clipboard (#288).");
            if (copied != expected)
                throw new InvalidOperationException(
                    $"The ⋯ menu's Copy wrote {copied.Length} characters, the bar's Copy {expected.Length}; they must be the same text.");
            if (menuButton.Flyout is { IsOpen: true })
                throw new InvalidOperationException("The ⋯ menu stayed open after its Copy.");

            Console.WriteLine($"{pane.GetType().Name}'s ⋯ menu Copy wrote the bar's text ({copied.Length} characters) and closed the menu.");
        }
        finally
        {
            MainWindowAccess.Override = null;
        }
    }

    /// <summary>
    /// #265: Ctrl+Shift+C as a keyboard sends it. A real keyboard sends a key-down for Ctrl,
    /// one for Shift, then one for C, and the terminal control clears its selection at the top
    /// of its <c>OnKeyDown</c> for every key, modifiers included; so by the time the pane's
    /// Tunnel handler saw Ctrl+Shift+C the selection was empty and nothing was copied.
    /// <c>ux-exec-copy</c> sends C with the modifiers already down and no key-down of their
    /// own, which is why it stayed green. The selection here is made twice: by Select all, and
    /// by a pointer drag across the first rows, each followed by the three key-downs.
    /// </summary>
    internal static void ExecCopyRealKeys(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ExecView>().First();
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().First();
        var clipboard = TopLevel.GetTopLevel(view)?.Clipboard
                        ?? throw new InvalidOperationException("The headless window has no clipboard to copy to.");

        void CopyByKeys(string how)
        {
            Write(clipboard, "");
            terminal.Focus();
            Dispatcher.UIThread.RunJobs();
            var selected = ExecCopy.Trim(terminal.SelectedText);
            if (selected.Length == 0)
                throw new InvalidOperationException($"{how} selected nothing, so there was nothing to copy.");

            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            window.KeyPress(Key.LeftShift, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null);
            window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
            window.KeyRelease(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
            window.KeyRelease(Key.LeftShift, RawInputModifiers.Control, PhysicalKey.ShiftLeft, null);
            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            var copied = Read(clipboard);
            if (copied != selected)
                throw new InvalidOperationException(
                    $"{how}, then Ctrl, Shift and C pressed in turn, copied {(string.IsNullOrEmpty(copied) ? "nothing" : $"{copied.Length} characters")}; "
                    + $"the selection holds {selected.Length} (#265).");
        }

        terminal.SelectAll();
        Dispatcher.UIThread.RunJobs();
        CopyByKeys("Select all");

        // A drag from the top-left corner across the first three rows, by the pointer.
        var from = terminal.TranslatePoint(new Point(4, 4), window)
                   ?? throw new InvalidOperationException("The terminal is not in the window.");
        var to = terminal.TranslatePoint(new Point(terminal.Bounds.Width / 2, Math.Min(terminal.Bounds.Height - 4, 48)), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        CopyByKeys("A pointer drag");

        Console.WriteLine("Exec copy by real key sequence passed (Select all and a pointer drag, each then Ctrl, Shift, C).");
    }

    private static void Click(TopLevel topLevel, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), topLevel)
                     ?? throw new InvalidOperationException($"{target.Name} is not in {topLevel.GetType().Name}.");
        topLevel.MouseDown(centre, MouseButton.Left);
        topLevel.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static string? Read(IClipboard clipboard)
    {
        var read = clipboard.TryGetTextAsync();
        while (!read.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        return read.Result;
    }

    private static void Write(IClipboard clipboard, string text) => Wait(clipboard.SetTextAsync(text));
}
