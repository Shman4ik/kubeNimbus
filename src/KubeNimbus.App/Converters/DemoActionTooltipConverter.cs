using System.Globalization;
using Avalonia.Data.Converters;

namespace KubeNimbus.App.Converters;

/// <summary>
/// Swaps a button's tooltip for its demo-cluster explanation. Exec and port-forward
/// are disabled on the demo cluster (their <c>CanExecute</c> needs a live client), and
/// a disabled control whose tooltip still describes what it would do reads as a bug
/// rather than as a limit — which is exactly the impression the demo mode exists to
/// avoid. Two named instances rather than a ConverterParameter, so the strings live
/// with the reason and the XAML stays a single binding.
/// </summary>
public sealed class DemoActionTooltipConverter(string live, string demo) : IValueConverter
{
    public static readonly DemoActionTooltipConverter Exec = new(
        "Exec into selected container",
        "Exec isn't available in the demo cluster — there is no container to run a shell in.");

    public static readonly DemoActionTooltipConverter PortForward = new(
        "Port-forward selected container",
        "Port-forwarding isn't available in the demo cluster — there is no kubelet to tunnel to.");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? demo : live;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
