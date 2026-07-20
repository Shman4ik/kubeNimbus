using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>Sidebar section chevron rotation: 90° (pointing down) when expanded, 0° (pointing right) when collapsed.</summary>
public sealed class ExpandedToAngleConverter : IValueConverter
{
    public static readonly ExpandedToAngleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 90d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
