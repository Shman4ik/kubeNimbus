using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>Dims a preview (quick-peek) inspector tab's title so it visually reads as transient.</summary>
public sealed class PreviewToOpacityConverter : IValueConverter
{
    public static readonly PreviewToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.65 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
