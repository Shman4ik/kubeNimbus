using System.Globalization;
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
        _ => null, // null Foreground falls back to the TextBlock's inherited (theme) color
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
