using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Layout claims the narrow scenarios exist to show, asserted rather than left to someone
/// looking at the PNG: a control pushed off the right edge renders perfectly — it is just
/// not there — which is how ENG-32 went unnoticed across several trains.
/// </summary>
internal static class LayoutChecks
{
    /// <summary>ENG-32: search, the unhealthy chip and Refresh all end inside the window.</summary>
    internal static void ListHeaderFits(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        foreach (var name in new[] { "RowFilterBox", "UnhealthyToggle", "RefreshButton" })
        {
            var control = view.FindControl<Control>(name)
                ?? throw new InvalidOperationException($"No control named {name} in the list header.");
            var right = RightEdge(control, window);
            if (!control.IsEffectivelyVisible || right > window.Bounds.Width + 0.5)
                throw new InvalidOperationException(
                    $"{name} ends at x={right:0} in a {window.Bounds.Width:0}px window — pushed off the list header (ENG-32).");
        }

        Console.WriteLine($"List header fits at {window.Bounds.Width:0}px (search, unhealthy chip, Refresh).");
    }

    /// <summary>ENG-35: the palette keeps a 16px gutter each side when the window is narrower than it.</summary>
    /// <summary>
    /// Every visible one-line TextBox holds its line in the middle of the box: the space
    /// above the text line and below it differ by at most a pixel. The exec pane's shell box
    /// failed this (9 over, 7 under in a 32px box): it kept Fluent's padding, which is 6 over
    /// 5, and a MinHeight taller than its 11.5px line, and on a Mac its placeholder read as
    /// sitting high. The placeholder is laid out in the same place as the text presenter, so
    /// measuring the presenter covers both.
    /// </summary>
    internal static void OneLineTextBoxesCentred(Window window)
    {
        var checkedBoxes = 0;
        foreach (var box in window.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible && !b.AcceptsReturn))
        {
            if (box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault() is not { } line
                || line.TranslatePoint(new Point(0, 0), box) is not { } origin)
            {
                continue;
            }

            var above = origin.Y;
            var below = box.Bounds.Height - (origin.Y + line.Bounds.Height);
            if (Math.Abs(above - below) > 1.01)
                throw new InvalidOperationException(
                    $"The one-line box \"{box.PlaceholderText ?? box.Name}\" holds its text {above:0.#}px from the top and {below:0.#}px from the bottom of a {box.Bounds.Height:0.#}px box; it should be centred.");
            checkedBoxes++;
        }

