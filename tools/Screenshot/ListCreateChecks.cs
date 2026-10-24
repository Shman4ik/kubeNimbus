using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Bundle L: the resource list's extended selection and bulk Delete (FEAT-6), its column
/// chooser (FEAT-13), and New YAML… (FEAT-12). The scenario builders are here rather than in
/// <see cref="ClusterTabScenarios"/> so the three features' harness half reads in one place;
/// the checks drive the real grid by pointer and key, as a person would.
/// </summary>
internal static class ListCreateChecks
{
    // ------------------------------------------------------------------ scenarios

    /// <summary>
    /// A bulk delete armed on the fixture's production cluster: the sentence names the count
    /// and the cluster, the names are listed under it, and the confirm is the red Delete.
    /// </summary>
    public static ClusterTabViewModel BulkDeleteArmed() => BulkTab(partial: false);

    /// <summary>
    /// The same run after the server refused one of them (narrow RBAC): the others deleted,
    /// the refused one with the API server's own sentence beside it, and a result line that
    /// counts both rather than a silent partial.
    /// </summary>
    public static ClusterTabViewModel BulkDeletePartial() => BulkTab(partial: true);

    private static ClusterTabViewModel BulkTab(bool partial)
    {
        var tab = ClusterTabScenarios.RowActionDeleteProduction();
        var rows = tab.Rows.Where(r => r.Namespace == tab.SelectedRow!.Namespace).Take(4).ToList();
        var client = FixtureData.CreateOfflineClient();
        var bulk = rows.Select(r => new BulkTarget(client, tab.SelectedKind!.Descriptor, r.Namespace, r.Name, tab.Context.Name)).ToList();
        var action = new RowActionViewModel(
            RowActionKind.Delete, client, tab.SelectedKind!.Descriptor, rows[0].Namespace, rows[0].Name, tab.Context.Name,
            environment: ClusterEnvironments.Classify(tab.Context.Name, tab.Context.ClusterName), bulk: bulk);
        tab.SetSelectedRows(rows);
        tab.PendingRowAction = action;

        if (partial)
        {
            for (var i = 0; i < action.BulkItems.Count; i++)
            {
                if (i == 2)
                {
                    action.BulkItems[i].MarkRefused(
                        $"pods \"{action.BulkItems[i].Target.Name}\" is forbidden: User \"system:serviceaccount:payments:deployer\" "
                        + "cannot delete resource \"pods\" in API group \"\" in the namespace \"payments\"");
                }
                else
                {
                    action.BulkItems[i].MarkDeleted();
                }
            }

            action.Message = RowActionViewModel.BulkSummary(3, 1, 4, action.TargetName, stopped: false);
            action.IsError = true;
            action.IsDone = true;
        }

        return tab;
    }

    /// <summary>New YAML…: an empty editor in the dock, with the InfoBar saying where it applies.</summary>
    public static ClusterTabViewModel NewYaml()
    {
        var tab = ClusterTabScenarios.YamlEditor();
        tab.InspectorTabs.Clear();
        var editor = new YamlEditorTabViewModel(
            FixtureData.CreateOfflineClient(),
            (_, _, _) => Task.FromResult<ResourceDescriptor?>(null),
            "payments",
            1)
        {
            IsPreview = false,
        };
        editor.SetCluster(tab.Context.Name, ClusterEnvironments.Classify(tab.Context.Name, tab.Context.ClusterName));
        tab.InspectorTabs.Add(editor);
        tab.SelectedInspectorTab = editor;
        return tab;
    }

    // --------------------------------------------------------------------- checks

    /// <summary>
    /// Selects three rows by pointer (click, then Shift+click), right-clicks inside the
    /// selection — which must keep it, so the menu's Delete is about all three — and presses
    /// Delete: one strip, armed for the three. The demo cluster then shows its notice in place.
    /// </summary>
    internal static void BulkDelete(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        Settle(window);

        var rows = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsEffectivelyVisible).OrderBy(r => r.TranslatePoint(default, grid)!.Value.Y).Take(3).ToList();
        if (rows.Count < 3) throw new InvalidOperationException("The demo list has fewer than three rows on screen.");

