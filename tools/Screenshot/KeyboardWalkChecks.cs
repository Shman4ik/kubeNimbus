using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #236: the keyboard walk of the parts <see cref="KeyboardChecks.KeyboardWalk"/> does not
/// reach, the Applications mode (its list and an application's page) and the overlays a
/// keyboard user opens (the palette, the cluster switcher, the preferences). The contract is
/// the same as there (UI rule 7b): Tab and Shift+Tab come back round with no trap, pass the
/// same stops both ways, each stop draws a focus visual, and a list is one stop. An overlay
/// adds two of its own: Tab never leaves it for a control under the scrim, and Esc closes it
/// from any of its stops and gives focus back to what had it (UI rule 16b).
/// </summary>
internal static class KeyboardWalkChecks
{
    /// <summary>Scenario <c>ux-keyboard-walk-applications</c>: the demo's Applications list.</summary>
    internal static void Applications(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ApplicationsView>().First(v => v.IsEffectivelyVisible);
        var vm = (ApplicationsViewModel)view.DataContext!;
        var start = Named<Button>(window, "AppMenuButton");
        var commandBar = Named<Control>(window, "CommandBar");
        var list = Named<ListBox>(view, "AppList");
        if (vm.VisibleRows.Count < 2)
            throw new InvalidOperationException("The Applications walk needs a list with rows.");

        vm.SelectedRow = vm.VisibleRows[0];
        Settle();

        var (forward, backward) = RoundTrip(window, start, "the Applications list");
        int Reached(Func<Control, bool> where, string region)
        {
            var index = forward.FindIndex(c => where(c));
            return index >= 0
                ? index
                : throw new InvalidOperationException($"Tab never reached {region} in the Applications mode. The stops: {string.Join(", ", forward.Select(KeyboardChecks.Describe))}.");
        }

        int[] order =
        [
            Reached(commandBar.IsVisualAncestorOf, "the command bar"),
            Reached(c => c.Name == "NamespaceButton" && view.IsVisualAncestorOf(c), "the namespace picker"),
            Reached(c => c.Name == "FilterBox" || c.FindAncestorOfType<TextBox>() is { Name: "FilterBox" }, "the search box"),
            Reached(c => c.Name == "AllChip", "the All applications chip"),
            Reached(c => c == list || list.IsVisualAncestorOf(c), "the list"),
        ];
        if (!order.SequenceEqual(order.Order()))
            throw new InvalidOperationException($"Tab reached the Applications mode's regions out of reading order: {string.Join(", ", order)}.");

        var inList = forward.Where(c => c == list || list.IsVisualAncestorOf(c)).ToList();
        if (inList is not [ListBoxItem { DataContext: ApplicationRowViewModel entered }] || !ReferenceEquals(entered, vm.SelectedRow))
            throw new InvalidOperationException(
                $"The Applications list should be one Tab stop, on the selected row; it was {inList.Count}: {string.Join(", ", inList.Take(4).Select(KeyboardChecks.Describe))}.");

        SameStops(forward, backward, "the Applications list");
        RequireRings(forward, "the Applications list");

        // Enter on the list opens the row's page; walk the page the same way, then Esc goes
        // back to the list with the same row focused.
        var opened = vm.SelectedRow!;
        inList[0].Focus(NavigationMethod.Tab);
        Settle();
        KeyboardChecks.Press(window, Key.Enter, RawInputModifiers.None);
        Settle();
        if (vm.Page?.Key != opened.Key)
            throw new InvalidOperationException("Enter on the focused Applications row did not open its page.");
        var page = window.GetVisualDescendants().OfType<ApplicationPageView>().FirstOrDefault(p => p.IsEffectivelyVisible)
                   ?? throw new InvalidOperationException("The application page is not on screen after Enter.");

        var (pageForward, pageBackward) = RoundTrip(window, start, "an application's page");
        if (!pageForward.Any(page.IsVisualAncestorOf))
            throw new InvalidOperationException($"Tab never reached the application page. The stops: {string.Join(", ", pageForward.Select(KeyboardChecks.Describe))}.");
        if (pageForward.Any(c => list.IsVisualAncestorOf(c) && c.IsEffectivelyVisible))
            throw new InvalidOperationException("Tab on the application page reached the list hidden under it.");
        if (pageForward.Any(c => c is ApplicationPageView or SelectableTextBlock))
            throw new InvalidOperationException(
                $"The application page itself or a line of its text is a Tab stop: {string.Join(", ", pageForward.Where(c => c is ApplicationPageView or SelectableTextBlock).Take(4).Select(KeyboardChecks.Describe))}.");
        var podList = Named<ListBox>(page, "PodList");
        var pageVm = (ApplicationPageViewModel)page.DataContext!;
        foreach (var (walk, keys) in new[] { (pageForward, "Tab"), (pageBackward, "Shift+Tab") })
        {
            var pods = walk.Where(podList.IsVisualAncestorOf).ToList();
            if (pods is not [ListBoxItem { DataContext: PagePodViewModel pod }] || !ReferenceEquals(pod, pageVm.SelectedPod))
                throw new InvalidOperationException(
                    $"{keys} into the page's pods should land once, on the selected pod ({pageVm.SelectedPod?.Name}); it landed on {string.Join(", ", pods.Select(KeyboardChecks.Describe))}.");
        }

        var linkNames = pageForward.Where(c => c.Classes.Contains("linkRow"))
            .Select(c => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName()).ToList();
        if (linkNames.Count > 1 && linkNames.Distinct().Count() != linkNames.Count)
            throw new InvalidOperationException($"The page's linked rows share a name, so a screen reader cannot tell them apart: {string.Join(", ", linkNames)}.");

        SameStops(pageForward, pageBackward, "an application's page");
        RequireRings(pageForward, "an application's page");

        pageForward.First(page.IsVisualAncestorOf).Focus(NavigationMethod.Tab);
        Settle();
        KeyboardChecks.Press(window, Key.Escape, RawInputModifiers.None);
        Settle();
        if (vm.Page is not null)
            throw new InvalidOperationException("Esc on the application page did not go back to the list.");
        if (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() is not Control back || !(back == list || list.IsVisualAncestorOf(back)))
            throw new InvalidOperationException("Esc back from the application page did not give focus back to the list.");

        Console.WriteLine($"Applications keyboard walk passed (list {forward.Count} stops, page {pageForward.Count} stops each way, each with a focus visual; no trap).");
    }

