using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace KubeNimbus.Screenshot;

/// <summary>
/// DESIGN.md rule 21: the pointer over every tooltip reaches the element that carries it.
/// A TextBlock or panel with no background draws nothing of its own (glyphs are not
/// hit-testable), so the pointer over its text lands on the row, card or header behind
/// it and the tooltip never opens. <c>Nimbus.Ui.Controls.ToolTipHitTesting</c>, installed
/// from <c>App.Initialize</c>, is the fix; this walks every scenario window and fails the
/// run on any tooltip the pointer passes through, so a view that bypasses it, or a
/// handler that stops being installed, goes red.
/// </summary>
internal static class TooltipChecks
{
    private static readonly List<string> Dead = [];
    private static int _probed;

    /// <summary>Hit-tests the middle of each visible, enabled tooltip-bearing element's text.</summary>
    internal static void Reach(Window window, string name)
    {
        foreach (var (element, point) in Probes(window))
        {
            // Covered by something unrelated (an overlay, a scrim, a sibling drawn on
            // top): not this defect, and not testable from here.
            var hit = window.InputHitTest(point) as Visual;
            if (hit is not null && !ReferenceEquals(hit, element) && !element.GetVisualAncestors().Contains(hit)
                && !hit.GetVisualAncestors().Contains(element))
            {
                continue;
            }

            _probed++;
            if (hit is null || !(ReferenceEquals(hit, element) || hit.GetVisualAncestors().Contains(element)))
            {
                Dead.Add($"{name}: {Describe(element)} (pointer reached {hit?.GetType().Name ?? "nothing"})");
            }
        }
    }

    /// <summary>Called once after every scenario has rendered.</summary>
    internal static void ThrowIfAnyDead(bool filtered)
    {
        Console.WriteLine($"Tooltips probed: {_probed}, dead: {Dead.Count}.");
        if (Dead.Count > 0)
        {
            throw new InvalidOperationException(
                "Tooltips the pointer cannot open (DESIGN.md rule 21):" + Environment.NewLine
                + string.Join(Environment.NewLine, Dead.Distinct()));
        }

        // Not vacuous: a full run has to find the tooltips it is checking.
        if (!filtered && _probed < 40)
        {
            throw new InvalidOperationException($"Only {_probed} tooltips were probed; the walk is not finding them.");
        }
    }

    private static IEnumerable<(Control Element, Point Point)> Probes(Window window)
    {
        foreach (var element in window.GetVisualDescendants().OfType<Control>().ToList())
        {
            // A disabled control shows no tooltip unless it opts in
            // (ToolTip.ShowOnDisabled), and the hit test skips it on purpose. A shape
            // is hit where it is filled, which is what it shows.
            if (ToolTip.GetTip(element) is null || !element.IsEffectivelyVisible || !element.IsEffectivelyEnabled
                || element.Bounds.Width < 1 || element.Bounds.Height < 1 || element is Shape)
            {
                continue;
            }

            Visual target = element;
            if (element is Panel)
            {
                target = element.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Width >= 1) ?? (Visual)element;
            }

            var middle = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
            if (target.TranslatePoint(middle, window) is not { } point || !IsOnScreen(element, point, window))
            {
                continue;
            }

            yield return (element, point);
        }
    }

    /// <summary>
    /// Whether the point is where the element can be seen and reached: inside the window,
    /// inside every ancestor that clips (a scroll viewport, a grid scrolled sideways, a
    /// narrow pane), and under nothing that has switched hit-testing off on purpose.
    /// </summary>
    private static bool IsOnScreen(Control element, Point point, Window window)
    {
        if (!new Rect(window.ClientSize).Contains(point))
        {
            return false;
        }

        foreach (var visual in element.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, window)))
        {
            if (visual is InputElement { IsHitTestVisible: false })
            {
                return false;
            }

            if (visual.ClipToBounds && visual.TranslatePoint(default, window) is { } origin
                && !new Rect(origin, visual.Bounds.Size).Contains(point))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(Control element)
    {
        var text = element as TextBlock is { } t ? t.Text
            : element.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
        return $"{element.GetType().Name}{(element.Name is { } n ? "#" + n : "")} \"{text}\" tip \"{ToolTip.GetTip(element)}\"";
    }
}
