using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.Controls;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #236: what UI Automation can do with the controls a keyboard or a screen reader reaches,
/// asserted on their headless peers. The walk in <see cref="AutomationChecks"/> asks every
/// peer for a name; these ask the patterns: a section header that folds, a drawn check box
/// that toggles, cluster tabs that are tabs, grid rows with a name and a selected state, a
/// menu item named by its label, and a chip whose toggle is refused staying unpressed. A
/// headless peer is not the Windows provider, so the QA VM's <c>qa-ui.ps1</c> run is still
/// the check that counts for a release (accessible-names).
/// </summary>
internal static class AutomationPatternChecks
{
    /// <summary>Scenario <c>ux-automation-patterns</c>: a demo list.</summary>
    internal static void Patterns(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        SectionHeaders(view, vm);
        GridRows(window, view, vm);
        MenuItemNames();
        NamespaceChecks(window, view, vm);
        ClusterTabs(window);
        AppMenuItems(window);
        Console.WriteLine("Automation patterns: section headers fold, grid rows named and marked, menu items named by label, namespace boxes toggle, cluster tabs are tabs, the app menu's items invoke.");
    }

    private static void SectionHeaders(ClusterTabView view, ClusterTabViewModel vm)
    {
        var headers = view.GetVisualDescendants().OfType<SidebarSectionHeader>().Where(h => h.IsEffectivelyVisible).ToList();
        if (headers.Count == 0)
            throw new InvalidOperationException("No sidebar section header on screen.");

        foreach (var header in headers)
        {
            var section = (SidebarSectionViewModel)header.DataContext!;
            if (ControlAutomationPeer.CreatePeerForElement(header) is not IExpandCollapseProvider pattern)
                throw new InvalidOperationException($"The {section.Title} header has no ExpandCollapse pattern.");
            var expected = section.ShowKinds ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
            if (pattern.ExpandCollapseState != expected)
                throw new InvalidOperationException($"The {section.Title} header reports {pattern.ExpandCollapseState} while its kinds are {(section.ShowKinds ? "showing" : "hidden")}.");
        }

        var workloads = headers.Select(h => (Header: h, Section: (SidebarSectionViewModel)h.DataContext!)).First(h => h.Section.Title == "Workloads");
        var provider = (IExpandCollapseProvider)ControlAutomationPeer.CreatePeerForElement(workloads.Header);
        provider.Collapse();
        Dispatcher.UIThread.RunJobs();
        if (workloads.Section.ShowKinds || provider.ExpandCollapseState != ExpandCollapseState.Collapsed)
            throw new InvalidOperationException("Collapse through UI Automation did not fold the Workloads section.");

        // #317: with Workloads folded, the selected kind is lit in Recent, and only there.
        var lit = view.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.Classes.Contains("navRow") && i.IsSelected);
        if (lit != 1)
            throw new InvalidOperationException($"{lit} sidebar rows are drawn selected with Workloads folded; exactly one should be (#317).");

        provider.Expand();
        Dispatcher.UIThread.RunJobs();
        if (!workloads.Section.ShowKinds || provider.ExpandCollapseState != ExpandCollapseState.Expanded)
            throw new InvalidOperationException("Expand through UI Automation did not unfold the Workloads section.");

