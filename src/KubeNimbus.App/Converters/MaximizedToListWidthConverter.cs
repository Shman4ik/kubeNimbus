using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>
/// Resource-list column width for the content Grid: collapses to zero when the
/// inspector is maximized (YAML/exec need the room far more than a fixed
/// ~440px sidecar gives them), otherwise a star-width share of the content area
/// so the split is responsive to window size instead of a hardcoded pixel width.
/// </summary>
public sealed class MaximizedToListWidthConverter : IValueConverter
{
    public static readonly MaximizedToListWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new GridLength(0) : new GridLength(1.4, GridUnitType.Star);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
