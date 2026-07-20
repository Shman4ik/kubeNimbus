using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace KubeNimbus.App.Converters;

/// <summary>Inspector maximize toggle icon: "restore" glyph when maximized, "fullscreen" glyph otherwise.</summary>
public sealed class MaximizedToIconConverter : IValueConverter
{
    public static readonly MaximizedToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "FullscreenExitIconGeometry" : "FullscreenIconGeometry";
        return Application.Current is { } app
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is Geometry geometry
                ? geometry
                : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
