using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KubeNimbus.App.Controls;

/// <summary>
/// One log line, with every occurrence of the log search's query highlighted in place —
/// the "find" half of the log panes' search, where the lines around a match stay visible.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SelectableTextBlock"/> that paints the matches' boxes <em>under</em> its
/// own text, rather than one that builds <c>Inlines</c> of coloured <c>Run</c>s. The text
/// stays a single bound string, so selection, Ctrl+C and the severity classes
/// (<c>.logError</c>/<c>.logWarn</c>/<c>.logInfo</c>, which set <c>Foreground</c> on the
/// block) behave exactly as they did; the only thing added is a few rectangles taken from
/// the block's own <see cref="TextBlock.TextLayout"/>, so they follow wrapping and the
/// font with no second measurement of their own. Same argument as <c>Sparkline</c>: a few
/// dozen lines of <see cref="DrawingContext"/> beat a component with a model of its own.
/// </para>
/// <para>
/// <see cref="MatchStart"/> is where the message begins inside the displayed text. Search
/// matches the message and never the timestamp prefix, so the highlight skips it — or a
/// search for "08:41" would light up text the pane's match count does not include.
/// </para>
/// <para>
/// Styled as a <see cref="SelectableTextBlock"/> (<see cref="StyleKeyOverride"/>), so the
/// theme and the severity class selectors written for that type still apply.
/// </para>
/// </remarks>
public sealed class LogLineText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<LogLineText, string?>(nameof(Query));

    public static readonly StyledProperty<int> MatchStartProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(MatchStart));

    public static readonly StyledProperty<bool> IsCurrentMatchProperty =
        AvaloniaProperty.Register<LogLineText, bool>(nameof(IsCurrentMatch));

    public static readonly StyledProperty<IBrush?> MatchBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(MatchBrush));

    public static readonly StyledProperty<IBrush?> CurrentMatchBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(CurrentMatchBrush));

    static LogLineText() =>
        AffectsRender<LogLineText>(
            QueryProperty, MatchStartProperty, IsCurrentMatchProperty, MatchBrushProperty, CurrentMatchBrushProperty);

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The text to highlight, case-insensitively. Empty or null highlights nothing.</summary>
    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    /// <summary>Index into <see cref="TextBlock.Text"/> where matching starts (the message, past any timestamp).</summary>
    public int MatchStart
    {
        get => GetValue(MatchStartProperty);
        set => SetValue(MatchStartProperty, value);
    }

    /// <summary>Whether this line is the search's current match — painted in <see cref="CurrentMatchBrush"/>.</summary>
    public bool IsCurrentMatch
    {
        get => GetValue(IsCurrentMatchProperty);
        set => SetValue(IsCurrentMatchProperty, value);
    }

    public IBrush? MatchBrush
    {
        get => GetValue(MatchBrushProperty);
        set => SetValue(MatchBrushProperty, value);
    }

    public IBrush? CurrentMatchBrush
    {
        get => GetValue(CurrentMatchBrushProperty);
        set => SetValue(CurrentMatchBrushProperty, value);
    }

    /// <summary>
    /// Every [start, length) at which <paramref name="query"/> occurs in
    /// <paramref name="text"/> at or after <paramref name="from"/>, case-insensitively and
    /// without overlaps. Public and static so the tests pin the ranges the boxes are drawn
    /// over without a rendered control.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Matches(string? text, string? query, int from)
    {
        var ranges = new List<(int, int)>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return ranges;
        }

        var index = Math.Clamp(from, 0, text.Length);
        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            ranges.Add((found, query.Length));
            index = found + query.Length;
        }

        return ranges;
    }

    /// <summary>
    /// Paints the match boxes, then lets the block draw its selection and text over them.
    /// <see cref="TextBlock.Render"/> is sealed; this is the hook it calls with the text's
    /// own origin, which is exactly the frame the layout's hit-test rectangles are in — so
    /// padding and alignment cannot push the boxes off the glyphs.
    /// </summary>
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        var brush = IsCurrentMatch ? CurrentMatchBrush ?? MatchBrush : MatchBrush;
        var ranges = brush is null ? [] : Matches(Text, Query, MatchStart);
        if (ranges.Count > 0)
        {
            using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
            {
                foreach (var (start, length) in ranges)
                {
                    foreach (var rect in TextLayout.HitTestTextRange(start, length))
                    {
                        context.FillRectangle(brush!, rect);
                    }
                }
            }
        }

        base.RenderTextLayout(context, origin);
    }
}