        ClickRow(window, rows[0], MouseButton.Left, RawModifiers.None);
        ClickRow(window, rows[2], MouseButton.Left, RawModifiers.Shift);
        if (vm.SelectedRows.Count != 3)
            throw new InvalidOperationException($"Click then Shift+click selected {vm.SelectedRows.Count} rows, not 3.");
        if (vm.SelectionSummary != "3 selected")
            throw new InvalidOperationException($"The selection count reads \"{vm.SelectionSummary}\".");

        // #406: opened on the Cmd scheme, so the Delete item's gesture is checked against the
        // Mac's ⌘⌫ — the XAML used to hard-code the Delete key, which a Mac keyboard lacks.
        var scheme = Nimbus.Ui.Hotkeys.Primary == KeyModifiers.Meta ? "mac" : "windows";
        Nimbus.Ui.Hotkeys.Initialize("mac");
        try
        {
            ClickRow(window, rows[1], MouseButton.Right, RawModifiers.None);
            if (grid.ContextFlyout is MenuFlyout { IsOpen: true } macMenu)
            {
                var delete = macMenu.Items.OfType<MenuItem>().First(i => i.Header as string == "Delete…");
                if (delete.InputGesture is not { Key: Key.Back, KeyModifiers: KeyModifiers.Meta })
                    throw new InvalidOperationException($"On the Cmd scheme the row menu's Delete shows \"{delete.InputGesture}\", not ⌘⌫.");
                macMenu.Hide();
                Settle(window);
            }
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize(scheme);
        }

        ClickRow(window, rows[1], MouseButton.Right, RawModifiers.None);
        if (vm.SelectedRows.Count != 3)
            throw new InvalidOperationException(
                $"A right click inside the selection collapsed it to {vm.SelectedRows.Count} row(s); its menu's Delete would act on one.");
        if (grid.ContextFlyout is MenuFlyout { IsOpen: true } menu)
        {
            // #305: with three rows selected, an item that acts on one object is disabled and
            // says why; Delete, which acts on all three, is the one left enabled.
            var items = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
            var edit = items.First(i => i.Header as string == "Edit YAML");
            if (edit.IsEffectivelyEnabled || vm.EditSelectedYamlCommand.CanExecute(null))
                throw new InvalidOperationException("Edit YAML is enabled with three rows selected; it would edit one of them.");
            if (ToolTip.GetTip(edit) is not string tip || !tip.Contains("3 rows are selected", StringComparison.Ordinal))
                throw new InvalidOperationException($"Edit YAML's tooltip does not say why it is disabled: \"{ToolTip.GetTip(edit)}\".");
            // #198: so is the hand-off of kubectl exec to the machine's terminal.
            var handOff = items.FirstOrDefault(i => i.Header as string == "Exec in my terminal")
                ?? throw new InvalidOperationException("The row menu has no \"Exec in my terminal\".");
            if (handOff.IsEffectivelyEnabled || vm.ExecInMyTerminalCommand.CanExecute(null))
                throw new InvalidOperationException("Exec in my terminal is enabled with three rows selected; it would exec into one of them.");
            if (!items.Any(i => i.Header as string == "Delete…") || !vm.DeleteSelectedCommand.CanExecute(null))
                throw new InvalidOperationException("Delete is missing or disabled with three rows selected.");
            if (!Equals(items.First(i => i.Header as string == "Delete…").InputGesture, KubeNimbus.App.CommandBindings.GestureFor(KubeNimbus.Core.Commands.CommandId.DeleteResource)))
                throw new InvalidOperationException("The row menu's Delete does not show the catalog's chord for the live scheme.");
            menu.Hide();
        }
        else
        {
            throw new InvalidOperationException("A right click inside the selection did not open the row menu.");
        }