        lit = view.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.Classes.Contains("navRow") && i.IsSelected && i.IsEffectivelyVisible);
        if (lit != 1)
            throw new InvalidOperationException($"{lit} sidebar rows are drawn selected; exactly one should be (#317).");
    }

    private static void GridRows(Window window, ClusterTabView view, ClusterTabViewModel vm)
    {
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        if (vm.VisibleRows.Count < 2)
            throw new InvalidOperationException("The demo list should show at least two rows.");

        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(vm.VisibleRows[0]);
        grid.SelectedItems.Add(vm.VisibleRows[1]);
        Settle();

        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible && r.DataContext is ResourceRowViewModel).ToList();
        foreach (var row in rows)
        {
            var item = (ResourceRowViewModel)row.DataContext!;
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            if (peer.GetName() != item.ToString() || !peer.GetName().StartsWith(item.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"The grid row for {item.Name} is named '{peer.GetName()}'.");
            var selected = grid.SelectedItems.Contains(item);
            var status = peer.GetItemStatus();
            if (selected != (status == "Selected"))
                throw new InvalidOperationException($"The grid row for {item.Name} is {(selected ? "" : "not ")}selected and reports item status '{status}'.");
        }

        grid.SelectedItems.Clear();
        Settle();
    }

    private static void MenuItemNames()
    {
        // The header before the tooltip, as in markup, and the other way round, as code can.
        var first = new MenuItem { Header = "Logs (all pods)" };
        ToolTip.SetTip(first, "Tail every pod this workload owns in one pane, colour-keyed by pod");
        var second = new MenuItem();
        ToolTip.SetTip(second, "Show every column this kind has, at the widths the list starts with.");
        second.Header = "Reset columns";
        foreach (var (item, label) in new[] { (first, "Logs (all pods)"), (second, "Reset columns") })
        {
            var name = ControlAutomationPeer.CreatePeerForElement(item).GetName();
            if (name != label)
                throw new InvalidOperationException($"The menu item '{label}' is named '{name}' through UI Automation; it should be its label.");
        }
    }

    /// <summary>
    /// #236, from the 2026-10 QA runs: the ☰ menu's items (Rescan, Reconnect) took real clicks,
    /// because UI Automation could not invoke them. Through the peers: the ☰ button opens its
    /// menu by Invoke, every item there is named by its label and has the Invoke pattern, and
    /// invoking one runs its command and closes the menu, as a click does. "Keyboard
    /// shortcuts" is the item invoked, since it changes nothing outside the window.
    /// </summary>
    private static void AppMenuItems(Window window)
    {
        var shell = (MainWindowViewModel)window.DataContext!;
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AppMenuButton");
        if (ControlAutomationPeer.CreatePeerForElement(button) is not IInvokeProvider open)
            throw new InvalidOperationException("The ☰ button has no Invoke pattern.");
        var flyout = (MenuFlyout)button.Flyout!;
        open.Invoke();
        Settle();
        if (!flyout.IsOpen)
            throw new InvalidOperationException("Invoking the ☰ button through UI Automation did not open its menu.");

        try
        {
            var items = flyout.Items.OfType<MenuItem>().ToList();
            if (items.Count == 0)
                throw new InvalidOperationException("The ☰ menu has no items.");
            foreach (var item in items)
            {
                var peer = ControlAutomationPeer.CreatePeerForElement(item);
                if (peer.GetName() != item.Header as string)
                    throw new InvalidOperationException($"The ☰ menu's item '{item.Header}' is named '{peer.GetName()}' through UI Automation.");
                if (peer.GetProvider<IInvokeProvider>() is null)
                    throw new InvalidOperationException($"The ☰ menu's item '{item.Header}' has no Invoke pattern, so UI Automation cannot run it.");
            }

            var shortcuts = items.First(i => i.Header as string == "Keyboard shortcuts");
            var wasOpen = shell.IsShortcutsOpen;
            ControlAutomationPeer.CreatePeerForElement(shortcuts).GetProvider<IInvokeProvider>()!.Invoke();
            Settle();
            if (shell.IsShortcutsOpen == wasOpen)
                throw new InvalidOperationException("Invoking the ☰ menu's Keyboard shortcuts through UI Automation did not run its command.");
            if (flyout.IsOpen)
                throw new InvalidOperationException("Invoking a ☰ menu item through UI Automation left the menu open; a click closes it.");
            shell.IsShortcutsOpen = wasOpen;
            Settle();
        }
        finally
        {
            flyout.Hide();
            Settle();
        }
    }

    private static void NamespaceChecks(Window window, ClusterTabView view, ClusterTabViewModel vm)
    {
        vm.SetNamespaces(["payments"]);
        Dispatcher.UIThread.RunJobs();
        var button = view.FindControl<Button>("NamespaceButton")!;
        button.Flyout!.ShowAt(button);
        Settle();
        try
        {
            var list = view.FindControl<ListBox>("NamespaceList")!;
            var monitoring = vm.FilteredNamespaces.First(c => c.Name == "monitoring");
            list.ScrollIntoView(monitoring);
            Settle();
            var mark = (list.ContainerFromItem(monitoring) as Control)?.GetVisualDescendants().OfType<CheckMark>().FirstOrDefault()
                       ?? throw new InvalidOperationException("The monitoring row has no check box.");
            var peer = ControlAutomationPeer.CreatePeerForElement(mark);
            if (peer.GetAutomationControlType() != AutomationControlType.CheckBox || peer is not IToggleProvider toggle)
                throw new InvalidOperationException("A namespace's check box is not a UI Automation check box with the Toggle pattern.");
            if (peer.GetName() != "monitoring" || toggle.ToggleState != ToggleState.Off)
                throw new InvalidOperationException($"The monitoring box reads '{peer.GetName()}', {toggle.ToggleState}; it should be 'monitoring', Off.");

            toggle.Toggle();
            Settle();
            if (!vm.SelectedNamespaces.Contains("monitoring") || toggle.ToggleState != ToggleState.On || !button.Flyout.IsOpen)
                throw new InvalidOperationException("Toggling the monitoring box through UI Automation did not add the namespace with the picker kept open.");

            toggle.Toggle();
            Settle();
            if (vm.SelectedNamespaces.Contains("monitoring") || toggle.ToggleState != ToggleState.Off)
                throw new InvalidOperationException("Toggling the monitoring box again did not take the namespace out.");
        }
        finally
        {
            button.Flyout!.Hide();
            Settle();
        }
    }

    private static void ClusterTabs(Window window)
    {
        var shell = (MainWindowViewModel)window.DataContext!;
        var strip = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TabStrip");
        var second = new ClusterTabViewModel(ClusterContext.Demo);
        shell.Tabs.Add(second);
        Settle();

        var stripPeer = ControlAutomationPeer.CreatePeerForElement(strip);
        if (stripPeer.GetAutomationControlType() != AutomationControlType.Tab)
            throw new InvalidOperationException($"The cluster tab strip reads as {stripPeer.GetAutomationControlType()}, not Tab.");

        var items = strip.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        if (items.Count != shell.Tabs.Count)
            throw new InvalidOperationException($"The cluster tab strip shows {items.Count} items for {shell.Tabs.Count} tabs.");
        foreach (var item in items)
        {
            var tab = (ClusterTabViewModel)item.DataContext!;
            var peer = ControlAutomationPeer.CreatePeerForElement(item);
            if (peer.GetAutomationControlType() != AutomationControlType.TabItem || string.IsNullOrEmpty(peer.GetName()))
                throw new InvalidOperationException($"The cluster tab {tab.Header} reads as {peer.GetAutomationControlType()} '{peer.GetName()}'.");
            if (peer is not ISelectionItemProvider selection || selection.IsSelected != ReferenceEquals(tab, shell.SelectedTab))
                throw new InvalidOperationException($"The cluster tab {tab.Header} does not report whether it is the one in front.");
        }

        // The keys: Right brings the next tab to the front and focuses it; Delete closes it.
        var first = shell.SelectedTab!;
        items[shell.Tabs.IndexOf(first)].Focus(NavigationMethod.Tab);
        Settle();
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        if (!ReferenceEquals(shell.SelectedTab, second) || TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() is not ListBoxItem { DataContext: ClusterTabViewModel focused } || focused != second)
            throw new InvalidOperationException("Right on the focused cluster tab did not bring the next one to the front with focus.");

        Press(window, Key.Delete, PhysicalKey.Delete);
        for (var i = 0; i < 100 && shell.Tabs.Contains(second); i++)
        {
            Settle();
            Thread.Sleep(10);
        }

        Settle();
        if (shell.Tabs.Contains(second) || !ReferenceEquals(shell.SelectedTab, first))
            throw new InvalidOperationException("Delete on the focused cluster tab did not close it.");
        if (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() is not ListBoxItem { DataContext: ClusterTabViewModel left } || left != first)
            throw new InvalidOperationException("After Delete closed a cluster tab, focus did not move to the tab in front.");
    }

    /// <summary>
    /// Scenario <c>ux-dock-tab-delete</c>: Delete on a focused dock tab closes it and leaves
    /// focus on the tab that came to the front (#236).
    /// </summary>
    internal static void DockTabDelete(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var strip = view.FindControl<ListBox>("InspectorTabStrip")!;
        var count = vm.InspectorTabs.Count;
        if (count < 2)
            throw new InvalidOperationException("The scenario should open two dock tabs.");

        var closing = vm.SelectedInspectorTab!;
        ((Control)strip.ContainerFromItem(closing)!).Focus(NavigationMethod.Tab);
        Settle();
        Press(window, Key.Delete, PhysicalKey.Delete);
        for (var i = 0; i < 100 && vm.InspectorTabs.Contains(closing); i++)
        {
            Settle();
            Thread.Sleep(10);
        }

        Settle();
        if (vm.InspectorTabs.Contains(closing) || vm.InspectorTabs.Count != count - 1)
            throw new InvalidOperationException("Delete on the focused dock tab did not close it.");
        if (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() is not ListBoxItem { DataContext: InspectorTabViewModelBase front }
            || front != vm.SelectedInspectorTab)
            throw new InvalidOperationException("After Delete closed a dock tab, focus did not move to the tab in front.");
        Console.WriteLine("Dock tab Delete: closed the focused tab, focus on the one in front.");

        // #370: the QA VM crops the open tab's pane by AutomationId (`qa-ui.ps1 screenshot
        // -Id DockContent`), so the pane has to be in the peer tree a client walks, under that
        // id, with bounds to crop to.
        var content = FindPeer(ControlAutomationPeer.CreatePeerForElement(window), "DockContent")
                      ?? throw new InvalidOperationException("No automation peer with AutomationId DockContent under the window: qa-ui cannot crop the dock's content.");
        if (!content.IsControlElement() || content.GetAutomationControlType() != AutomationControlType.Pane || content.GetBoundingRectangle().Height < 20)
            throw new InvalidOperationException(
                $"DockContent is a {content.GetAutomationControlType()}{(content.IsControlElement() ? "" : " outside the control view")}, {content.GetBoundingRectangle().Height:0}px tall; qa-ui needs a Pane in the control view with bounds to crop.");
        Console.WriteLine($"Dock content: AutomationId DockContent, {content.GetAutomationControlType()}, {content.GetBoundingRectangle().Height:0}px tall.");
    }

    private static AutomationPeer? FindPeer(AutomationPeer peer, string automationId)
    {
        if (peer.GetAutomationId() == automationId)
            return peer;
        foreach (var child in peer.GetChildren())
        {
            if (FindPeer(child, automationId) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Scenario <c>ux-managed-fields-uia</c>: over text that no longer reads as one YAML
    /// document the managed-fields chip refuses, and a Toggle through UI Automation has to
    /// leave it unpressed as a click does (#236, QA check M-8), with the binding to the view
    /// model still working after.
    /// </summary>
    internal static void ManagedFieldsToggle(Window window)
    {
        var editor = window.GetVisualDescendants().OfType<YamlEditorView>().First();
        var vm = (YamlEditorTabViewModel)editor.DataContext!;
        var chip = editor.FindControl<ToggleButton>("ManagedFieldsChip")!;
        var toggle = (IToggleProvider)ControlAutomationPeer.CreatePeerForElement(chip);
        var valid = vm.YamlText;

        vm.YamlText = "metadata: [unclosed\n";
        Settle();
        toggle.Toggle();
        Settle();
        if (vm.ShowManagedFields || chip.IsChecked == true || toggle.ToggleState != ToggleState.Off)
            throw new InvalidOperationException(
                $"Over unparsable YAML a UI Automation toggle left the chip {(chip.IsChecked == true ? "pressed" : "unpressed")} and the view model at {vm.ShowManagedFields}; both should be off.");

        vm.YamlText = valid;
        Settle();
        toggle.Toggle();
        Settle();
        if (!vm.ShowManagedFields || chip.IsChecked != true)
            throw new InvalidOperationException("Over readable YAML a UI Automation toggle did not show the managed fields.");

        vm.ShowManagedFields = false;
        Settle();
        if (chip.IsChecked == true)
            throw new InvalidOperationException("After the toggles the chip no longer follows the view model.");
        Console.WriteLine("Managed-fields chip: a refused UI Automation toggle leaves it unpressed.");
    }

    /// <summary>
    /// Part of <c>cluster-tab-dock-many-tabs</c> (#313): a plain vertical wheel over the dock's
    /// tab strip scrolls it sideways, both ways.
    /// </summary>
    internal static void DockStripWheel(Window window)
    {
        var strip = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "InspectorTabStrip");
        var viewer = strip.GetVisualDescendants().OfType<ScrollViewer>().First();
        viewer.Offset = default;
        Settle();
        if (viewer.Extent.Width <= viewer.Viewport.Width)
            throw new InvalidOperationException("The dock's tab strip does not overflow, so the wheel cannot be checked.");

        var centre = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        window.MouseWheel(centre, new Vector(0, -2));
        Settle();
        var down = viewer.Offset.X;
        if (down <= 0)
            throw new InvalidOperationException("A vertical wheel down over the dock's tab strip did not scroll it (#313).");

        window.MouseWheel(centre, new Vector(0, 2));
        Settle();
        if (viewer.Offset.X >= down)
            throw new InvalidOperationException("A vertical wheel up over the dock's tab strip did not scroll it back (#313).");

        // Off the strip again before the capture. Left over a tab, the pointer opened that tab's
        // tooltip once its show delay had passed on the wall clock, so whether the PNG had a
        // tooltip in it depended on how long the log settle after this check took: always,
        // while the demo replayed a line every 280 ms, and never once it was shortened.
        window.MouseMove(new Point(2, 2));
        Settle();
        Console.WriteLine($"Dock tab strip: a vertical wheel scrolls it sideways ({down:0}px for two notches).");
    }

    /// <summary>
    /// Scenario <c>ux-switcher-filter-uia</c> (#367): after the switcher's filter narrows the
    /// list and is cleared again, every row on screen is named, through UI Automation, after
    /// the context it draws, and reports the bounds it is drawn at. The list recycles its
    /// containers, and a name taken once from a container's first content outlived it: the QA
    /// run's click at the position UI Automation gave for qa-limited opened qa-cut. The
    /// screenshot is the switcher back on the unfiltered list.
    /// </summary>
    internal static void SwitcherAfterFilter(Window window)
    {
        var vm = (MainWindowViewModel)window.DataContext!;
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SwitcherList");
        Settle();
        var unfiltered = SwitcherRowsMatch(window, list, "the unfiltered list");

        vm.Switcher.Query = "prod";
        Settle();
        var filtered = SwitcherRowsMatch(window, list, "the filter 'prod'");

        vm.Switcher.Query = "";
        Settle();
        SwitcherRowsMatch(window, list, "the filter cleared");
        Console.WriteLine($"Switcher automation: {unfiltered} rows, then {filtered} filtered, then {unfiltered} again, each named for and bounded where it is drawn.");
    }

    private static int SwitcherRowsMatch(Window window, ListBox list, string state)
    {
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.DataContext is ClusterSwitcherItemViewModel)
            .ToList();
        if (rows.Count < 2)
            throw new InvalidOperationException($"The switcher draws {rows.Count} rows after {state}.");

        // The row's name is the text it draws (AutomationNames.NameFromContentText): the group
        // heading on a group's first row, the context, its provenance line. So it must name this
        // row's context and no other context the list holds.
        var vm = (MainWindowViewModel)window.DataContext!;
        var everyName = vm.AvailableContexts.Select(c => c.Name)
            .Concat(vm.Tabs.Select(t => t.Context.Name))
            .Append(ClusterContext.Demo.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var item = (ClusterSwitcherItemViewModel)row.DataContext!;
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            var name = peer.GetName();
            var named = name.Split(", ").Where(everyName.Contains).ToList();
            if (named.Count != 1 || named[0] != item.Name)
                throw new InvalidOperationException($"After {state}, the switcher row drawn as {item.Name} is named '{name}' through UI Automation.");

            var drawn = row.TranslatePoint(default, window) is { } origin ? new Rect(origin, row.Bounds.Size) : default;
            var reported = peer.GetBoundingRectangle();
            if (Math.Abs(reported.X - drawn.X) > 1 || Math.Abs(reported.Y - drawn.Y) > 1
                || Math.Abs(reported.Width - drawn.Width) > 1 || Math.Abs(reported.Height - drawn.Height) > 1)
                throw new InvalidOperationException($"After {state}, the switcher row {item.Name} is drawn at {drawn} and UI Automation reports {reported}.");
        }

        return rows.Count;
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Settle();
    }

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
