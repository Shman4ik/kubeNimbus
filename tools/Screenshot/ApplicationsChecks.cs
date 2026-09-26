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

        // The mode switch, by pointer, and back — nothing is restarted either way.
        var rows = vm.Rows.Count;
        var switcher = window.FindControl<ListBox>("ModeSwitch")!;
        Click(window, window.FindControl<ListBoxItem>("ResourcesModeItem")!);
        if (shell.Mode != ShellMode.Resources || !window.GetVisualDescendants().OfType<ClusterTabView>().Any(v => v.IsEffectivelyVisible))
            throw new InvalidOperationException("Clicking Resources did not switch the content to the explorer.");
        Click(window, window.FindControl<ListBoxItem>("ApplicationsModeItem")!);
        if (shell.Mode != ShellMode.Applications || vm.Rows.Count != rows || switcher.SelectedIndex != 0)
            throw new InvalidOperationException("Switching back to Applications lost the list.");

        Console.WriteLine($"Applications interaction passed ({rows} applications; arrows, Enter, Esc, /, {Hotkeys.PrimaryLabel}+F, chips, mode switch).");
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
