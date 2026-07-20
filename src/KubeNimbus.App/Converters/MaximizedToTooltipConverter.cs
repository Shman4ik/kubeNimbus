using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>Tooltip text for the inspector maximize toggle button.</summary>
public sealed class MaximizedToTooltipConverter : IValueConverter
{
    public static readonly MaximizedToTooltipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Restore split view" : "Maximize inspector";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
