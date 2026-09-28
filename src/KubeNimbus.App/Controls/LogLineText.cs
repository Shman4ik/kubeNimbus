using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Controls;

/// <summary>
/// One log line: its level keyword coloured, its timestamp dimmed, and every match of the
/// log search highlighted in place — the "find" half of the log panes' search, where the
/// lines around a match stay visible.
/// </summary>
/// <remarks>
/// <para>
/// The text stays a single bound string, so selection and Ctrl+C behave exactly as a
/// <see cref="SelectableTextBlock"/>'s do. The colours are <em>style overrides</em> on the
/// block's own <see cref="TextLayout"/> (<see cref="CreateTextLayout"/>), not
/// <c>Inlines</c> of coloured <c>Run</c>s, and the match boxes are painted under the
/// glyphs from the same layout's hit-test rectangles, so they follow wrapping and the font
/// with no second measurement of their own. Same argument as <c>Sparkline</c>: a few dozen
/// lines of drawing beat a component with a model of its own.
/// </para>
/// <para>
/// Only the level keyword takes the severity colour. The whole line used to, and on an
/// ASP.NET pod where every line is <c>info:</c> the pane turned blue from top to bottom — a
/// colour on every line says nothing about any of them. The row's own marking of errors
/// and warnings is the view's (<c>Border.logRow</c> in Theme.axaml).
/// </para>
/// <para>
/// Every brush here is optional and a null one draws nothing extra: a null
/// <see cref="LevelBrush"/> leaves the keyword in the block's inherited foreground. That is
/// the property the old <c>Foreground</c> binding lacked (<c>log-severity-classes.md</c>).
/// </para>
/// <para>
/// Styled as a <see cref="SelectableTextBlock"/> (<see cref="StyleKeyOverride"/>), so the
/// theme and the selectors written for that type still apply.
/// </para>
/// </remarks>
public sealed class LogLineText : SelectableTextBlock
{
    public static readonly StyledProperty<LogQuery?> QueryProperty =
        AvaloniaProperty.Register<LogLineText, LogQuery?>(nameof(Query));

    public static readonly StyledProperty<IReadOnlyList<LogPin>?> PinsProperty =
        AvaloniaProperty.Register<LogLineText, IReadOnlyList<LogPin>?>(nameof(Pins));

