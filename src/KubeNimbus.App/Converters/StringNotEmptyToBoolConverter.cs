using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>True when a bound string is non-empty — for "clear filter" button visibility.</summary>
public sealed class StringNotEmptyToBoolConverter : IValueConverter
{
    public static readonly StringNotEmptyToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
