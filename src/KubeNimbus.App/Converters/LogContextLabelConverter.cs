using System.Globalization;
using Avalonia.Data.Converters;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Converters;

/// <summary>
/// A context size → the log filter's picker caption: "No context" / "±5 lines" in the list,
/// and with the parameter <c>short</c> the chip's own "Context" / "±5".
/// </summary>
public sealed class LogContextLabelConverter : IValueConverter
{
    public static readonly LogContextLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is "short"
            ? (value is int n and > 0 ? $"±{n}" : "Context")
            : LogSearch.ContextLabel(value is int lines ? lines : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
