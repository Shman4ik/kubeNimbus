using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.Controls;

/// <summary>
/// The application page's timeline: one horizontal strip over the last 15–60 minutes, with a
/// vertical mark per deploy, a dot per dated container termination and per Warning event.
/// </summary>
/// <remarks>
/// <para>
/// Drawn by hand for <see cref="Sparkline"/>'s reasons — no charting dependency survives
/// NativeAOT for free — and because the whole thing is four kinds of mark on one axis.
/// </para>
/// <para>
/// Colours are the theme's own brushes (accent for deploys, danger for terminations,
/// warning for events), resolved at render time so a live theme switch repaints correctly;
/// the kind is also always said in the hover text, so colour never carries it alone. Hover
/// shows the nearest mark's detail in the tooltip — a strip of dots with nothing behind them
/// would be a picture, not evidence.
/// </para>
/// </remarks>
public sealed class TimelineStrip : Control
{
    public static readonly StyledProperty<TimelineWindow?> WindowProperty =
        AvaloniaProperty.Register<TimelineStrip, TimelineWindow?>(nameof(Window));

    static TimelineStrip() => AffectsRender<TimelineStrip>(WindowProperty);

    public TimelineWindow? Window
    {
        get => GetValue(WindowProperty);
        set => SetValue(WindowProperty, value);
    }

    private const double Inset = 22;
    private const double AxisY = 30;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 56);

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private double X(TimelineWindow window, DateTimeOffset at) =>
        Inset + window.Position(at) * Math.Max(Bounds.Width - Inset * 2, 1);

    public override void Render(DrawingContext context)
    {
        if (Window is not { } window || Bounds.Width <= Inset * 2)
        {
            return;
        }

        var text = Brush("SystemControlForegroundBaseMediumBrush", Brushes.Gray);
        var line = Brush("CardBorderBrush", Brushes.LightGray);
        var accent = Brush("AppAccentBrush", Brushes.DodgerBlue);
        var danger = Brush("AppDangerBrush", Brushes.IndianRed);
        var warning = Brush("AppWarningBrush", Brushes.Orange);

        var left = Inset;
        var right = Bounds.Width - Inset;
        context.DrawLine(new Pen(line, 1), new Point(left, AxisY), new Point(right, AxisY));

        // Four ticks: the window's edges and thirds, in local wall-clock time.
        var typeface = new Typeface(FontFamily.Default);
        for (var i = 0; i <= 3; i++)
        {
            var at = window.Start + window.Span * (i / 3.0);
            var label = i == 3 ? $"now {at.ToLocalTime():HH:mm}" : at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            var formatted = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, text);
            var x = X(window, at) - formatted.Width / 2;
            x = Math.Clamp(x, 0, Bounds.Width - formatted.Width);
            context.DrawText(formatted, new Point(x, AxisY + 8));
        }

        foreach (var item in window.Items)
        {
            var x = X(window, item.At);
            switch (item.Kind)
            {
                case TimelineKind.Deploy:
                    context.DrawLine(new Pen(accent, 2), new Point(x, AxisY - 16), new Point(x, AxisY + 4));
                    var label = new FormattedText(item.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 10.5, accent);
                    context.DrawText(label, new Point(Math.Min(x + 4, Bounds.Width - label.Width), AxisY - 28));
                    break;
                case TimelineKind.Termination:
                    context.DrawEllipse(danger, null, new Point(x, AxisY), 4, 4);
                    break;
                case TimelineKind.WarningEvent:
                    context.DrawRectangle(warning, null, new Rect(x - 3.5, AxisY - 3.5, 7, 7));
                    break;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Window is not { Items.Count: > 0 } window)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        var x = e.GetPosition(this).X;
        var nearest = window.Items
            .Select(i => (Item: i, Distance: Math.Abs(X(window, i.At) - x)))
            .OrderBy(p => p.Distance)
            .First();
        if (nearest.Distance > 12)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        var kind = nearest.Item.Kind switch
        {
            TimelineKind.Deploy => "Deploy",
            TimelineKind.Termination => "Container ended",
            _ => "Warning event",
        };
        ToolTip.SetTip(this, $"{kind} · {nearest.Item.At.ToLocalTime():HH:mm:ss}\n{nearest.Item.Detail}");
    }
}
