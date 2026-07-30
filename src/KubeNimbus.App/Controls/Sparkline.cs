using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KubeNimbus.App.Controls;

/// <summary>
/// A minimal area/line chart for a short numeric series — the renderer behind the
/// CPU/memory-over-time graphs (pod list cells and pod detail's Usage tab).
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a charting package on purpose: every
/// dependency has to survive NativeAOT + trimming (CLAUDE.md tech-stack rule),
/// and the charting libraries in the Avalonia ecosystem bring reflection-based
/// binding and theming with them. This is ~100 lines of <see cref="DrawingContext"/>
/// calls with no reflection, no templates and no per-frame allocation beyond the
/// two geometries it draws.
/// <para>
/// Values are oldest-first and evenly spaced across the width: the app polls
/// <c>metrics.k8s.io</c> on a fixed interval, so sample index *is* the time axis.
/// A null entry is a gap (the subject reported nothing that tick) and breaks the
/// line rather than being drawn as zero — a pod that stopped reporting must not
/// look like a pod that went idle.
/// </para>
/// </remarks>
public sealed class Sparkline : Control
{
    /// <summary>Headroom above the peak when auto-scaling, so the maximum doesn't sit flat on the top edge.</summary>
    private const double AutoScaleHeadroom = 1.12;

    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> AreaFillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(AreaFill));

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(BaselineBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.4);

    /// <summary>Top of the value axis. <see cref="double.NaN"/> (the default) auto-scales to the series peak.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), double.NaN);

    static Sparkline() =>
        AffectsRender<Sparkline>(
            ValuesProperty, StrokeProperty, AreaFillProperty, BaselineBrushProperty,
            StrokeThicknessProperty, MaximumProperty);

    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? AreaFill
    {
        get => GetValue(AreaFillProperty);
        set => SetValue(AreaFillProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// A bare <see cref="Control"/> measures to nothing, which would make the chart
    /// invisible inside a StackPanel. Fill the offered space instead, falling back
    /// to a compact inline size when a dimension is unconstrained.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 64 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 18 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (BaselineBrush is { } baseline)
        {
            var y = height - 0.5;
            context.DrawLine(new Pen(baseline, 1), new Point(0, y), new Point(width, y));
        }

        if (Values is not { Count: > 0 })
        {
            return;
        }

        // Explicitly non-nullable so the local render helpers below don't have to
        // re-prove it (nullable flow state doesn't cross into a local function).
        IReadOnlyList<double?> values = Values!;

        var max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            max = 0;
            foreach (var value in values)
            {
                if (value is { } v && v > max)
                {
                    max = v;
                }
            }

            max *= AutoScaleHeadroom;
        }

        if (max <= 0)
        {
            // Nothing but zeros/gaps: the baseline already tells that story.
            return;
        }

        var inset = StrokeThickness / 2 + 0.5;
        var plotWidth = Math.Max(width - inset * 2, 0);
        var plotHeight = Math.Max(height - inset * 2, 0);
        var pen = Stroke is { } stroke
            ? new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

        var index = 0;
        while (index < values.Count)
        {
            if (values[index] is null)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Count && values[index] is not null)
            {
                index++;
            }

            var end = index - 1;
            if (end == start)
            {
                // A lone reading between gaps has no line to draw — mark it as a dot
                // so a single sample still shows up rather than rendering as empty.
                if (Stroke is { } dot)
                {
                    context.DrawEllipse(dot, null, At(start), StrokeThickness, StrokeThickness);
                }

                continue;
            }

            if (AreaFill is { } fill)
            {
                context.DrawGeometry(fill, null, BuildArea(start, end));
            }

            if (pen is not null)
            {
                context.DrawGeometry(null, pen, BuildLine(start, end));
            }
        }

        // Named At rather than Point so it can't shadow the Avalonia.Point type.
        Point At(int i)
        {
            var x = values.Count == 1 ? width / 2 : inset + plotWidth * i / (values.Count - 1);
            var y = inset + plotHeight * (1 - Math.Clamp(values[i]!.Value / max, 0, 1));
            return new Point(x, y);
        }

        StreamGeometry BuildLine(int from, int to)
        {
            var geometry = new StreamGeometry();
            using var ctx = geometry.Open();
            ctx.BeginFigure(At(from), isFilled: false);
            for (var i = from + 1; i <= to; i++)
            {
                ctx.LineTo(At(i));
            }

            ctx.EndFigure(isClosed: false);
            return geometry;
        }

        StreamGeometry BuildArea(int from, int to)
        {
            var bottom = height;
            var geometry = new StreamGeometry();
            using var ctx = geometry.Open();
            ctx.BeginFigure(new Point(At(from).X, bottom), isFilled: true);
            for (var i = from; i <= to; i++)
            {
                ctx.LineTo(At(i));
            }

            ctx.LineTo(new Point(At(to).X, bottom));
            ctx.EndFigure(isClosed: true);
            return geometry;
        }
    }
}
