using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Converters;

/// <summary>Maps a LogLineViewModel.Severity to a foreground color for the log view — Error/Warn stand out, Info/None stay neutral.</summary>
public sealed class LogSeverityToBrushConverter : IValueConverter
{
    public static readonly LogSeverityToBrushConverter Instance = new();

    private static readonly IBrush Error = new SolidColorBrush(Colors.IndianRed);
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D9822B"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#4E9EF2"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Error => Error,
        LogSeverity.Warn => Warn,
        LogSeverity.Info => Info,

        // UnsetValue, emphatically NOT null. Foreground is an inherited property, and a
        // binding that produces null writes a *local* null which beats inheritance —
        // then Avalonia's glyph-run draw early-returns on a null brush, so every line
        // without a severity keyword rendered completely invisible. nginx access logs,
        // Go's log.Print and any JSON logger produce exactly those lines, which is most
        // of them. UnsetValue means "no value here", so inheritance wins and the line
        // takes the theme foreground in both light and dark.
        _ => AvaloniaProperty.UnsetValue,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
