using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace KubeNimbus.App.Converters;

/// <summary>Maps a ResourceRowViewModel.StatusHealth string ("ok"/"warn"/"error"/"idle") to its dot color.</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    public static readonly HealthToBrushConverter Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#46A758"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D9822B"));
    private static readonly IBrush Error = new SolidColorBrush(Colors.IndianRed);
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#80808080"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "ok" => Ok,
        "warn" => Warn,
        "error" => Error,
        _ => Idle,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
