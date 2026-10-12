using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
