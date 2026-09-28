using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Controls;

/// <summary>
/// A thin strip beside a log pane's scrollbar with a tick for every error, warning, search
/// match and pinned highlight among the shown lines, at the height the line sits in the
/// pane. A click scrolls to the nearest marked line.
/// </summary>
/// <remarks>
/// <para>
/// The counts in the pane's bar say how many errors there are; this says where. It is
/// VS Code's overview ruler, klogg's and lnav's marks column: the one view of a
/// four-thousand-line buffer that fits on screen, and the reason nobody has to scroll a
/// whole log looking for the red.
/// </para>
/// <para>
/// A tick's height is the line's position in the list, not in pixels, so with wrapping on
/// it is close rather than exact — the same trade VS Code makes. Lines are bucketed into
/// pixel rows first and each row drawn once, so four thousand lines cost as many
/// rectangles as the strip is tall, whatever the buffer holds. The search's answer for a
/// line is remembered on the line (<see cref="LogLineViewModel.Matches"/>), so a flush of
/// new lines does not re-run a regular expression over the old ones.
/// </para>
/// </remarks>
public sealed class LogOverviewRuler : Control
{
    public static readonly StyledProperty<IList<LogLineViewModel>?> LinesProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IList<LogLineViewModel>?>(nameof(Lines));

    public static readonly StyledProperty<LogQuery?> QueryProperty =
        AvaloniaProperty.Register<LogOverviewRuler, LogQuery?>(nameof(Query));

    public static readonly StyledProperty<IReadOnlyList<LogPin>?> PinsProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IReadOnlyList<LogPin>?>(nameof(Pins));

    public static readonly StyledProperty<LogLineViewModel?> CursorLineProperty =
        AvaloniaProperty.Register<LogOverviewRuler, LogLineViewModel?>(nameof(CursorLine));


    public static readonly StyledProperty<IBrush?> ErrorBrushProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IBrush?>(nameof(ErrorBrush));

    public static readonly StyledProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IBrush?>(nameof(WarnBrush));

    public static readonly StyledProperty<IBrush?> MatchBrushProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IBrush?>(nameof(MatchBrush));

    public static readonly StyledProperty<IBrush?> CursorBrushProperty =
        AvaloniaProperty.Register<LogOverviewRuler, IBrush?>(nameof(CursorBrush));

    private INotifyCollectionChanged? _observed;
    private int[] _rowLine = [];

    static LogOverviewRuler()
    {
        AffectsRender<LogOverviewRuler>(
            QueryProperty, PinsProperty, CursorLineProperty, ErrorBrushProperty, WarnBrushProperty, MatchBrushProperty, CursorBrushProperty);
    }

    public LogOverviewRuler() => Cursor = new Cursor(StandardCursorType.Hand);

    public IList<LogLineViewModel>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public LogQuery? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public IReadOnlyList<LogPin>? Pins
    {
        get => GetValue(PinsProperty);
        set => SetValue(PinsProperty, value);
    }

    /// <summary>The line the pane's error jump is on, drawn across the whole strip.</summary>
    public LogLineViewModel? CursorLine
    {
        get => GetValue(CursorLineProperty);
        set => SetValue(CursorLineProperty, value);
    }

    /// <summary>
    /// Where line <c>i</c> sits, as a fraction of the strip's height, or null when that is
    /// not known yet. Set by the pane from its rendered rows, so a tick lands on its line
    /// whether the lines wrap or not and whether or not they fill the pane — a log shorter
    /// than its pane occupies the top of it, and a tick placed by index alone would sit far
    /// below the line it marks. Without it, lines are placed by index.
    /// </summary>
    public Func<int, double?>? PositionOf { get; set; }

    public IBrush? ErrorBrush
    {
        get => GetValue(ErrorBrushProperty);
        set => SetValue(ErrorBrushProperty, value);
    }

    public IBrush? WarnBrush
    {
        get => GetValue(WarnBrushProperty);
        set => SetValue(WarnBrushProperty, value);
    }

    public IBrush? MatchBrush
    {
        get => GetValue(MatchBrushProperty);
        set => SetValue(MatchBrushProperty, value);
    }

