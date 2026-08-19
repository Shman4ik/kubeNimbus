using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KubeNimbus.App.Controls;

/// <summary>
/// One node resource's track: how much of allocatable is already requested, and where
/// the sum of the declared limits falls on the same axis.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled for the same reason <see cref="Sparkline"/> is — no reflection, no
/// templates, nothing a NativeAOT publish has to be argued about — and one control
/// rather than two stacked bars because the card lives in a ~300px dock (UI rule 10):
/// a second bar per resource would triple the card's height to say something the same
/// axis can already carry.
/// </para>
/// <para>
/// The requested fill clamps at the track, because a fill drawn past its own end says
/// less than the percentage printed beside it already does. The limit marker does
/// <em>not</em> silently clamp: limits routinely oversubscribe a node (that is ordinary
/// overcommit, and is exactly what someone opens this card to see), so a limit past
/// allocatable pins the marker at the track's end and draws it in
/// <see cref="OverBrush"/> — a different colour from an in-range marker, so "at the
/// edge" and "past the edge" can never render identically.
/// </para>
/// <para>
/// A null <see cref="LimitPercent"/> draws no marker and no extent at all. The pods row
/// has no limit to speak of and must not render an empty slot where the other two rows
/// have a figure.
/// </para>
/// </remarks>
public sealed class ResourceMeter : Control
{
    /// <summary>Requested as a percentage of allocatable. Values above 100 clamp to the track.</summary>
    public static readonly StyledProperty<double> RequestedPercentProperty =
        AvaloniaProperty.Register<ResourceMeter, double>(nameof(RequestedPercent));

    /// <summary>Sum of the declared limits as a percentage of allocatable, or null when there is none.</summary>
    public static readonly StyledProperty<double?> LimitPercentProperty =
        AvaloniaProperty.Register<ResourceMeter, double?>(nameof(LimitPercent));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ResourceMeter, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<ResourceMeter, IBrush?>(nameof(FillBrush));

    /// <summary>The lighter extent drawn from zero to the limit, behind the requested fill.</summary>
    public static readonly StyledProperty<IBrush?> LimitBrushProperty =
        AvaloniaProperty.Register<ResourceMeter, IBrush?>(nameof(LimitBrush));

    /// <summary>The marker at the limit, drawn on top of both.</summary>
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<ResourceMeter, IBrush?>(nameof(MarkerBrush));

    /// <summary>The marker's colour when the limit is past allocatable.</summary>
    public static readonly StyledProperty<IBrush?> OverBrushProperty =
        AvaloniaProperty.Register<ResourceMeter, IBrush?>(nameof(OverBrush));

    static ResourceMeter() =>
        AffectsRender<ResourceMeter>(
            RequestedPercentProperty, LimitPercentProperty, TrackBrushProperty,
            FillBrushProperty, LimitBrushProperty, MarkerBrushProperty, OverBrushProperty);

    public double RequestedPercent
    {
        get => GetValue(RequestedPercentProperty);
        set => SetValue(RequestedPercentProperty, value);
    }

    public double? LimitPercent
    {
        get => GetValue(LimitPercentProperty);
        set => SetValue(LimitPercentProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? LimitBrush
    {
        get => GetValue(LimitBrushProperty);
        set => SetValue(LimitBrushProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public IBrush? OverBrush
    {
        get => GetValue(OverBrushProperty);
        set => SetValue(OverBrushProperty, value);
    }

    /// <summary>
    /// A bare <see cref="Control"/> measures to nothing, which inside a Grid star column
    /// is invisible rather than merely small. Take the width offered and a compact
    /// default height.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 8 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;

        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, new RoundedRect(new Rect(0, 0, width, height), radius));
        }

        var limit = LimitPercent;
        if (limit is { } limitPercent && LimitBrush is { } limitFill)
        {
            var limitWidth = width * Math.Clamp(limitPercent, 0, 100) / 100d;
            if (limitWidth > 0)
            {
                context.DrawRectangle(
                    limitFill, null, new RoundedRect(new Rect(0, 0, limitWidth, height), radius));
            }
        }

        var fillWidth = width * Math.Clamp(RequestedPercent, 0, 100) / 100d;
        if (fillWidth > 0 && FillBrush is { } fill)
        {
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, fillWidth, height), radius));
        }

        if (limit is not { } marker)
        {
            return;
        }

        // 2px, and pinned inside the track's right edge when the limit oversubscribes —
        // drawn in the over colour so "exactly full" and "past full" never look alike.
        const double MarkerWidth = 2;
        var over = marker > 100;
        var brush = over ? OverBrush ?? MarkerBrush : MarkerBrush;
        if (brush is null)
        {
            return;
        }

        var x = width * Math.Clamp(marker, 0, 100) / 100d;
        x = Math.Clamp(x - MarkerWidth / 2, 0, Math.Max(width - MarkerWidth, 0));
        context.DrawRectangle(brush, null, new Rect(x, 0, MarkerWidth, height));
    }
}