        Settle(window);
        grid.Focus();
        window.KeyPress(Key.Delete, RawModifiers.None, PhysicalKey.Delete, null);
        Settle(window);
        if (vm.PendingRowAction is not { IsBulk: true, TargetCount: 3 } action)
            throw new InvalidOperationException("Delete with three rows selected did not arm one strip for all three.");
        if (!action.Headline.StartsWith("Delete 3 ", StringComparison.Ordinal) || !action.Headline.Contains(" on ", StringComparison.Ordinal))
            throw new InvalidOperationException($"The bulk strip reads \"{action.Headline}\" — it must name the count and the cluster.");
        if (action.ConfirmCommand.CanExecute(null))
            throw new InvalidOperationException("The demo cluster's bulk Delete is enabled; there is no API server to delete on.");
        Console.WriteLine($"Bulk delete passed: \"{action.Headline}\".");
    }

    /// <summary>
    /// #305: the refused row of a bulk delete shows the API server's whole sentence at the
    /// harness's 1280px — wrapped, never cut with an ellipsis before the verb and resource.
    /// </summary>
    internal static void BulkRefusalWraps(Window window)
    {
        Settle(window);
        var detail = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "BulkItemDetail" && t.Text?.Contains("forbidden", StringComparison.Ordinal) == true)
            ?? throw new InvalidOperationException("The bulk strip shows no refusal.");
        if (detail.TextLayout.TextLines.Any(line => line.HasCollapsed))
            throw new InvalidOperationException("The refusal is cut with an ellipsis; it must wrap.");
        if (detail.TextLayout.TextLines.Count < 2)
            throw new InvalidOperationException("The refusal fits one line at 1280px, so this check no longer proves it wraps.");
        Console.WriteLine($"Bulk refusal passed: {detail.TextLayout.TextLines.Count} lines, none cut.");
    }

    /// <summary>
    /// #301 (FEAT-6): three selected rows survive both ways a re-sort rewrites the list. A Reset
    /// (a sort click, or a poll that reshuffles a usage-sorted list) keeps them on its own in
    /// Avalonia 12's DataGrid, and this pins that. A poll that moves a few rows is a Remove and an
    /// Insert per row, and the grid kept one of the three until the view model handed the rest
    /// back (<c>MultipleSelectionRestored</c>); with that handler emptied this check failed with
    /// "the grid has 1 row(s) selected".
    /// </summary>
    internal static void SelectionKeptThroughResort(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        Settle(window);

        var rows = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsEffectivelyVisible).OrderBy(r => r.TranslatePoint(default, grid)!.Value.Y).Take(3).ToList();
        if (rows.Count < 3) throw new InvalidOperationException("The demo list has fewer than three rows on screen.");

        ClickRow(window, rows[0], MouseButton.Left, RawModifiers.None);
        ClickRow(window, rows[2], MouseButton.Left, RawModifiers.Shift);
        var selected = vm.SelectedRows.ToHashSet();
        if (selected.Count != 3)
            throw new InvalidOperationException($"Click then Shift+click selected {selected.Count} rows, not 3.");

        // Sorting by name descending replaces the list with one Reset, as a metrics poll on a
        // list sorted by CPU does past InPlaceResortLimit; the grid keeps the selection itself.
        vm.ToggleSort(ResourceColumn.Name);
        vm.ToggleSort(ResourceColumn.Name);
        Settle(window);

        var inGrid = grid.SelectedItems.OfType<ResourceRowViewModel>().ToHashSet();
        if (!inGrid.SetEquals(selected))
            throw new InvalidOperationException($"After the re-sort the grid has {inGrid.Count} row(s) selected, not the same 3.");
        if (!vm.SelectedRows.ToHashSet().SetEquals(selected) || vm.SelectionSummary != "3 selected")
            throw new InvalidOperationException($"After the re-sort the view model has {vm.SelectedRows.Count} row(s) selected (\"{vm.SelectionSummary}\").");

        // The metrics poll's other path: a few rows out of place are moved one by one (a Remove
        // and an Insert each), and a DataGrid drops the selection of a row removed under it.
        vm.ToggleSort(ResourceColumn.Cpu);
        Settle(window);
        var cpuSelection = vm.VisibleRows.Where(r => r.LatestCpuNanocores is > 0).Take(3).ToList();
        if (cpuSelection.Count < 3) throw new InvalidOperationException("The demo list has fewer than three measured rows.");
        grid.SelectedItems.Clear();
        foreach (var row in cpuSelection) grid.SelectedItems.Add(row);
        Settle(window);
        cpuSelection[0].ApplyUsage(cpuSelection[0].LatestCpuNanocores!.Value * 1000, 1);
        vm.ResortVisibleRows();
        Settle(window);
        inGrid = grid.SelectedItems.OfType<ResourceRowViewModel>().ToHashSet();
        if (!inGrid.SetEquals(cpuSelection))
            throw new InvalidOperationException($"After a usage re-sort that moved a selected row, the grid has {inGrid.Count} row(s) selected, not the same 3.");
        Console.WriteLine("Selection through a re-sort passed: 3 rows selected before and after, through a Reset and a move.");
    }

    /// <summary>
    /// #301 (FEAT-6): a rollout restart of four selected Deployments, armed on the bulk strip —
    /// the count and the cluster in the sentence, the names under it, and the question asked.
    /// </summary>
    public static ClusterTabViewModel BulkRestartArmed() => BulkRestartTab(partial: false);

    /// <summary>The same after a run where one restart was refused by RBAC: every outcome on its own row.</summary>
    public static ClusterTabViewModel BulkRestartPartial() => BulkRestartTab(partial: true);

    private static ClusterTabViewModel BulkRestartTab(bool partial)
    {
        var tab = ClusterTabScenarios.RowActionRestart();
        var rows = tab.Rows.Where(r => r.Namespace == tab.SelectedRow!.Namespace).Take(4).ToList();
        if (rows.Count < 2) throw new InvalidOperationException("The fixture has fewer than two Deployments in one namespace.");
        var client = FixtureData.CreateOfflineClient();
        var bulk = rows.Select(r => new BulkTarget(client, tab.SelectedKind!.Descriptor, r.Namespace, r.Name, tab.Context.Name)).ToList();
        var action = new RowActionViewModel(
            RowActionKind.Restart, client, tab.SelectedKind!.Descriptor, rows[0].Namespace, rows[0].Name, tab.Context.Name,
            environment: ClusterEnvironments.Classify(tab.Context.Name, tab.Context.ClusterName), bulk: bulk);
        tab.SetSelectedRows(rows);
        tab.PendingRowAction = action;

        if (partial)
        {
            for (var i = 0; i < action.BulkItems.Count; i++)
            {
                if (i == 1)
                {
                    action.BulkItems[i].MarkRefused(
                        $"deployments.apps \"{action.BulkItems[i].Target.Name}\" is forbidden: User \"deploy-bot\" "
                        + "cannot patch resource \"deployments\" in API group \"apps\" in the namespace \"payments\"");
                }
                else
                {
                    action.BulkItems[i].MarkDone("Restart requested, stamped 2026-08-01 10:00:00Z");
                }
            }

            action.Message = RowActionViewModel.BulkRestartSummary(
                action.BulkItems.Count - 1, 1, action.BulkItems.Count, action.TargetName, stopped: false);
            action.IsError = true;
            action.IsDone = true;
        }

        return tab;
    }

    /// <summary>
    /// #301 (FEAT-12): Open a YAML file… on a three-document file — the editor holds the first
    /// document, the tab is titled with the file name, and the InfoBar says the other two were
    /// neither opened nor applied.
    /// </summary>
    public static ClusterTabViewModel YamlFromFile()
    {
        var tab = NewYaml();
        var editor = (YamlEditorTabViewModel)tab.SelectedInspectorTab!;
        editor.LoadFromFile("checkout-stack.yaml", """
            # checkout: the worker's settings, its Service and its Deployment
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: checkout-settings
            data:
              mode: fast
              retries: "3"
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: checkout
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: checkout-worker
            """);
        return tab;
    }

    /// <summary>
    /// The follow-up to #405: a saved tab whose context this launch did not find, named in the
    /// status bar. The fixture has no workspace file to restore from, so the state is put in.
    /// </summary>
    internal static Control WithUnrestoredTab(Control host)
    {
        var shell = (MainWindowViewModel)((Window)host).DataContext!;
        shell.AddUnrestoredTab(new KubeNimbus.App.TabSnapshot("sandbox", "/home/dev/.kube/sandbox.yaml"));
        return host;
    }

    /// <summary>The line is on screen, whole, and its tooltip names the kubeconfig the tab was saved from.</summary>
    internal static void UnrestoredTabNotice(Window window)
    {
        Settle(window);
        var line = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "UnrestoredTabsNotice")
            ?? throw new InvalidOperationException("The status bar has no unrestored-tabs line.");
        if (!line.IsEffectivelyVisible)
            throw new InvalidOperationException("The unrestored-tabs line is not visible; the status bar may be hidden.");
        if (line.Text != "1 saved tab not reopened: sandbox is not in the kubeconfig files read")
            throw new InvalidOperationException($"The unrestored-tabs line reads \"{line.Text}\".");
        if (line.TextLayout.TextLines.Any(l => l.HasCollapsed))
            throw new InvalidOperationException("The unrestored-tabs line is cut with an ellipsis.");
        if (ToolTip.GetTip(line) is not string tip || !tip.Contains("/home/dev/.kube/sandbox.yaml", StringComparison.Ordinal))
            throw new InvalidOperationException($"The line's tooltip does not name the kubeconfig: \"{ToolTip.GetTip(line)}\".");
        Console.WriteLine("Unrestored-tab line passed.");
    }

    /// <summary>
    /// A right click on a column header opens the column chooser, not the row menu; unticking
    /// Restarts hides that column and remembers it; the chooser is left open for the PNG.
    /// </summary>
    internal static void ColumnChooser(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        Settle(window);

        var menu = OpenChooserOn(window, grid, "Restarts");
        if (grid.ContextFlyout is { IsOpen: true })
            throw new InvalidOperationException("A right click on a header opened the row menu as well as the chooser.");
        var restarts = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Restarts")
            ?? throw new InvalidOperationException("The chooser does not list Restarts.");
        var name = menu.Items.OfType<MenuItem>().First(i => i.Header as string == "Name");
        if (name.IsEnabled || name.IsChecked != true)
            throw new InvalidOperationException("Name can be unticked; the identity column must stay.");
        if (restarts.IsChecked != true) throw new InvalidOperationException("Restarts starts unticked.");

        restarts.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
        Settle(window);
        var column = grid.Columns.First(c => c.Tag as string == ResourceColumn.Restarts);
        if (column.IsVisible || !vm.HiddenColumns.Contains(ResourceColumn.Restarts))
            throw new InvalidOperationException("Unticking Restarts did not hide the column.");

        // Open again for the screenshot: Restarts unticked, Reset columns enabled.
        menu = OpenChooserOn(window, grid, "Ready");
        if (menu.Items.OfType<MenuItem>().First(i => i.Header as string == "Restarts").IsChecked != false)
            throw new InvalidOperationException("The chooser does not show Restarts as hidden.");
        Console.WriteLine("Column chooser passed: header right click opens it, unticking hides and remembers.");
    }

    /// <summary>
    /// #301: the column chooser by keyboard. The palette's "Choose the list's columns…" opens it
    /// with focus on the first column that can be hidden, drawn with a focus visual; Down moves
    /// to the next, Enter hides that column and closes the menu, and focus comes back to the
    /// list; opened again, Esc closes it and focus comes back to the list too (UI rules 7b, 16b).
    /// </summary>
    internal static void ColumnChooserKeyboard(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var vm = (ClusterTabViewModel)view.DataContext!;
        var shell = (MainWindowViewModel)window.DataContext!;
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        Settle(window);

        MenuFlyout OpenFromPalette()
        {
            shell.Palette.Open();
            shell.Palette.Query = "columns";
            var row = shell.Palette.FilteredItems.FirstOrDefault(i => i.Title == "Choose the list's columns…")
                ?? throw new InvalidOperationException("The palette does not offer the column chooser.");
            shell.Palette.SelectedItem = row;
            shell.Palette.ExecuteSelected();
            Settle(window);
            Settle(window);
            var opened = view.OpenColumnChooser;
            if (opened is not { IsOpen: true })
                throw new InvalidOperationException("The palette's column chooser row did not open the chooser.");
            return opened;
        }

        void Press(Key key, PhysicalKey physical, string? symbol)
        {
            window.KeyPress(key, RawInputModifiers.None, physical, symbol);
            window.KeyRelease(key, RawInputModifiers.None, physical, symbol);
            Settle(window);
        }

        void FocusIsBackOnTheList(string after)
        {
            if (focus.GetFocusedElement() is not Visual focused || (focused != grid && !grid.IsVisualAncestorOf(focused)))
                throw new InvalidOperationException(
                    $"After {after} focus was on {focus.GetFocusedElement()?.GetType().Name ?? "nothing"}, not back on the list.");
        }

        var menu = OpenFromPalette();
        var items = menu.Items.OfType<MenuItem>().ToList();
        var first = items.First(i => i.IsEnabled);
        if (focus.GetFocusedElement() != first)
            throw new InvalidOperationException(
                $"The chooser opened with focus on {focus.GetFocusedElement()?.GetType().Name ?? "nothing"}, not on {first.Header}.");
        // A menu shows the item the keys are on as its selected item, the same highlight the
        // pointer draws; that is this control's focus visual (UI rule 7b).
        var second = items.Where(i => i.IsEnabled).Skip(1).First();
        if (!ShowsAsCurrent(first, second))
            throw new InvalidOperationException($"{first.Header} has focus with no highlight to show it (UI rule 7b).");

        Press(Key.Down, PhysicalKey.ArrowDown, null);
        if (focus.GetFocusedElement() != second || !ShowsAsCurrent(second, first))
            throw new InvalidOperationException($"Down in the chooser did not move focus and its highlight to {second.Header}.");
        if (second.Tag is not string secondId)
            throw new InvalidOperationException($"{second.Header} carries no column id.");

        Press(Key.Enter, PhysicalKey.Enter, "\r");
        if (!vm.HiddenColumns.Contains(secondId))
            throw new InvalidOperationException($"Enter on {second.Header} did not hide that column.");
        if (menu.IsOpen)
            throw new InvalidOperationException("The chooser stayed open after Enter.");
        FocusIsBackOnTheList("Enter in the chooser");

        menu = OpenFromPalette();
        Press(Key.Escape, PhysicalKey.Escape, null);
        if (menu.IsOpen)
            throw new InvalidOperationException("Esc did not close the chooser.");
        FocusIsBackOnTheList("Esc in the chooser");

        // Back to the kind's own columns. The PNG is the list with focus given back to it: a
        // headless capture draws the window, not the chooser's popup.
        vm.ResetColumns();
        Settle(window);
        Console.WriteLine($"Column chooser by keyboard passed: palette opens it on {first.Header}, Down, Enter hid {second.Header}, Esc closes, focus returns.");
    }

    /// <summary>
    /// The item is the menu's selected one and its row is painted differently from
    /// <paramref name="other"/>'s, which is not: the highlight is drawn, not only flagged.
    /// </summary>
    private static bool ShowsAsCurrent(MenuItem item, MenuItem other)
    {
        static IBrush? Fill(MenuItem i) => i.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_LayoutRoot")?.Background;
        return item.IsSelected && !other.IsSelected && Fill(item) is ISolidColorBrush mine
               && (Fill(other) is not ISolidColorBrush theirs || theirs.Color != mine.Color);
    }

    private static MenuFlyout OpenChooserOn(Window window, DataGrid grid, string headerText)
    {
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .FirstOrDefault(h => h.IsEffectivelyVisible && (h.Content as string)?.StartsWith(headerText, StringComparison.Ordinal) == true)
            ?? throw new InvalidOperationException($"No visible {headerText} header.");
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Settle(window);

        return window.GetVisualDescendants().OfType<ClusterTabView>().First().OpenColumnChooser
            ?? throw new InvalidOperationException($"A right click on the {headerText} header did not open the column chooser.");
    }

    private static class RawModifiers
    {
        public const RawInputModifiers None = RawInputModifiers.None;
        public const RawInputModifiers Shift = RawInputModifiers.Shift;
    }

    private static void ClickRow(Window window, DataGridRow row, MouseButton button, RawInputModifiers modifiers)
    {
        // On the Name cell's text, away from the row's logs icon at the cell's right end.
        var point = row.TranslatePoint(new Point(row.Bounds.Width * 0.3, row.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("A row is not in the window.");
        window.MouseDown(point, button, modifiers);
        window.MouseUp(point, button, modifiers);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