    /// <summary>
    /// Scenario <c>ux-keyboard-walk-overlays</c>: the palette, the cluster switcher and the
    /// preferences, each opened from a focused control, walked inside, and closed by Esc from a
    /// stop other than the one it opened on.
    /// </summary>
    internal static void Overlays(Window window)
    {
        var shell = (MainWindowViewModel)window.DataContext!;
        var main = (MainWindow)window;
        var opener = Named<Button>(window, "AppMenuButton");

        Walk(window, opener, "the command palette",
            open: main.OpenPalette,
            isOpen: () => shell.Palette.IsOpen,
            root: () => Named<TextBox>(window, "PaletteQueryBox").GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("scrim")));
        Walk(window, opener, "the cluster switcher",
            open: () => shell.OpenSwitcherCommand.Execute(null),
            isOpen: () => shell.Switcher.IsOpen,
            root: () => Named<TextBox>(window, "SwitcherQueryBox").GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("scrim")));
        Walk(window, opener, "the preferences",
            open: () => shell.ShowPreferencesCommand.Execute(null),
            isOpen: () => shell.IsPreferencesOpen,
            root: () => window.GetVisualDescendants().OfType<Nimbus.Ui.Controls.OverlayPanel>().First(p => p.IsOpen && p.Title == "Preferences"));

        // The PNG: the preferences open with the keyboard on the shortcut modifier's drop-down,
        // the stop that drew no ring before (#236).
        shell.ShowPreferencesCommand.Execute(null);
        Settle();
        window.GetVisualDescendants().OfType<PreferencesView>().First().GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible).Focus(NavigationMethod.Tab);
        Settle();

        Console.WriteLine("Overlay keyboard walk passed (palette, switcher, preferences: Tab stays inside and cycles, every stop has a focus visual, Esc from any stop closes and gives focus back).");
    }

    /// <summary>
    /// Scenario <c>ux-log-line-keys</c>: a pod's logs with JSON lines. With the log list
    /// focused, Up lands on the newest line and draws it, Up moves, Copy copies that line as
    /// drawn, and Enter on a JSON line opens its fields and closes them again; focus stays on
    /// the list throughout (#236, <c>LogLineCursor</c>).
    /// </summary>
    internal static void LogLineKeys(Window window)
    {
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        var list = window.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "LogItems" && c.IsEffectivelyVisible);
        var pane = (PodDetailTabViewModel)list.FindAncestorOfType<PodDetailView>()!.DataContext!;
        for (var i = 0; i < 500 && pane.LogLines.Count == 0; i++)
        {
            Settle();
            Thread.Sleep(20);
        }

        var lines = pane.LogLines;
        if (lines.Count < 3 || !lines.Any(l => l.IsJson))
            throw new InvalidOperationException($"The pod's log pane shows {lines.Count} lines, {lines.Count(l => l.IsJson)} of them JSON; the check needs JSON lines.");

        list.Focus(NavigationMethod.Tab);
        Settle();
        KeyboardChecks.Press(window, Key.Up, RawInputModifiers.None);
        var newest = lines[^1];
        if (!newest.IsKeyLine || lines.Count(l => l.IsKeyLine) != 1)
            throw new InvalidOperationException("Up in a focused log list did not put the keys on the newest line, and on it alone.");
        KeyboardChecks.Press(window, Key.Up, RawInputModifiers.None);
        var line = lines[^2];
        if (!line.IsKeyLine || newest.IsKeyLine)
            throw new InvalidOperationException("A second Up in the log list did not move to the line before.");
        Settle();
        if ((list.ContainerFromItem(line) as Control)?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("logRow")) is not { BorderThickness.Top: > 0 })
            throw new InvalidOperationException("The line the keys are on is not drawn: its row has no outline while the list has focus.");

        var clipboard = window.Clipboard ?? throw new InvalidOperationException("The headless window has no clipboard.");
        var cleared = clipboard.SetTextAsync("");
        while (!cleared.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        var primary = (RawInputModifiers)Nimbus.Ui.Hotkeys.Primary;
        window.KeyPress(Key.C, primary, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, primary, PhysicalKey.C, "c");
        Settle();
        var read = clipboard.TryGetTextAsync();
        while (!read.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        if (read.Result != line.DisplayText)
            throw new InvalidOperationException($"Copy on the log line put '{read.Result}' on the clipboard, not the line '{line.DisplayText}'.");

        // Home, then Down to the first JSON line; Enter opens it and Enter closes it.
        KeyboardChecks.Press(window, Key.Home, RawInputModifiers.None);
        var json = lines.First(l => l.IsJson);
        for (var i = 0; i < lines.Count && !json.IsKeyLine; i++)
            KeyboardChecks.Press(window, Key.Down, RawInputModifiers.None);
        if (!json.IsKeyLine)
            throw new InvalidOperationException("Home and Down never reached the first JSON line.");
        KeyboardChecks.Press(window, Key.Enter, RawInputModifiers.None);
        if (!json.IsExpanded)
            throw new InvalidOperationException("Enter on a JSON log line did not open its fields.");
        KeyboardChecks.Press(window, Key.Enter, RawInputModifiers.None);
        if (json.IsExpanded)
            throw new InvalidOperationException("A second Enter on the JSON line did not close its fields.");
        if (focus.GetFocusedElement() != list)
            throw new InvalidOperationException($"The log line keys moved focus off the list, to {KeyboardChecks.Describe(focus.GetFocusedElement() as Control)}.");

        // The PNG: the JSON line open under the keys.
        KeyboardChecks.Press(window, Key.Enter, RawInputModifiers.None);
        Console.WriteLine("Log line keys passed (Up to the newest line, Copy copies it, Enter opens and closes a JSON line).");
    }

    private static void Walk(Window window, Control opener, string what, Action open, Func<bool> isOpen, Func<Control?> root)
    {
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        opener.Focus(NavigationMethod.Tab);
        Settle();
        open();
        Settle();
        if (!isOpen())
            throw new InvalidOperationException($"Could not open {what}.");
        var panel = root() ?? throw new InvalidOperationException($"{what} has no overlay on screen.");

        bool Inside(Control c) => c == panel || panel.IsVisualAncestorOf(c);
        if (focus.GetFocusedElement() is not Control first || !Inside(first))
            throw new InvalidOperationException($"Opening {what} left focus on {KeyboardChecks.Describe(focus.GetFocusedElement() as Control)}, outside it.");

        Control Next(RawInputModifiers modifiers, string keys, int presses)
        {
            KeyboardChecks.Press(window, Key.Tab, modifiers);
            var now = focus.GetFocusedElement() as Control
                      ?? throw new InvalidOperationException($"{keys} in {what} left focus on nothing after {presses} presses.");
            return Inside(now)
                ? now
                : throw new InvalidOperationException(
                    $"{keys} in {what} left it for {KeyboardChecks.Describe(now)}, a control under the scrim, after {presses} presses.");
        }

        // The first Tab finds the entry stop (focus opens on the search box, or on the panel
        // itself, which is no stop); each walk then starts there and has to come back to it.
        var entry = Next(RawInputModifiers.None, "Tab", 1);
        List<Control> Cycle(RawInputModifiers modifiers, string keys)
        {
            entry.Focus(NavigationMethod.Tab);
            Settle();
            var stops = new List<Control> { entry };
            for (var i = 0; i < 100; i++)
            {
                var now = Next(modifiers, keys, i + 1);
                if (now == entry)
                    return stops;
                if (stops.Contains(now))
                    throw new InvalidOperationException($"{keys} in {what} went round a loop that does not pass {KeyboardChecks.Describe(entry)}: {string.Join(", ", stops.Append(now).Select(c => $"{KeyboardChecks.Describe(c)} {(c as ContentControl)?.Content as string}"))}.");
                stops.Add(now);
            }

            throw new InvalidOperationException($"{keys} in {what} did not come round within 100 presses.");
        }

        var forward = Cycle(RawInputModifiers.None, "Tab");
        var backward = Cycle(RawInputModifiers.Shift, "Shift+Tab");
        SameStops(forward, backward, what);
        RequireRings(forward.Where(c => c != panel).ToList(), what);

        // Esc from a stop other than the one it opened on: the palette's and the switcher's
        // Esc used to be their search box's key.
        var away = forward.LastOrDefault(c => c != first && c != panel) ?? first;
        away.Focus(NavigationMethod.Tab);
        Settle();
        KeyboardChecks.Press(window, Key.Escape, RawInputModifiers.None);
        Settle();
        if (isOpen())
            throw new InvalidOperationException($"Esc on {KeyboardChecks.Describe(away)} in {what} did not close it, a trap for a keyboard user.");
        if (!opener.IsFocused)
            throw new InvalidOperationException($"Closing {what} with Esc left focus on {KeyboardChecks.Describe(focus.GetFocusedElement() as Control)}, not on what had it.");
    }

    private static (List<Control> Forward, List<Control> Backward) RoundTrip(Window window, Control start, string where)
    {
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        List<Control> Walk(RawInputModifiers modifiers, string keys)
        {
            start.Focus(NavigationMethod.Tab);
            Settle();
            var stops = new List<Control>();
            for (var i = 0; i < 250; i++)
            {
                KeyboardChecks.Press(window, Key.Tab, modifiers);
                var focused = focus.GetFocusedElement() as Control
                              ?? throw new InvalidOperationException(
                                  $"{keys} on {where}: nothing had focus after {i + 1} presses; the stop before was {KeyboardChecks.Describe(stops.LastOrDefault())}.");
                if (focused == start)
                    return stops;
                stops.Add(focused);
            }

            throw new InvalidOperationException(
                $"{keys} on {where} did not come back to the command bar within 250 presses, a focus trap. The last stops: {string.Join(", ", stops.TakeLast(4).Select(KeyboardChecks.Describe))}.");
        }

        return (Walk(RawInputModifiers.None, "Tab"), Walk(RawInputModifiers.Shift, "Shift+Tab"));
    }

    private static void SameStops(List<Control> forward, List<Control> backward, string where)
    {
        var there = forward.ToHashSet();
        if (!there.SetEquals(backward))
            throw new InvalidOperationException(
                $"Shift+Tab passed different stops from Tab on {where}: only forward {string.Join(", ", there.Except(backward).Select(KeyboardChecks.Describe))}; "
                + $"only backward {string.Join(", ", backward.Except(there).Select(KeyboardChecks.Describe))}.");
    }

    private static void RequireRings(List<Control> stops, string where)
    {
        var noRing = new List<string>();
        foreach (var stop in stops)
        {
            stop.Focus(NavigationMethod.Tab);
            Settle();
            if (!KeyboardChecks.HasFocusVisual(stop))
                noRing.Add(KeyboardChecks.Describe(stop));
        }

        if (noRing.Count > 0)
            throw new InvalidOperationException($"Tab stops on {where} with no focus visual: {string.Join(", ", noRing)}.");
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} on screen.");

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
