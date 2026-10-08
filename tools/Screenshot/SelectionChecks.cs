using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace KubeNimbus.Screenshot;

/// <summary>
/// DESIGN.md rule 20 gives a selected row two faces: the solid accent with white text in the
/// list that holds keyboard focus, and ordinary text on a lighter fill everywhere else. The
/// shared rule sets both the fill and the text colour on the item's template part, so a list
/// that repaints only the fill (a wash, or a transparent part under a row body of its own)
/// keeps the white text on a fill that is nearly white in the light theme. The Applications
/// list, the application page's pods, every segmented tab strip and the cluster switcher all
/// did that from the day the rule arrived, and nothing failed: a PNG only shows it when the
/// list happens to hold focus at capture time.
/// </summary>
internal static class SelectionChecks
{
    private static readonly List<string> Failures = [];
    private static int _probed;

    /// <summary>Every selected row in the window: white text only on an opaque fill.</summary>
    internal static void Walk(Window window, string name)
    {
        foreach (var item in window.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (!item.IsSelected || !item.IsEffectivelyVisible)
            {
                continue;
            }

            _probed++;
            if (Problem(item) is { } problem)
            {
                Failures.Add($"{name}: {problem}");
            }
        }
    }

    /// <summary>
    /// For a check that has just put focus in <paramref name="list"/>: the list has to hold
    /// focus (or this proved nothing) and its selected row has to be readable.
    /// </summary>
    internal static void RequireFocused(ListBox list, string where)
    {
        if (!list.IsKeyboardFocusWithin)
        {
            throw new InvalidOperationException($"{where} did not take keyboard focus, so its focused selection was not checked.");
        }

        var item = list.ContainerFromIndex(list.SelectedIndex) as ListBoxItem
            ?? throw new InvalidOperationException($"{where} has no selected row to check.");
        if (Problem(item) is { } problem)
        {
            throw new InvalidOperationException($"{where}: {problem}");
        }
    }

    internal static void ThrowIfAnyFailed(bool filtered)
    {
        Console.WriteLine($"Selected rows checked: {_probed}, failures: {Failures.Count}.");
        if (Failures.Count > 0)
        {
            throw new InvalidOperationException(
                "White text on a selection fill that is not the solid accent (DESIGN.md rule 20):" + Environment.NewLine
                + string.Join(Environment.NewLine, Failures.Distinct()));
        }

        if (!filtered && _probed < 20)
        {
            throw new InvalidOperationException($"Only {_probed} selected rows were checked; the walk is not finding them.");
        }
    }

    private static string? Problem(ListBoxItem item)
    {
        var presenter = item.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, item));
        // In the dark theme ordinary text is white too, and white on the wash is what it should be.
        if (presenter is null || !IsWhite(presenter.Foreground) || IsOpaque(presenter.Background)
            || (item.TryFindResource("SystemControlForegroundBaseHighBrush", item.ActualThemeVariant, out var ordinary)
                && IsWhite(ordinary as IBrush)))
        {
            return null;
        }

        var list = item.FindAncestorOfType<ListBox>();
        var listName = list?.Name is { Length: > 0 } n ? n : string.Join('.', list?.Classes.Where(c => !c.StartsWith(':')) ?? []);
        return $"ListBox {listName} draws its selected row's text white on {Describe(presenter.Background)}";
    }

    private static bool IsWhite(IBrush? brush) =>
        brush is ISolidColorBrush { Color: { R: 255, G: 255, B: 255 } };

    private static bool IsOpaque(IBrush? brush) =>
        brush is ISolidColorBrush { Color.A: 255, Opacity: >= 1 };

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "no fill",
        ISolidColorBrush solid => solid.Color.A == 0 ? "a transparent fill" : $"{solid.Color} at opacity {solid.Opacity}",
        _ => brush.GetType().Name,
    };
}
