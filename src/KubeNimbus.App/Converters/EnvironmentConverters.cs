using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using KubeNimbus.Core;

namespace KubeNimbus.App.Converters;

/// <summary>
/// <see cref="ClusterEnvironment"/> → the brush that colours it (tab edge, switcher
/// dot). Resolved from the theme dictionary rather than hardcoded so the palette
/// stays defined in one place (Theme.axaml's Env*Brush entries).
/// </summary>
public sealed class EnvironmentToBrushConverter : IValueConverter
{
    public static readonly EnvironmentToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is ClusterEnvironment environment
            ? environment switch
            {
                ClusterEnvironment.Production => "EnvProductionBrush",
                ClusterEnvironment.Staging => "EnvStagingBrush",
                ClusterEnvironment.Development => "EnvDevelopmentBrush",
                _ => "EnvUnknownBrush",
            }
            : "EnvUnknownBrush";

        if (Application.Current is { } app
            && app.TryFindResource(key, app.ActualThemeVariant, out var resource)
            && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Pinned state → filled or outline pin glyph.</summary>
public sealed class PinnedToIconConverter : IValueConverter
{
    public static readonly PinnedToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "PinIconGeometry" : "PinOutlineIconGeometry";
        return Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var resource)
            ? resource
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Pinned state → icon opacity. An unpinned pin sits back so it reads as an
/// affordance rather than a claim, but stays visible enough to be discoverable —
/// a control that only appears on hover is a control most people never find.
/// </summary>
public sealed class PinnedToOpacityConverter : IValueConverter
{
    public static readonly PinnedToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when the value is the <see cref="ClusterEnvironment"/> named in the
/// converter parameter. Drives both the env pill's style class
/// (<c>Classes.production="{Binding …, ConverterParameter=Production}"</c> —
/// Avalonia's class bindings are per-class booleans, a bound <c>Classes</c> string
/// is not a thing) and the environment menu's check marks.
/// </summary>
public sealed class EnvironmentEqualsConverter : IValueConverter
{
    public static readonly EnvironmentEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ClusterEnvironment environment
        && parameter is string name
        && Enum.TryParse<ClusterEnvironment>(name, ignoreCase: true, out var expected)
        && environment == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
