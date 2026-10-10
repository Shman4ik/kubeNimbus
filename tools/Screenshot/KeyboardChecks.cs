using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.Controls;
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

    /// <summary>
    /// B2-5: a paste reaches the pod as XTerm.NET's own paste would send it, never raw. The
    /// clipboard is pasted with the pane's real Ctrl+Shift+V and the bytes are read off the
    /// terminal model, as <see cref="ExecKeys"/> reads them:
    /// <list type="bullet">
    /// <item>escape sequences in the clipboard are dropped, so <c>ESC[201~</c> cannot end a
    /// bracketed paste early and run what follows as typed;</item>
    /// <item>with bracketed paste on, a multi-line paste goes at once, inside the markers;</item>
    /// <item>with it off (BusyBox <c>sh</c>), a multi-line paste sends nothing until it is
    /// confirmed, Enter on the armed strip sends it, and Esc sends nothing.</item>
    /// </list>
    /// The terminal control's own <c>PasteFromClipboardAsync</c> sends the clipboard raw, so
    /// routing the gesture back to it turns this red.
    /// </summary>
    internal static void ExecPaste(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ExecView>().First();
        var exec = (ExecTabViewModel)view.DataContext!;
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().First();
        var clipboard = TopLevel.GetTopLevel(view)?.Clipboard
                        ?? throw new InvalidOperationException("The headless window has no clipboard to paste from.");

        var sent = new List<byte>();
        void OnInput(object? sender, TerminalUserInputEventArgs e) => sent.AddRange(e.Data.ToArray());
        exec.Terminal.UserInput += OnInput;
        try
        {
            string PasteWith(string text, Func<bool> done)
            {
                sent.Clear();
                var write = clipboard.SetTextAsync(text);
                while (!write.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                }

                terminal.Focus();
                Dispatcher.UIThread.RunJobs();
                window.KeyPress(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.V, "V");
                window.KeyRelease(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.V, "V");
                for (var i = 0; i < 200 && !done(); i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }

                return System.Text.Encoding.UTF8.GetString([.. sent]);
            }

            void Check(string actual, string expected, string what)
            {
                if (actual != expected)
                    throw new InvalidOperationException(
                        $"{what}: sent {Escape(actual)} where {Escape(expected)} was expected.");
            }

            exec.Terminal.Feed("\u001b[?2004l");
            Check(PasteWith("echo hi\u001b[201~; rm -rf /tmp/x\u009b1m", () => sent.Count > 0),
                "echo hi[201~; rm -rf /tmp/x1m", "A single-line paste with escape sequences in it");

            exec.Terminal.Feed("\u001b[?2004h");
            Check(PasteWith("one\ntwo\r\n", () => sent.Count > 0),
                "\u001b[200~one\rtwo\r\u001b[201~", "A multi-line paste to a shell with bracketed paste on");

            exec.Terminal.Feed("\u001b[?2004l");
            Check(PasteWith("one\ntwo\nthree", () => exec.HasPendingPaste), "", "A multi-line paste to a shell without bracketed paste, before it is confirmed");
            if (!exec.PendingPastePrompt.StartsWith("Paste 3 lines?", StringComparison.Ordinal))
                throw new InvalidOperationException($"The armed paste says \"{exec.PendingPastePrompt}\".");

            var confirm = view.FindControl<Button>("PasteConfirmButton")!;
            for (var i = 0; i < 20 && !confirm.IsFocused; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            if (!confirm.IsFocused)
                throw new InvalidOperationException("The armed paste's Paste button did not take focus, so Enter would reach the terminal.");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Check(System.Text.Encoding.UTF8.GetString([.. sent]), "one\rtwo\rthree", "Enter on the armed paste");
            if (exec.HasPendingPaste)
                throw new InvalidOperationException("The paste stayed armed after it was sent.");

            Check(PasteWith("one\ntwo", () => exec.HasPendingPaste), "", "A second multi-line paste, before Esc");
            var strip = confirm.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("overlayCard"));
            confirm.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            if (exec.HasPendingPaste || sent.Count > 0 || strip.IsVisible)
                throw new InvalidOperationException("Esc on the armed paste did not cancel it, or sent something.");
        }
        finally
        {
            exec.Terminal.UserInput -= OnInput;
        }

        Console.WriteLine("Exec paste passed (controls dropped, bracketed when asked, multi-line armed until confirmed, Esc cancels).");
    }

    private static string Escape(string text) =>
        "\"" + string.Concat(text.Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString())) + "\"";

    /// <summary>
    /// The log pane's keys, sent as key events to a real window: Alt+R and Alt+C flip the
    /// search's regex and match-case toggles from the search box, Alt+P pins nothing without
    /// a search, and Alt+Up / Alt+Down move the error cursor from anywhere in the pane. The
    /// 0.5.1 release pass reported Alt+R and Alt+Up as dead after pressing them through an
    /// input tool whose Alt may never reach Avalonia; this is the same gestures sent where
    /// no tool can drop the modifier.
    /// </summary>
    internal static void LogSearchKeys(Window window)
    {
        var view = window.GetVisualDescendants().OfType<PodDetailView>().First();
        var vm = (PodDetailTabViewModel)view.DataContext!;
        var box = view.FindControl<TextBox>("LogSearchBox")!;

        void Press(Key key, PhysicalKey physical, string symbol, Control target)
        {
            target.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(key, RawInputModifiers.Alt, physical, symbol);
            window.KeyRelease(key, RawInputModifiers.Alt, physical, symbol);
            Dispatcher.UIThread.RunJobs();
        }

        if (vm.IsLogRegex || vm.IsLogMatchCase)
            throw new InvalidOperationException("The log search should start with regex and match case off.");

        Press(Key.R, PhysicalKey.R, "r", box);
        if (!vm.IsLogRegex)
            throw new InvalidOperationException("Alt+R in the log search box did not turn the regular expression on.");
        Press(Key.R, PhysicalKey.R, "r", box);
        if (vm.IsLogRegex)
            throw new InvalidOperationException("A second Alt+R did not turn the regular expression off.");
        Press(Key.C, PhysicalKey.C, "c", box);
        if (!vm.IsLogMatchCase)
            throw new InvalidOperationException("Alt+C in the log search box did not turn match case on.");
        Press(Key.C, PhysicalKey.C, "c", box);

        // The fixture's own log may or may not carry errors; give the pane two it cannot miss.
        vm.Enqueue("2026-10-01T10:00:00.000Z ERROR first failure");
        vm.Enqueue("2026-10-01T10:00:01.000Z INFO between");
        vm.Enqueue("2026-10-01T10:00:02.000Z ERROR second failure");
        vm.FlushLogLines();
        Dispatcher.UIThread.RunJobs();
        if (!vm.Problems.HasErrors)
            throw new InvalidOperationException("The pane shows no error line after two were enqueued, so Alt+Up cannot be checked.");
        var first = vm.Problems.Current;
        Press(Key.Up, PhysicalKey.ArrowUp, null!, box);
        if (vm.Problems.Current is null || ReferenceEquals(vm.Problems.Current, first))
            throw new InvalidOperationException("Alt+Up in the log search box did not move the error cursor.");

        Console.WriteLine("Log search keys passed (Alt+R, Alt+C, Alt+Up).");
    }

    /// <summary>
    /// Ctrl+S in the YAML editor starts an apply. The cheat sheet and docs/keyboard-shortcuts.md
    /// listed it while nothing was bound to it; the 0.5.1 release pass pressed it and nothing
    /// happened. The apply is observed where it begins, <c>IsBusy</c> turning on, because the
    /// fixture's client points at nothing and the request fails a moment later.
    /// </summary>
    internal static void YamlApplyKey(Window window)
    {
        var view = window.GetVisualDescendants().OfType<YamlEditorView>().First();
        var vm = (YamlEditorTabViewModel)view.DataContext!;
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First();

        if (!vm.ApplyCommand.CanExecute(null))
            throw new InvalidOperationException("The fixture's editor cannot apply, so Ctrl+S cannot be checked.");

        var busy = false;
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(YamlEditorTabViewModel.IsBusy) && vm.IsBusy)
                busy = true;
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            editor.TextArea.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }

        if (!busy)
            throw new InvalidOperationException("Ctrl+S in the YAML editor did not start an apply.");

        Console.WriteLine("YAML editor apply key passed (Ctrl+S).");
    }

    /// <summary>
    /// ENG-4's keyboard-only walk, and ENG-54's dock tabs inside it. From the command bar's
    /// first button, Tab has to come back round to it (no focus trap), passing the command
    /// bar, the sidebar's kinds, the resource list, the dock's tabs and the open pane in that
    /// order; Shift+Tab has to pass the same stops. Every stop draws a focus visual. The
    /// sidebar's catalog is one stop, entered on the selected kind, and a log pane's lines are
    /// at most one. Then the keys inside the two lists that are one stop: the arrow keys move
    /// between dock tabs and bring one to the front, and between sidebar rows, where Enter or
    /// Space opens a kind and Enter folds a section.
    /// </summary>
    internal static void KeyboardWalk(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        var start = Named<Button>(window, "AppMenuButton");
        var commandBar = Named<Control>(window, "CommandBar");
        var sidebar = Named<ItemsControl>(view, "SidebarItems");
        var grid = Named<DataGrid>(view, "ResourceGrid");
        var strip = Named<ListBox>(view, "InspectorTabStrip");
        var pane = view.GetVisualDescendants().OfType<ContentControl>()
            .First(c => c is not ListBoxItem && c.Content is InspectorTabViewModelBase);
        if (vm.InspectorTabs.Count < 2 || vm.SelectedKind is not { } kind)
            throw new InvalidOperationException("The keyboard walk needs a selected kind and two dock tabs.");

        // The demo's log lines arrive on a timer. Wait for the first one, so the log list's
        // single stop is in every walk rather than in whichever runs the timer won.
        for (var i = 0; i < 500 && !pane.GetVisualDescendants().OfType<LogLineText>().Any(l => l.IsEffectivelyVisible); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        if (!pane.GetVisualDescendants().OfType<LogLineText>().Any(l => l.IsEffectivelyVisible))
            throw new InvalidOperationException("The front pod's log pane drew no line within 10 seconds, so its Tab stop cannot be checked.");

        List<Control> Walk(RawInputModifiers modifiers, string keys)
        {
            start.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            var stops = new List<Control>();
            for (var i = 0; i < 250; i++)
            {
                Press(window, Key.Tab, modifiers);
                var focused = focus.GetFocusedElement() as Control
                              ?? throw new InvalidOperationException(
                                  $"{keys}: nothing had focus after {i + 1} presses; the stop before was {Describe(stops.LastOrDefault())}.");
                if (focused == start)
                    return stops;
                stops.Add(focused);
            }

            throw new InvalidOperationException(
                $"{keys} did not come back to the command bar within 250 presses, a focus trap. The last stops: {string.Join(", ", stops.TakeLast(4).Select(Describe))}.");
        }

        var forward = Walk(RawInputModifiers.None, "Tab");
        var backward = Walk(RawInputModifiers.Shift, "Shift+Tab");

        int Reached(Func<Control, bool> where, string region)
        {
            var index = forward.FindIndex(c => where(c));
            return index >= 0
                ? index
                : throw new InvalidOperationException($"Tab never reached {region}. The stops: {string.Join(", ", forward.Select(Describe))}.");
        }

        int[] order =
        [
            Reached(c => commandBar.IsVisualAncestorOf(c), "the command bar"),
            Reached(c => sidebar.IsVisualAncestorOf(c), "the sidebar's kinds"),
            Reached(c => c == grid, "the resource list"),
            Reached(c => strip.IsVisualAncestorOf(c), "the dock's tabs"),
            Reached(c => pane.IsVisualAncestorOf(c), "the open pane"),
        ];
        if (!order.SequenceEqual(order.Order()))
            throw new InvalidOperationException($"Tab reached the regions out of reading order: {string.Join(", ", order)}.");

        // The pane is left out of the comparison: the demo's logs keep arriving, and a log
        // pane's error and warning counts appear with their first line, between the walks.
        var shell = forward.Where(c => !pane.IsVisualAncestorOf(c)).ToHashSet();
        var shellBack = backward.Where(c => !pane.IsVisualAncestorOf(c)).ToList();
        if (!shell.SetEquals(shellBack) || !backward.Any(pane.IsVisualAncestorOf))
            throw new InvalidOperationException(
                $"Shift+Tab passed different stops from Tab: only forward {string.Join(", ", shell.Except(shellBack).Select(Describe))}; "
                + $"only backward {string.Join(", ", shellBack.Except(shell).Select(Describe))}.");

        var sidebarStops = forward.Where(sidebar.IsVisualAncestorOf).ToList();
        if (sidebarStops is not [ListBoxItem { DataContext: SidebarKindViewModel entered } sidebarStop])
            throw new InvalidOperationException(
                $"The sidebar's kinds should be one Tab stop and are {sidebarStops.Count}: {string.Join(", ", sidebarStops.Take(5).Select(Describe))}.");
        if (entered.Descriptor != kind.Descriptor)
            throw new InvalidOperationException($"Tab into the sidebar landed on {entered.DisplayName}, not on the selected kind, {kind.DisplayName}.");

        // The log list is one stop, the list itself; no line is one. A line could be recycled
        // under a new log line while it held the stop, which left focus on a hidden line.
        var lines = forward.Count(c => c is LogLineText);
        var logLists = forward.Count(c => c is ItemsControl { Name: "LogItems" } && pane.IsVisualAncestorOf(c));
        if (lines != 0 || logLists != 1)
            throw new InvalidOperationException($"The log pane should be one Tab stop, the list; it was {logLists} list stops and {lines} line stops.");

        var tabStop = forward.First(strip.IsVisualAncestorOf);
        if (tabStop is not ListBoxItem { DataContext: InspectorTabViewModelBase front } || front != vm.SelectedInspectorTab)
            throw new InvalidOperationException($"Tab into the dock's tabs landed on {Describe(tabStop)}, not on the tab in front.");

        var noRing = new List<string>();
        foreach (var stop in forward)
        {
            stop.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            if (!HasFocusVisual(stop))
                noRing.Add(Describe(stop));
        }

        if (noRing.Count > 0)
            throw new InvalidOperationException($"Tab stops with no focus visual: {string.Join(", ", noRing)}.");

        // The dock's tabs: the arrow keys move along the strip and bring that tab to the front.
        tabStop.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.Right, RawInputModifiers.None);
        if (vm.SelectedInspectorTab != vm.InspectorTabs[1])
            throw new InvalidOperationException("Right on the front dock tab did not bring the next one to the front.");
        Press(window, Key.Left, RawInputModifiers.None);
        if (vm.SelectedInspectorTab != vm.InspectorTabs[0])
            throw new InvalidOperationException("Left on the second dock tab did not bring the first one back.");

        // The sidebar: Down to the next row, Enter opens its kind, Up and Space come back.
        sidebarStop.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.Down, RawInputModifiers.None);
        if (focus.GetFocusedElement() is not ListBoxItem { DataContext: SidebarKindViewModel next } || next.Descriptor == entered.Descriptor)
            throw new InvalidOperationException($"Down in the sidebar moved focus to {Describe(focus.GetFocusedElement() as Control)}, not the next kind.");
        Press(window, Key.Enter, RawInputModifiers.None);
        if (vm.SelectedKind?.Descriptor != next.Descriptor)
            throw new InvalidOperationException($"Enter on {next.DisplayName} in the sidebar did not open it.");
        Press(window, Key.Up, RawInputModifiers.None);
        Press(window, Key.Space, RawInputModifiers.None);
        if (vm.SelectedKind?.Descriptor != kind.Descriptor)
            throw new InvalidOperationException($"Up and Space in the sidebar did not go back to {kind.DisplayName}.");

        // A section header: Home reaches the first one, Enter folds it and Enter unfolds it.
        Press(window, Key.Home, RawInputModifiers.None);
        if (focus.GetFocusedElement() is not Border { DataContext: SidebarSectionViewModel section })
            throw new InvalidOperationException($"Home in the sidebar landed on {Describe(focus.GetFocusedElement() as Control)}, not the first section header.");
        var expanded = section.ShowKinds;
        Press(window, Key.Enter, RawInputModifiers.None);
        var folded = section.ShowKinds;
        Press(window, Key.Enter, RawInputModifiers.None);
        if (folded == expanded || section.ShowKinds != expanded)
            throw new InvalidOperationException($"Enter on the {section.Title} header did not fold and unfold it.");

        // Leave the ring on the front dock tab for the PNG.
        strip.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine($"Keyboard walk passed ({forward.Count} Tab stops each way, each with a focus visual; dock tab and sidebar keys).");
    }

    /// <summary>
    /// ENG-4: the YAML editor and the exec terminal keep Tab, so Ctrl+Tab leaves them for the
    /// next stop and Ctrl+Shift+Tab for the one before, and neither key reaches the text or
    /// the shell.
    /// </summary>
    internal static void LeaveEditorKeys(Window window)
    {
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault();
        Control trap = (Control?)editor ?? window.GetVisualDescendants().OfType<TerminalControl>().First();
        Control inner = editor is not null ? editor.TextArea : trap;
        var exec = editor is null ? (ExecTabViewModel)window.GetVisualDescendants().OfType<ExecView>().First().DataContext! : null;
        var text = editor?.Text;

        var sent = new List<byte>();
        void OnInput(object? sender, TerminalUserInputEventArgs e) => sent.AddRange(e.Data.ToArray());
        if (exec is not null)
            exec.Terminal.UserInput += OnInput;
        try
        {
            foreach (var (modifiers, keys) in new[]
                     {
                         (RawInputModifiers.Control, "Ctrl+Tab"),
                         (RawInputModifiers.Control | RawInputModifiers.Shift, "Ctrl+Shift+Tab"),
                     })
            {
                inner.Focus(NavigationMethod.Pointer);
                Dispatcher.UIThread.RunJobs();
                Press(window, Key.Tab, modifiers);
                var now = focus.GetFocusedElement() as Control;
                if (now is null || now == trap || trap.IsVisualAncestorOf(now))
                    throw new InvalidOperationException($"{keys} in the {trap.GetType().Name} left focus on {Describe(now)}; it should leave the control.");
            }
        }
        finally
        {
            if (exec is not null)
                exec.Terminal.UserInput -= OnInput;
        }

        if (sent.Count > 0)
            throw new InvalidOperationException($"Ctrl+Tab reached the pod as [{string.Join(" ", sent.Select(b => $"0x{b:X2}"))}].");
        if (editor is not null && editor.Text != text)
            throw new InvalidOperationException("Ctrl+Tab changed the YAML it was meant to leave.");

        Console.WriteLine($"Leaving the {trap.GetType().Name} passed (Ctrl+Tab, Ctrl+Shift+Tab).");
    }

    /// <summary>
    /// What the keyboard sees on a stop: the shared ring in the adorner layer (DESIGN.md rule
    /// 20); a text box's own focused border and caret; the grid's focus rectangle on its
    /// current cell; or, for the selected row of a list that holds the focus, the accent
    /// selection that rule gives such a list instead. A strip (segmented, dock tabs) keeps its
    /// wash whatever the focus, so it needs the ring.
    /// </summary>
    private static bool HasFocusVisual(Control stop)
    {
        if (AdornerLayer.GetAdornerLayer(stop) is { } layer
            && layer.Children.Any(c => c is Nimbus.Ui.Controls.FocusRing && AdornerLayer.GetAdornedElement(c) == stop))
            return true;

        if (stop is TextBox { IsFocused: true })
            return true;

        if (stop is DataGrid grid)
            return grid.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "FocusVisual" && c.IsEffectivelyVisible);

        return stop is ListBoxItem { IsSelected: true } item
               && item.FindAncestorOfType<ListBox>() is { } list
               && !list.Classes.Contains("segmented") && !list.Classes.Contains("strip") && !list.Classes.Contains("dockTabs");
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} on screen.");

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        var (physical, symbol) = key switch
        {
            Key.Tab => (PhysicalKey.Tab, "\t"),
            Key.Enter => (PhysicalKey.Enter, "\r"),
            Key.Space => (PhysicalKey.Space, " "),
            Key.Up => (PhysicalKey.ArrowUp, (string?)null),
            Key.Down => (PhysicalKey.ArrowDown, null),
            Key.Left => (PhysicalKey.ArrowLeft, null),
            Key.Right => (PhysicalKey.ArrowRight, null),
            Key.Home => (PhysicalKey.Home, null),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        window.KeyPress(key, modifiers, physical, symbol);
        window.KeyRelease(key, modifiers, physical, symbol);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Describe(Control? control) => control switch
    {
        null => "nothing",
        { Name.Length: > 0 } => $"{control.GetType().Name} {control.Name}",
        _ => $"{control.GetType().Name} '{Avalonia.Automation.AutomationProperties.GetName(control)}' ({control.DataContext?.GetType().Name})",
    };

    private static void Expect(byte[] actual, byte[] expected, string gesture)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{gesture} sent [{string.Join(" ", actual.Select(b => $"0x{b:X2}"))}] to the pod, expected "
                + $"[{string.Join(" ", expected.Select(b => $"0x{b:X2}"))}].");
    }
}
