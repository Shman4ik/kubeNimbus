using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace KubeNimbus.App.Converters;

/// <summary>Resolves a StreamGeometry resource key (e.g. "CubeOutlineIconGeometry") to the geometry itself.</summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public static readonly IconKeyToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is { } app
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is Geometry geometry)
        {
            return geometry;
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