    public static readonly StyledProperty<int> MatchStartProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(MatchStart));

    public static readonly StyledProperty<bool> IsCurrentMatchProperty =
        AvaloniaProperty.Register<LogLineText, bool>(nameof(IsCurrentMatch));

    public static readonly StyledProperty<IBrush?> MatchBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(MatchBrush));

    public static readonly StyledProperty<IBrush?> CurrentMatchBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(CurrentMatchBrush));

    public static readonly StyledProperty<int> LevelStartProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(LevelStart), -1);

    public static readonly StyledProperty<int> LevelLengthProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(LevelLength));

    public static readonly StyledProperty<IBrush?> LevelBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(LevelBrush));

    public static readonly StyledProperty<IBrush?> PrefixBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(PrefixBrush));

    static LogLineText()
    {
        AffectsRender<LogLineText>(
            QueryProperty, PinsProperty, MatchStartProperty, IsCurrentMatchProperty, MatchBrushProperty, CurrentMatchBrushProperty);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelStartProperty || change.Property == LevelLengthProperty
            || change.Property == LevelBrushProperty || change.Property == PrefixBrushProperty
            || change.Property == MatchStartProperty)
        {
            // The colours are part of the layout, so a new one is a new layout, not a repaint.
            InvalidateTextLayout();
        }
    }

    /// <summary>The pane's search. Null highlights nothing.</summary>
    public LogQuery? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    /// <summary>The pane's pinned highlights, each painted in its own colour under the search's own matches.</summary>
    public IReadOnlyList<LogPin>? Pins
    {
        get => GetValue(PinsProperty);
        set => SetValue(PinsProperty, value);
    }

    /// <summary>
    /// Index into <see cref="TextBlock.Text"/> where the message starts, past any timestamp
    /// the pane printed. Matching starts here, and what is before it is drawn in
    /// <see cref="PrefixBrush"/>.
    /// </summary>
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

    /// <summary>Where the level keyword starts in <see cref="TextBlock.Text"/>; -1 for none.</summary>
    public int LevelStart
    {
        get => GetValue(LevelStartProperty);
        set => SetValue(LevelStartProperty, value);
    }

    public int LevelLength
    {
        get => GetValue(LevelLengthProperty);
        set => SetValue(LevelLengthProperty, value);
    }

    /// <summary>The level keyword's colour, set by the severity classes; null leaves it plain.</summary>
    public IBrush? LevelBrush
    {
        get => GetValue(LevelBrushProperty);
        set => SetValue(LevelBrushProperty, value);
    }

    /// <summary>The timestamp prefix's colour (text before <see cref="MatchStart"/>); null leaves it plain.</summary>
    public IBrush? PrefixBrush
    {
        get => GetValue(PrefixBrushProperty);
        set => SetValue(PrefixBrushProperty, value);
    }

    /// <summary>
    /// The coloured spans for a line of <paramref name="length"/> characters, in order and
    /// without overlaps: the prefix [0, <paramref name="prefixLength"/>), the level keyword,
    /// and the selection on top of either — the selection's own foreground wins where they
    /// meet, as it does in the base block. Static and public so the tests pin the spans
    /// without a rendered control.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length, int Kind)> Spans(
        int length, int prefixLength, int levelStart, int levelLength, int selectionStart, int selectionLength)
    {
        // Kind: 0 prefix, 1 level, 2 selection.
        var wanted = new List<(int Start, int End, int Kind)>(3);
        if (prefixLength > 0)
        {
            wanted.Add((0, Math.Min(prefixLength, length), 0));
        }

        if (levelStart >= 0 && levelLength > 0 && levelStart < length)
        {
            wanted.Add((levelStart, Math.Min(levelStart + levelLength, length), 1));
        }

        var spans = new List<(int, int, int)>();
        var selStart = Math.Clamp(selectionStart, 0, length);
        var selEnd = Math.Clamp(selectionStart + selectionLength, 0, length);
        foreach (var (start, end, kind) in wanted.OrderBy(w => w.Start))
        {
            // The parts of this span outside the selection.
            if (selEnd <= selStart || end <= selStart || start >= selEnd)
            {
                if (end > start) spans.Add((start, end - start, kind));
                continue;
            }

            if (start < selStart) spans.Add((start, selStart - start, kind));
            if (end > selEnd) spans.Add((selEnd, end - selEnd, kind));
        }

        if (selEnd > selStart)
        {
            spans.Add((selStart, selEnd - selStart, 2));
        }

        spans.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return spans;
    }

    /// <summary>
    /// The base block's layout, with the prefix, level and selection colours as style
    /// overrides. <see cref="SelectableTextBlock"/>'s own override applies only the
    /// selection's foreground, and a layout carries one list of overrides, so this builds the
    /// whole list and the layout with it. A line with nothing to colour and no selection
    /// takes the base path unchanged.
    /// </summary>
    protected override TextLayout CreateTextLayout(string? text)
    {
        var content = text ?? "";
        var selectionStart = Math.Min(SelectionStart, SelectionEnd);
        var selectionLength = Math.Abs(SelectionEnd - SelectionStart);
        var prefix = PrefixBrush is null ? 0 : MatchStart;
        var levelStart = LevelBrush is null ? -1 : LevelStart;
        if (Inlines is { Count: > 0 } || (prefix <= 0 && levelStart < 0))
        {
            return base.CreateTextLayout(text);
        }

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var selectionBrush = selectionLength > 0 ? SelectionForegroundBrush : null;
        var overrides = new List<ValueSpan<TextRunProperties>>();
        foreach (var (start, length, kind) in Spans(
                     content.Length, prefix, levelStart, LevelLength, selectionStart, selectionBrush is null ? 0 : selectionLength))
        {
            var brush = kind switch { 0 => PrefixBrush, 1 => LevelBrush, _ => selectionBrush };
            overrides.Add(new ValueSpan<TextRunProperties>(
                start, length, new GenericTextRunProperties(typeface, FontSize, foregroundBrush: brush, fontFeatures: FontFeatures)));
        }

        var maxSize = GetMaxSizeFromConstraint();
        return new TextLayout(
            content,
            typeface,
            FontSize,
            Foreground,
            TextAlignment,
            TextWrapping,
            TextTrimming,
            TextDecorations,
            FlowDirection,
            maxSize.Width,
            maxSize.Height,
            LineHeight,
            LetterSpacing,
            MaxLines,
            FontFeatures,
            overrides);
    }

    /// <summary>
    /// Paints the match boxes, then lets the block draw its selection and text over them.
    /// <see cref="TextBlock.Render"/> is sealed; this is the hook it calls with the text's
    /// own origin, which is exactly the frame the layout's hit-test rectangles are in — so
    /// padding and alignment cannot push the boxes off the glyphs.
    /// </summary>
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (Pins is { Count: > 0 } pins)
        {
            foreach (var pin in pins)
            {
                Paint(context, origin, pin.Brush, pin.Query.Matches(Text, MatchStart));
            }
        }

        var brush = IsCurrentMatch ? CurrentMatchBrush ?? MatchBrush : MatchBrush;
        if (brush is not null && Query is { } query)
        {
            Paint(context, origin, brush, query.Matches(Text, MatchStart));
        }

        base.RenderTextLayout(context, origin);
    }

    private void Paint(DrawingContext context, Point origin, IBrush brush, IReadOnlyList<(int Start, int Length)> ranges)
    {
        if (ranges.Count == 0)
        {
            return;
        }

        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            foreach (var (start, length) in ranges)
            {
                foreach (var rect in TextLayout.HitTestTextRange(start, length))
                {
                    context.FillRectangle(brush, rect);
                }
            }
        }
    }
}
