using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>
/// True when a selected-tab index equals the converter parameter. Drives the
/// per-tab tool groups that share a row with a <c>ListBox.segmented</c> strip:
/// the tools belong to one tab, but they live outside the tab's content so the
/// strip and the toolbar can share a single row of the inspector dock.
/// </summary>
public sealed class IndexEqualsConverter : IValueConverter
{
    public static readonly IndexEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index
        && parameter is string text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected)
        && index == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