        if (checkedBoxes == 0)
            throw new InvalidOperationException("No visible one-line TextBox to check; the scenario changed under the check.");
        Console.WriteLine($"One-line text boxes centred: {checkedBoxes}.");
    }

    internal static void PaletteFollowsWindow(Window window)
    {
        var box = window.FindControl<TextBox>("PaletteQueryBox")
            ?? throw new InvalidOperationException("No palette query box.");
        var panel = box.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("overlayCard"));
        var left = panel.TranslatePoint(default, window)?.X ?? double.NaN;
        var right = RightEdge(panel, window);
        if (left < 16 - 0.5 || right > window.Bounds.Width - 16 + 0.5)
            throw new InvalidOperationException(
                $"The palette spans x={left:0}..{right:0} in a {window.Bounds.Width:0}px window; it should keep a 16px gutter (ENG-35).");

        Console.WriteLine($"Palette follows the window ({panel.Bounds.Width:0}px in {window.Bounds.Width:0}px).");
    }

    /// <summary>
    /// ENG-6: when the list's columns are wider than the grid, the grid scrolls sideways —
    /// the rightmost column is reachable rather than cut off.
    /// </summary>
    internal static void GridReachesLastColumn(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ClusterTabView>().First();
        var grid = view.FindControl<DataGrid>("ResourceGrid")!;
        var columns = grid.Columns.Where(c => c.IsVisible).Sum(c => c.ActualWidth);
        var bar = grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var viewport = grid.Bounds.Width;
        Console.WriteLine(
            $"Grid columns {columns:0}px in a {viewport:0}px grid; horizontal bar visible={bar?.IsVisible}, max={bar?.Maximum:0}.");
        if (columns > viewport + 0.5 && (bar is null || !bar.IsVisible || bar.Maximum <= 0))
            throw new InvalidOperationException(
                $"The list's columns need {columns:0}px of a {viewport:0}px grid and it does not scroll sideways (ENG-6).");
    }

    /// <summary>
    /// A log pane scrolled to its end, which is where Follow keeps it, leaves its last line
    /// clear of the horizontal scroll bar. Fluent's scroll bars hide themselves and are drawn
    /// over the content rather than beside it, so the last line sat under the bar until the
    /// panes gave their content a bottom margin. Left scrolled to the end, so the PNG shows it.
    /// </summary>
    internal static void LogEndClearsScrollBar(Window window)
    {
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(s => s.Name == "LogScroll" && s.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("No visible log pane.");
        var items = scroll.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "LogItems");

        // A log shorter than its pane has no end to scroll to, and would pass without
        // having measured anything.
        if (scroll.Extent.Height <= scroll.Bounds.Height + 0.5)
            throw new InvalidOperationException(
                $"The log pane's {items.ItemCount} lines fit its {scroll.Bounds.Height:0}px, so there was nothing to scroll and nothing was checked.");

        // Twice: the list is virtualized, and the first pass can land on an estimated extent.
        for (var i = 0; i < 2; i++)
        {
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var last = items.ContainerFromIndex(items.ItemCount - 1)
            ?? throw new InvalidOperationException("The log's last line has no row after scrolling to the end.");
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)?.Y ?? double.PositiveInfinity;
        var bar = scroll.TryFindResource("ScrollBarSize", out var size) && size is double d ? d : 10;
        var gap = scroll.Bounds.Height - bottom;
        if (gap < bar - 0.5)
            throw new InvalidOperationException(
                $"Scrolled to its end, the log pane's last line ends {gap:0.#}px above the pane's bottom edge, under the {bar:0}px horizontal scroll bar.");

        Console.WriteLine($"Log pane's last line ends {gap:0}px above its bottom edge (scroll bar {bar:0}px).");
    }

    /// <summary>
    /// ENG-51: a log pane's bar keeps every one of its tools inside the pane at a narrow
    /// window, and its search box wide enough to type in. Below about 1150px the right-hand
    /// tools used to run off the pane's edge, the ⋯ menu (Clear, Save) first.
    /// </summary>
    internal static void LogBarFits(Window window)
    {
        var checkedBars = 0;
        foreach (var bar in window.GetVisualDescendants().OfType<StackPanel>()
                     .Where(p => p.Name == "LogBarTools" && p.IsEffectivelyVisible))
        {
            var pane = bar.GetVisualAncestors().OfType<UserControl>().First();
            var paneRight = RightEdge(pane, window);
            var limit = Math.Min(paneRight, window.Bounds.Width);
            var problems = new List<string>();
            foreach (var control in bar.Children.Where(c => c.IsVisible))
            {
                var right = RightEdge(control, window);
                if (right > limit + 0.5)
                    problems.Add($"{Describe(control)} ends at x={right:0}, past the pane's edge at {limit:0}");
            }

            var search = pane.FindControl<TextBox>("LogSearchBox")
                ?? throw new InvalidOperationException("No LogSearchBox in a log pane.");
            var searchRight = RightEdge(search, window);
            var barLeft = bar.TranslatePoint(default, window)?.X ?? double.NaN;
            if (search.Bounds.Width < MinLogSearchWidth - 0.5)
                problems.Add($"the search box is {search.Bounds.Width:0}px, narrower than the {MinLogSearchWidth}px it needs to be typed in");
            if (searchRight > barLeft + 0.5)
                problems.Add($"the search box ends at x={searchRight:0}, under the tools that start at x={barLeft:0}");

            // Whatever left the bar is in the ⋯ menu instead, and nothing is in both places.
            var moved = new List<string>();
            foreach (var (slotName, menuName) in new[] { ("RangeSlot", "MenuRange"), ("LevelsSlot", "MenuLevels"), ("ContextSlot", "MenuContext"), ("CopySlot", "MenuCopy") })
            {
                var slot = pane.FindControl<Control>(slotName) ?? throw new InvalidOperationException($"No {slotName}.");
                var menu = pane.FindControl<Control>(menuName) ?? throw new InvalidOperationException($"No {menuName}.");
                if (slot.IsVisible == menu.IsVisible)
                    problems.Add($"{slotName} and {menuName} are both {(slot.IsVisible ? "shown" : "hidden")}");
                if (!slot.IsVisible) moved.Add(slotName.Replace("Slot", "", StringComparison.Ordinal));
            }

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"{pane.GetType().Name}'s log bar does not fit a {window.Bounds.Width:0}px window (ENG-51): {string.Join("; ", problems)}.");

            Console.WriteLine(
                $"{pane.GetType().Name}'s log bar fits {window.Bounds.Width:0}px: search {search.Bounds.Width:0}px, tools end at {RightEdge(bar, window):0} of {limit:0}, "
                + $"in the menu: {(moved.Count == 0 ? "nothing" : string.Join(", ", moved))}.");
            checkedBars++;
        }

        if (checkedBars == 0)
            throw new InvalidOperationException("No visible log bar, so nothing was checked.");
    }

    /// <summary>Room for the placeholder and a few typed characters beside the box's own buttons.</summary>
    private const double MinLogSearchWidth = 160;

    private static string Describe(Control control) =>
        control.Name is { Length: > 0 } name ? name
        : ToolTip.GetTip(control) is string tip ? $"\"{tip}\""
        : control.GetType().Name;

    /// <summary>
    /// FEAT-77: the confirm strip reads as one element. The confirm button sits right after
    /// the question's text rather than at the far edge of the window, it ends inside the
    /// window, and a scale's replica box is the blank at the end of the sentence — just
    /// after it and level with it — not a field with a label of its own.
    /// </summary>
    internal static void ActionStripReadsAsOneBlock(Window window)
    {
        // A strip with a list under its sentence (a bulk delete's names) realizes the list
        // after the first pass and lays out again; measure what is drawn, not the pass before.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var strip = window.GetVisualDescendants().OfType<RowActionStrip>().First();
        var text = strip.FindControl<Control>("QuestionText")!;
        var sentence = strip.FindControl<TextBlock>("ActionSentence")!;
        var confirm = strip.FindControl<Button>("ConfirmButton")!;
        var box = strip.FindControl<NumericUpDown>("ReplicaInput") is { IsEffectivelyVisible: true } b ? b : null;

        // Where the ink ends, not where the controls' boxes end: a TextBlock stretches to its
        // column, so its bounds say nothing about whether the words reach the buttons.
        var textRight = text.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(InkRight)
            .Append(box is null ? 0 : RightEdge(box, window))
            .Max();
        var confirmLeft = confirm.TranslatePoint(default, window)?.X ?? double.NaN;
        var gap = confirmLeft - textRight;
        if (!confirm.IsEffectivelyVisible || gap is < 0 or > 32 || RightEdge(confirm, window) > window.Bounds.Width + 0.5)
            throw new InvalidOperationException(
                $"The strip's confirm starts {gap:0}px after its text (x={confirmLeft:0}, text ends at x={textRight:0}) "
                + $"in a {window.Bounds.Width:0}px window; it should sit with the sentence it answers (FEAT-77).");

        if (box is not null)
        {
            // The box is the sentence's blank: the word it completes ("to") sits just before it
            // on the same line, and the pair ends the sentence. The docked box this replaced
            // passed a looser check while "to" wrapped onto a line of its own under it.
            var lead = strip.FindControl<TextBlock>("BlankLead")!;
            var leadRight = (lead.TranslatePoint(default, window)?.X ?? double.NaN) + lead.TextLayout.Width;
            var leadMiddle = lead.TranslatePoint(new Point(0, lead.Bounds.Height / 2), window)?.Y ?? double.NaN;
            var boxLeft = box.TranslatePoint(default, window)?.X ?? double.NaN;
            var boxTop = box.TranslatePoint(default, window)?.Y ?? double.NaN;
            var boxGap = boxLeft - leadRight;
            var sentenceBottom = (sentence.TranslatePoint(default, window)?.Y ?? double.NaN) + sentence.Bounds.Height;
            var boxBottom = boxTop + box.Bounds.Height;
            if (!lead.IsEffectivelyVisible || lead.Text != "to" || boxGap is < 0 or > 16
                || leadMiddle < boxTop || leadMiddle > boxBottom || boxBottom > sentenceBottom + 0.5)
                throw new InvalidOperationException(
                    $"The replica box starts {boxGap:0}px after \"{lead.Text}\" (its middle at y={leadMiddle:0}, the box "
                    + $"spanning y={boxTop:0}..{boxBottom:0}, the sentence ending at y={sentenceBottom:0}); it should follow "
                    + "the word it completes on the same line, inside the sentence (FEAT-77).");

            // #293: "to" stands on the baseline of the number it introduces, not centred beside it.
            var number = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
            var numberBaseline = number.TranslatePoint(new Point(0, number.TextLayout.TextLines[0].Baseline), window)?.Y ?? double.NaN;
            var leadBaseline = lead.TranslatePoint(new Point(0, lead.Padding.Top + lead.TextLayout.TextLines[0].Baseline), window)?.Y ?? double.NaN;
            if (!(Math.Abs(numberBaseline - leadBaseline) <= 0.5))
                throw new InvalidOperationException(
                    $"\"to\" stands on y={leadBaseline:0.0} and the replica box's number on y={numberBaseline:0.0}; they should share a baseline (#293).");

            // 2026-10-21: and the sentence's own words stand on that baseline too. The row used
            // to stand on the line with its bottom, so "to" and the box floated above the words.
            var sentenceTop = sentence.TranslatePoint(new Point(0, sentence.Padding.Top), window)?.Y ?? double.NaN;
            var boxMiddle = boxTop + box.Bounds.Height / 2;
            var hangBelow = boxBottom - numberBaseline;
            double? sentenceBaseline = null;
            foreach (var textLine in sentence.TextLayout.TextLines)
            {
                if (boxMiddle >= sentenceTop && boxMiddle <= sentenceTop + textLine.Height + hangBelow)
                {
                    sentenceBaseline = sentenceTop + textLine.Baseline;
                    break;
                }

                sentenceTop += textLine.Height;
            }

            if (sentenceBaseline is not { } words || !(Math.Abs(words - numberBaseline) <= 0.75))
                throw new InvalidOperationException(
                    $"The sentence stands on y={sentenceBaseline:0.0} and the replica box's number on y={numberBaseline:0.0}; "
                    + "the words, \"to\" and the number should share one baseline.");
        }
        // 2026-10-21: the verb's glyph is centred on the first line's words, also when the scale
        // box shares that line and makes it as tall as the box.
        var glyph = strip.FindControl<PathIcon>("ActionGlyph")!;
        var firstLine = sentence.TextLayout.TextLines[0];
        var firstBaseline = sentence.TranslatePoint(new Point(0, sentence.Padding.Top + firstLine.Baseline), window)?.Y ?? double.NaN;
        var glyphMiddle = (glyph.TranslatePoint(default, window)?.Y ?? double.NaN) + glyph.Bounds.Height / 2;
        var capMiddle = firstBaseline - RowActionStrip.CapMiddle * sentence.FontSize;
        if (!(Math.Abs(glyphMiddle - capMiddle) <= 1))
            throw new InvalidOperationException(
                $"The strip's glyph is centred on y={glyphMiddle:0.0} and the first line's capitals on y={capMiddle:0.0}; it should sit level with the words.");

        Console.WriteLine($"Action strip reads as one block at {window.Bounds.Width:0}px (confirm {gap:0}px after the text).");

        double InkRight(TextBlock block) =>
            (block.TranslatePoint(default, window)?.X ?? double.NaN) + block.Padding.Left + block.TextLayout.Width;
    }

    /// <summary>
    /// 2026-10-21 (the owner, after an audit of the rendered app): the buttons and fields of one
    /// row are one height and share one centre line. Delete sat 10px shorter than Apply in the
    /// YAML toolbar, Sync's split button shorter than Restart…, and a filled button taller than
    /// the fields or the soft Cancel beside it, because each family had its own padding and the
    /// filled one had no border. The row is everything of those kinds inside
    /// <paramref name="anchor"/>'s parent panel; an icon-only toolbar button is not one of them.
    /// </summary>
    internal static TControl Find<TView, TControl>(Window window, Func<TControl, bool> match)
        where TView : Control where TControl : Control =>
        window.GetVisualDescendants().OfType<TView>().First(v => v.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<TControl>().First(c => c.IsEffectivelyVisible && match(c));

    internal static void RowIsLevel(Window window, Control anchor, string where)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var row = anchor.GetVisualParent() as Visual
            ?? throw new InvalidOperationException($"{where}: the anchor has no parent.");
        var members = row.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c is SplitButton or ComboBox or NumericUpDown or TextBox or Button
                && !c.Classes.Contains("toolbar") && !c.Classes.Contains("linkText") && !c.Classes.Contains("chip")
                && !c.GetVisualAncestors().TakeWhile(v => v != row).Any(v => v is SplitButton or ComboBox or NumericUpDown or TextBox))
            .Where(c => c is not Button b || b.Content is not null)
            .ToList();
        var measured = members.Select(c =>
        {
            var top = c.TranslatePoint(default, window)?.Y ?? double.NaN;
            return (Control: c, Height: c.Bounds.Height, Middle: top + c.Bounds.Height / 2);
        }).ToList();
        var text = string.Join(", ", measured.Select(m => $"{m.Control.GetType().Name} {Label(m.Control)} {m.Height:0.#}px at y={m.Middle:0.#}"));
        if (measured.Count < 2)
            throw new InvalidOperationException($"{where}: found {measured.Count} controls in the row ({text}); the check needs two.");
        var tallest = measured.Max(m => m.Height);
        var shortest = measured.Min(m => m.Height);
        var highest = measured.Min(m => m.Middle);
        var lowest = measured.Max(m => m.Middle);
        if (tallest - shortest > 0.5 || lowest - highest > 0.5)
            throw new InvalidOperationException($"{where}: the row's controls differ in height or centre: {text}.");
        Console.WriteLine($"{where}: {measured.Count} controls, all {tallest:0.#}px on one centre line.");

        static string Label(Control c) => c switch
        {
            ContentControl { Content: string s } => $"\"{s}\"",
            _ => c.Name ?? "",
        };
    }

    /// <summary>
    /// #269: the dock's tab strip is one row however many tabs are open — every tab at the
    /// same height, the strip no taller than one tab plus its margins — and bringing the last
    /// tab to the front scrolls it into the strip's view.
    /// </summary>
    internal static void DockTabsOneRow(Window window)
    {
        var strip = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "InspectorTabStrip");
        var vm = (ClusterTabViewModel)strip.DataContext!;
        var tabs = strip.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        if (tabs.Count != vm.InspectorTabs.Count || tabs.Count < 10)
            throw new InvalidOperationException($"The scenario should have ten dock tabs and the strip shows {tabs.Count}.");

        var tops = tabs.Select(t => Math.Round(t.TranslatePoint(default, strip)?.Y ?? double.NaN, 1)).Distinct().ToList();
        var tallest = tabs.Max(t => t.Bounds.Height + t.Margin.Top + t.Margin.Bottom);
        if (tops.Count != 1 || strip.Bounds.Height > tallest + 0.5)
            throw new InvalidOperationException(
                $"The dock's tab strip is {strip.Bounds.Height:0}px tall for {tabs.Count} tabs on {tops.Count} rows; it should be one row of {tallest:0}px (#269).");

        // The current harness renders at the first tab; the last one must be reachable too.
        vm.SelectedInspectorTab = vm.InspectorTabs[^1];
        Dispatcher.UIThread.RunJobs();
        var last = (ListBoxItem)strip.ContainerFromIndex(vm.InspectorTabs.Count - 1)!;
        var left = last.TranslatePoint(default, strip)?.X ?? double.NaN;
        if (left < 0 || left + last.Bounds.Width > strip.Bounds.Width + 0.5)
            throw new InvalidOperationException(
                $"The last dock tab, brought to the front, sits at x={left:0}..{left + last.Bounds.Width:0} outside the {strip.Bounds.Width:0}px strip (#269).");
        vm.SelectedInspectorTab = vm.InspectorTabs[0];
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"Dock tab strip: {tabs.Count} tabs on one {strip.Bounds.Height:0}px row; the last scrolls into view.");
    }

    /// <summary>
    /// FEAT-71: the cluster switcher draws a row's pin only where it is about to be used — the
    /// selected row (the pointer is nowhere in a headless render) — and always on a pinned
    /// row. A hidden pin is not hit-testable either, so a click on an empty-looking spot does
    /// not pin. FEAT-73: no row draws an environment dot beside its pill.
    /// </summary>
    internal static void SwitcherPinsOnlyWhereUsed(Window window)
    {
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SwitcherList");
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible).ToList();
        var problems = new List<string>();
        var shownUnpinned = 0;
        var hiddenUnpinned = 0;
        foreach (var row in rows)
        {
            var item = (ClusterSwitcherItemViewModel)row.DataContext!;
            var pin = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("switcherPin"));
            var expected = item.IsPinned || row.IsSelected;
            var shown = pin.Opacity > 0.5 && pin.IsHitTestVisible;
            if (shown != expected)
                problems.Add($"{item.Name}: pin {(shown ? "shown" : "hidden")} (pinned {item.IsPinned}, selected {row.IsSelected})");
            if (!shown && (pin.Opacity > 0 || pin.IsHitTestVisible))
                problems.Add($"{item.Name}: a hidden pin is still drawn or still takes clicks");
            if (!item.IsPinned) _ = shown ? shownUnpinned++ : hiddenUnpinned++;
            if (row.GetVisualDescendants().OfType<Ellipse>().Any(e => e.IsEffectivelyVisible))
                problems.Add($"{item.Name}: an environment dot beside the pill");
        }

        if (hiddenUnpinned == 0)
            problems.Add("no unpinned, unselected row was rendered, so nothing was checked");
        if (shownUnpinned == 0)
            problems.Add("no selected, unpinned row was rendered, so the pin that shows on selection was not checked (#287)");
        if (problems.Count > 0)
            throw new InvalidOperationException($"The switcher's pins (FEAT-71/73): {string.Join("; ", problems)}.");

        Console.WriteLine($"Switcher pins: {rows.Count} rows, {hiddenUnpinned} unpinned pins hidden, {shownUnpinned} shown on the selected row.");
    }

    /// <summary>FEAT-73: the exec pane states its connection in words, with no status dot beside them.</summary>
    internal static void ExecHasNoStatusDot(Window window)
    {
        var view = window.GetVisualDescendants().OfType<ExecView>().First();
        if (view.GetVisualDescendants().OfType<Ellipse>().Any(e => e.Classes.Contains("statusDot") && e.IsEffectivelyVisible))
            throw new InvalidOperationException("The exec pane draws a status dot beside its connection sentence (FEAT-73).");
        Console.WriteLine("Exec pane has no status dot beside its sentence.");
    }

    /// <summary>
    /// #215 / #237, DESIGN.md rule 23: inside the dock, a group is marked by a header or a
    /// border, never both; a pane's content (an editor, the terminal, a log) is never boxed
    /// again inside the dock's own card; and a dot-led list hangs its dots into the section's
    /// inset, so each row's text starts where the section header does. Put the `card` class
    /// back on a section, an editor's border or the log's, or drop `dotLed`, and this throws.
    /// </summary>
    internal static void OneGroupingDevice(Window window)
    {
        var dock = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DockRegion");
        bool BoxedInDock(Visual v) => v.GetVisualAncestors().TakeWhile(a => a != dock)
            .Any(a => a is Border { Classes: var classes } && classes.Contains("card"));

        var headers = dock.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("sectionHeader") && t.IsEffectivelyVisible).ToList();
        if (headers.FirstOrDefault(BoxedInDock) is { } boxed)
            throw new InvalidOperationException($"The {boxed.Text} section has a header and a card around it (DESIGN.md rule 23, #215).");

        var content = dock.GetVisualDescendants().Where(v => v is AvaloniaEdit.TextEditor
            || v is SvcSystems.UI.Terminal.TerminalControl
            || v is ItemsControl { Name: "LogItems" }).Where(v => v.IsEffectivelyVisible).ToList();
        if (content.FirstOrDefault(BoxedInDock) is { } inner)
            throw new InvalidOperationException($"A {inner.GetType().Name} sits in a card inside the dock's own (DESIGN.md rule 23, #215).");

        var rows = 0;
        // Every list in a section whose rows lead with a status dot, whatever its classes.
        var dotLists = dock.GetVisualDescendants().OfType<ItemsControl>()
            .Where(i => i.IsEffectivelyVisible && i.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("section"))
                        && i.GetRealizedContainers().Any(r => r.GetVisualDescendants().OfType<Ellipse>().Any(e => e.Classes.Contains("statusDot"))));
        foreach (var list in dotLists)
        {
            var header = list.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("section"))
                .GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("sectionHeader"));
            var headerX = header.TranslatePoint(default, window)!.Value.X;
            foreach (var row in list.GetRealizedContainers())
            {
                if (row.GetVisualDescendants().OfType<Ellipse>().FirstOrDefault(e => e.Classes.Contains("statusDot")) is not { } dot)
                    continue;
                var text = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
                var dotX = dot.TranslatePoint(default, window)!.Value.X;
                var textX = text.TranslatePoint(default, window)!.Value.X;
                if (Math.Abs(textX - headerX) > 0.5 || dotX >= headerX)
                    throw new InvalidOperationException(
                        $"\"{text.Text}\" starts at x={textX:0.0} under {header.Text} at x={headerX:0.0}, its dot at x={dotX:0.0}: a dot-led row should hang its dot left of the header and line its text up with it (#237).");
                // A dot laid out in the right place can still be clipped away: a hang put on the
                // row rather than the list sits outside the ItemsControl's bounds, which clip it.
                if (ClippedBy(dot) is { } clip)
                    throw new InvalidOperationException(
                        $"The dot of \"{text.Text}\" under {header.Text} is clipped by {clip.GetType().Name}: put `dotLed` on the list, not the row (#237).");
                rows++;
            }
        }

        Console.WriteLine($"One grouping device: {headers.Count} section headers unboxed, {content.Count} pane contents unboxed, {rows} dot-led rows aligned.");
    }

    /// <summary>The first ancestor that clips to its bounds and cuts into <paramref name="visual"/> sideways, if any.</summary>
    private static Visual? ClippedBy(Visual visual)
    {
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (!ancestor.ClipToBounds || visual.TranslatePoint(default, ancestor) is not { } origin)
                continue;
            // Horizontal only: a row scrolled below a ScrollViewer's fold is clipped vertically
            // on purpose, and a hang moves the dot sideways.
            if (origin.X < -0.5 || origin.X + visual.Bounds.Width > ancestor.Bounds.Width + 0.5)
                return ancestor;
        }

        return null;
    }

    private static double RightEdge(Visual control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width, 0), root)?.X ?? double.PositiveInfinity;
}
