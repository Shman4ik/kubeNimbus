using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The Applications mode driven through the real rendered window: the list's keys, the
/// chips by pointer, the mode switch by pointer, and the page's Esc. Each step throws on a
/// regression, because every one of these can render perfectly and do nothing — a chip
/// wired with a command beside its two-way IsChecked (UI rule 8b), a key a ListBox swallows
/// before the view sees it, an Esc a focused child eats.
/// </summary>
internal static class ApplicationsChecks
{
    internal static void SettlePage(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    internal static void Keys(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ApplicationsView>().First(v => v.IsEffectivelyVisible);
        var vm = (ApplicationsViewModel)view.DataContext!;
        var shell = (MainWindowViewModel)window.DataContext!;
        var list = view.FindControl<ListBox>("AppList")!;
        var filter = view.FindControl<TextBox>("FilterBox")!;

        if (vm.Rows.Count < 5 || vm.VisibleRows[0].GroupHeader?.StartsWith("NEEDS ATTENTION", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("The demo list did not open with the Needs attention group first.");

        // Arrows and Enter.
        vm.SelectedRow = vm.VisibleRows[0];
        Dispatcher.UIThread.RunJobs();
        view.FocusList();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        if (!ReferenceEquals(vm.SelectedRow, vm.VisibleRows[1]))
            throw new InvalidOperationException("Down did not move the selection to the second row.");
        var opened = vm.SelectedRow!;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        if (vm.Page?.Key != opened.Key)
            throw new InvalidOperationException("Enter did not open the selected application.");

        // Esc on the page: back, same row selected.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (vm.IsPageOpen || !ReferenceEquals(vm.SelectedRow, opened))
            throw new InvalidOperationException("Esc did not return to the list with the same row selected.");

        // "/" to the search box, type, Esc clears, Esc again returns to the rows.
        view.FocusList();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.OemQuestion, RawInputModifiers.None, PhysicalKey.Slash, "/");
        Dispatcher.UIThread.RunJobs();
        if (!filter.IsFocused)
            throw new InvalidOperationException("/ did not focus the search box.");
        window.KeyTextInput("payments");
        Dispatcher.UIThread.RunJobs();
        if (vm.Filter != "payments" || vm.VisibleRows.Count == 0 || vm.VisibleRows.Any(r => !r.Matches("payments")))
            throw new InvalidOperationException("Typing in the search box did not narrow by namespace.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (vm.Filter.Length != 0)
            throw new InvalidOperationException("Esc did not clear the search.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (filter.IsFocused)
            throw new InvalidOperationException("A second Esc did not hand focus back to the rows.");

        // Ctrl/Cmd+F from the window.
        window.KeyPress(Key.F, (RawInputModifiers)Hotkeys.Primary, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs();
        if (!filter.IsFocused)
            throw new InvalidOperationException("Ctrl/Cmd+F did not focus the Applications search box.");

        // The chips, by pointer: one-of, and a click on the checked one keeps it on.
        var attention = view.FindControl<KubeNimbus.App.Controls.RadioChip>("AttentionChip")!;
        Click(window, attention);
        if (vm.Chip != ApplicationChip.NeedsAttention || vm.VisibleRows.Any(r => !r.NeedsAttention))
            throw new InvalidOperationException("The Needs attention chip did not narrow the list.");
        Click(window, attention);
        if (vm.Chip != ApplicationChip.NeedsAttention || attention.IsChecked != true)
            throw new InvalidOperationException("Clicking the checked chip turned it off; one chip must stay on.");
        Click(window, view.FindControl<KubeNimbus.App.Controls.RadioChip>("AllChip")!);
        if (vm.Chip != ApplicationChip.All)
            throw new InvalidOperationException("The All chip did not bring every row back.");

        // Double-click a row, then Esc. Enter alone hid this: a double-click opens the page
        // from inside the second press, the row under the pointer then took focus, and Esc
        // went to the list the page had hidden — found on a live cluster, not here.
        var target = vm.VisibleRows[0];
        var container = list.ContainerFromItem(target) as Control
            ?? throw new InvalidOperationException("The first application row has no container.");
        var centre = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        SettlePage(window);
        if (vm.Page?.Key != target.Key)
            throw new InvalidOperationException("Double-clicking a row did not open its application.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (vm.IsPageOpen)
            throw new InvalidOperationException("Esc after a double-click did not return to the list: focus stayed on the hidden row.");

        // One click opens a row by default (OpenApplicationsOnSingleClick), and opens the row
        // it landed on, not the one selected before. With the setting off, one click only
        // selects and a double-click still opens.
        var single = vm.VisibleRows[1];
        ClickRow(window, list, single);
        SettlePage(window);
        if (vm.Page?.Key != single.Key)
            throw new InvalidOperationException("One click on a row did not open its application.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        if (vm.IsPageOpen)
            throw new InvalidOperationException("Esc after a single click did not return to the list.");

        KubeNimbus.App.App.Update(s => s with { OpenApplicationsOnSingleClick = false });
        try
        {
            ClickRow(window, list, target);
            SettlePage(window);
            if (vm.IsPageOpen || !ReferenceEquals(vm.SelectedRow, target))
                throw new InvalidOperationException("With one-click open off, a click opened the application instead of selecting it.");
            ClickRow(window, list, target);
            ClickRow(window, list, target);
            SettlePage(window);
            if (vm.Page?.Key != target.Key)
                throw new InvalidOperationException("With one-click open off, a double-click did not open the application.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            KubeNimbus.App.App.Update(s => s with { OpenApplicationsOnSingleClick = true });
        }

        // A header sorts by pointer, clicked at the cell's far edge where no text is (UI rule
        // 8): ascending, descending, then the list's own order with its groups back.
        var restartsHeader = view.FindControl<Button>("RestartsHeader")!;
        ClickAt(window, restartsHeader, restartsHeader.Bounds.Width - 2);
        ClickAt(window, restartsHeader, restartsHeader.Bounds.Width - 2);
        if (vm.SortColumn != ApplicationSortColumn.Restarts || !vm.SortDescending
            || vm.VisibleRows.Zip(vm.VisibleRows.Skip(1)).Any(p => p.First.Assessment.Restarts < p.Second.Assessment.Restarts)
            || restartsHeader.Content as string != "Restarts ↓")
            throw new InvalidOperationException("Two clicks on the Restarts header did not sort the list by restarts, most first.");
        ClickAt(window, restartsHeader, restartsHeader.Bounds.Width - 2);
        if (vm.SortColumn is not null || vm.VisibleRows[0].GroupHeader?.StartsWith("NEEDS ATTENTION", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("A third click on the header did not return the list to its own order.");

        // The namespace picker from the keyboard: Ctrl/Cmd+Shift+N, type, Enter.
        view.FocusList();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.N, (RawInputModifiers)Hotkeys.Primary | RawInputModifiers.Shift, PhysicalKey.N, "N");
        SettlePage(window);
        var namespaceSearch = view.FindControl<TextBox>("NamespaceSearch")!;
        if (!namespaceSearch.IsFocused)
            throw new InvalidOperationException($"{Hotkeys.PrimaryLabel}+Shift+N did not open the namespace picker with its search focused.");
        window.KeyTextInput("payments");
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        SettlePage(window);
        if (!vm.SelectedNamespaces.SequenceEqual(["payments"]) || vm.VisibleRows.Count == 0
            || vm.VisibleRows.Any(r => !r.Namespaces.Contains("payments")))
            throw new InvalidOperationException("Choosing payments in the namespace picker did not narrow the list to it.");

        // Several namespaces by pointer: a row's box adds it and the picker stays open, and
        // the list holds both namespaces' applications. A click elsewhere on a row chooses it
        // alone and closes the picker.
        var namespaceButton = view.FindControl<Button>("NamespaceButton")!;
        vm.SetNamespaces([]);
        Click(window, namespaceButton);
        SettlePage(window);
        var namespaceList = view.FindControl<ListBox>("NamespaceList")!;
        foreach (var name in new[] { "payments", "monitoring" })
        {
            var choice = vm.NamespaceChoices.FirstOrDefault(c => c.Name == name)
                ?? throw new InvalidOperationException($"The namespace picker does not offer {name}.");
            namespaceList.ScrollIntoView(choice);
            SettlePage(window);
            UxInteractionChecks.ClickNamespaceBox(window, namespaceList, choice);
        }

        if (!vm.SelectedNamespaces.SequenceEqual(["monitoring", "payments"])
            || vm.VisibleRows.Any(r => !r.Namespaces.Any(n => n is "payments" or "monitoring"))
            || !vm.VisibleRows.Any(r => r.Namespaces.Contains("monitoring")))
            throw new InvalidOperationException("Two clicks in the namespace picker did not choose both namespaces.");
        if (namespaceButton.Flyout is not { IsOpen: true } flyout)
            throw new InvalidOperationException("A click on a row's box closed the namespace picker; choosing several needs it to stay open.");
        var monitoring = vm.NamespaceChoices.First(c => c.Name == "monitoring");
        var monitoringRow = (Control)namespaceList.ContainerFromItem(monitoring)!;
        ClickAt(window, monitoringRow, monitoringRow.Bounds.Width - 6);
        if (!vm.SelectedNamespaces.SequenceEqual(["monitoring"]) || flyout.IsOpen)
            throw new InvalidOperationException("A click on a namespace row did not choose it alone and close the picker.");
        vm.SetNamespaces([]);
        Dispatcher.UIThread.RunJobs();

        // The mode switch, by pointer, and back — nothing is restarted either way.
        var rows = vm.Rows.Count;
        var switcher = window.FindControl<ListBox>("ModeSwitch")!;
        Click(window, window.FindControl<ListBoxItem>("ResourcesModeItem")!);
        if (shell.Mode != ShellMode.Resources || !window.GetVisualDescendants().OfType<ClusterTabView>().Any(v => v.IsEffectivelyVisible))
            throw new InvalidOperationException("Clicking Resources did not switch the content to the explorer.");
        Click(window, window.FindControl<ListBoxItem>("ApplicationsModeItem")!);
        if (shell.Mode != ShellMode.Applications || vm.Rows.Count != rows || switcher.SelectedIndex != 0)
            throw new InvalidOperationException("Switching back to Applications lost the list.");

        // The two controls named Applications agree: on a page, the selected mode segment
        // goes back to the list like the page's "‹ Applications" link. From Resources it only
        // switches mode and the page is kept.
        ClickRow(window, list, target);
        SettlePage(window);
        if (vm.Page?.Key != target.Key)
            throw new InvalidOperationException("One click did not open the application before the mode-switch check.");
        Click(window, window.FindControl<ListBoxItem>("ResourcesModeItem")!);
        Click(window, window.FindControl<ListBoxItem>("ApplicationsModeItem")!);
        SettlePage(window);
        if (shell.Mode != ShellMode.Applications || vm.Page?.Key != target.Key)
            throw new InvalidOperationException("Coming back from Resources closed the application page; switching mode must keep it.");
        Click(window, window.FindControl<ListBoxItem>("ApplicationsModeItem")!);
        SettlePage(window);
        if (vm.IsPageOpen || !ReferenceEquals(vm.SelectedRow, target))
            throw new InvalidOperationException("The selected Applications segment did not go back to the list from a page.");

        Console.WriteLine($"Applications interaction passed ({rows} applications; arrows, Enter, Esc, /, {Hotkeys.PrimaryLabel}+F, chips, header sort by pointer, {Hotkeys.PrimaryLabel}+Shift+N namespace picker, two namespaces by pointer, double-click then Esc, one click on and off, mode switch, Applications segment back to the list).");
    }

    /// <summary>Opens the namespace picker for its screenshot: two namespaces chosen, so the checks show.</summary>
    internal static void OpenNamespacePicker(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ApplicationsView>().First(v => v.IsEffectivelyVisible);
        var button = view.FindControl<Button>("NamespaceButton")!;
        button.Flyout!.ShowAt(button);
        SettlePage(window);
    }

    private static void ClickAt(Window window, Control target, double x)
    {
        var point = target.TranslatePoint(new Point(x, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{target.Name} is not in the window.");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickRow(Window window, ListBox list, ApplicationRowViewModel row)
    {
        var container = list.ContainerFromItem(row) as Control
            ?? throw new InvalidOperationException($"Application row {row.Name} has no container.");
        var centre = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{target.Name} is not in the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
