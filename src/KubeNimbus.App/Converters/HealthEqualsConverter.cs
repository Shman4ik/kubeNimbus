using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>True when a ResourceRowViewModel.StatusHealth string equals the converter parameter
/// ("ok"/"warn"/"error") — drives the status pill's color via Classes.ok/.warn/.error.</summary>
public sealed class HealthEqualsConverter : IValueConverter
{
    public static readonly HealthEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