    public IBrush? CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    /// <summary>A tick was clicked: the line to bring into view.</summary>
    public event EventHandler<LogLineViewModel>? LineRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesProperty)
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnLinesChanged;
            }

            _observed = change.NewValue as INotifyCollectionChanged;
            if (_observed is not null)
            {
                _observed.CollectionChanged += OnLinesChanged;
            }

            InvalidateVisual();
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    // Kinds in drawing order; a row shows every kind it has, the later ones on top.
    private const int MatchBit = 1, PinBit = 2, WarnBit = 4, ErrorBit = 8, CursorBit = 16;

    /// <summary>
    /// Which marks each pixel row of a strip <paramref name="height"/> tall carries, and the
    /// first line that put a mark there. Static and public so the tests pin the bucketing
    /// without a rendered control.
    /// </summary>
    public static (int[] Marks, int[] FirstLine, int[] Pin) Bucket(
        IList<LogLineViewModel> lines, int height, LogQuery? query, IReadOnlyList<LogPin>? pins, LogLineViewModel? cursor,
        Func<int, double?>? positionOf = null)
    {
        var marks = new int[Math.Max(0, height)];
        var first = new int[marks.Length];
        var pinOf = new int[marks.Length];
        Array.Fill(first, -1);
        Array.Fill(pinOf, -1);
        if (lines.Count == 0 || height <= 0)
        {
            return (marks, first, pinOf);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var bits = 0;
            if (line.Severity == LogSeverity.Error && !line.IsInheritedSeverity) bits |= ErrorBit;
            else if (line.Severity == LogSeverity.Warn && !line.IsInheritedSeverity) bits |= WarnBit;
            if (query is not null && line.Matches(query)) bits |= MatchBit;
            var pin = -1;
            for (var p = 0; pins is not null && p < pins.Count && pin < 0; p++)
            {
                if (line.Matches(pins[p].Query)) pin = p;
            }

            if (pin >= 0) bits |= PinBit;
            if (ReferenceEquals(line, cursor)) bits |= CursorBit;
            if (bits == 0) continue;

            var row = positionOf?.Invoke(i) is { } at
                ? Math.Clamp((int)(at * height), 0, height - 1)
                : (int)((long)i * height / lines.Count);
            marks[row] |= bits;
            if (first[row] < 0) first[row] = i;
            if (pin >= 0 && pinOf[row] < 0) pinOf[row] = pin;
        }

        return (marks, first, pinOf);
    }

    public override void Render(DrawingContext context)
    {
        // Transparent rather than nothing, so the whole strip hit-tests and not only the
        // ticks (UI rule 8).
        var bounds = Bounds;
        context.FillRectangle(Brushes.Transparent, new Rect(bounds.Size));

        if (Lines is not { Count: > 0 } lines)
        {
            _rowLine = [];
            return;
        }

        var height = (int)bounds.Height;
        var (marks, first, pinOf) = Bucket(lines, height, Query, Pins, CursorLine, PositionOf);
        _rowLine = first;
        var width = bounds.Width;
        var pins = Pins;
        for (var row = 0; row < marks.Length; row++)
        {
            var bits = marks[row];
            if (bits == 0) continue;

            // Matches and pins on the left half, severity on the right, so a matching error
            // shows both; the cursor spans the strip.
            if ((bits & MatchBit) != 0 && MatchBrush is { } match)
                context.FillRectangle(match, new Rect(0, row - 1, width / 2, 3));
            if ((bits & PinBit) != 0 && pins is not null && pinOf[row] is var p and >= 0 && p < pins.Count)
                context.FillRectangle(pins[p].SolidBrush, new Rect(0, row - 1, width / 2, 3));
            if ((bits & WarnBit) != 0 && WarnBrush is { } warn)
                context.FillRectangle(warn, new Rect(width / 2, row - 1, width / 2, 3));
            if ((bits & ErrorBit) != 0 && ErrorBrush is { } error)
                context.FillRectangle(error, new Rect(width / 2, row - 1, width / 2, 3));
            if ((bits & CursorBit) != 0 && CursorBrush is { } cursor)
                context.FillRectangle(cursor, new Rect(0, row - 1, width, 3));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Lines is not { Count: > 0 } lines)
        {
            return;
        }

        var y = (int)e.GetPosition(this).Y;
        var index = -1;

        // The nearest marked row within a few pixels: ticks are three pixels tall and a
        // click that lands beside one means that one.
        for (var d = 0; d <= 4 && index < 0; d++)
        {
            foreach (var row in (int[])[y - d, y + d])
            {
                if (row >= 0 && row < _rowLine.Length && _rowLine[row] >= 0)
                {
                    index = _rowLine[row];
                    break;
                }
            }
        }

        if (index < 0)
        {
            index = Math.Clamp((int)(y / Math.Max(1, Bounds.Height) * lines.Count), 0, lines.Count - 1);
        }

        LineRequested?.Invoke(this, lines[index]);
        e.Handled = true;
    }
}
