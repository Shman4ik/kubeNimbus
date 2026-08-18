using Avalonia.Media;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The colours an aggregated log pane keys its pods with, and the rule for shortening
/// a pod's name down to the part that distinguishes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One palette in both themes, deliberately.</b> These are mid-tone hues chosen to
/// stay legible on the light theme's near-white card and on the dark theme's near-black
/// one, rather than two palettes swapped on the theme variant. The argument is the one
/// already settled for the exec terminal's palette: a set of colours resolved once and
/// held (here, in a brush per line) does not follow a live theme swap, and a half-swapped
/// palette is worse than a single one that works in both. Both themes are rendered by
/// the screenshot harness, which is where this claim is checked rather than asserted.
/// </para>
/// <para>
/// <b>Eight, and cycling.</b> A workload with more than eight pods reuses colours — the
/// colour is a "these lines came from the same place" hint beside a name that is always
/// printed, not an identifier. Inventing more hues past eight buys pairs that are hard
/// to tell apart, which is worse than an honest repeat.
/// </para>
/// </remarks>
internal static class LogSourcePalette
{
    private static readonly IBrush[] Brushes =
    [
        new SolidColorBrush(Color.Parse("#3A8FD0")),
        new SolidColorBrush(Color.Parse("#2E9E5B")),
        new SolidColorBrush(Color.Parse("#CC7A2E")),
        new SolidColorBrush(Color.Parse("#9A5FC0")),
        new SolidColorBrush(Color.Parse("#1F9E9E")),
        new SolidColorBrush(Color.Parse("#D25C82")),
        new SolidColorBrush(Color.Parse("#6272D0")),
        new SolidColorBrush(Color.Parse("#A98A1E")),
    ];

    public static int Count => Brushes.Length;

    public static IBrush BrushFor(int index) => Brushes[((index % Brushes.Length) + Brushes.Length) % Brushes.Length];

    /// <summary>
    /// The part of <paramref name="podName"/> worth printing beside every line, given
    /// that every pod in the pane belongs to <paramref name="workloadName"/> and
    /// therefore starts with it. Falls back to the full name whenever the prefix is not
    /// actually shared (a pane opened on a Service, whose selector can match pods named
    /// anything at all) or when stripping it would leave nothing.
    /// </summary>
    public static string ShortNameFor(string podName, string workloadName)
    {
        if (workloadName.Length == 0
            || !podName.StartsWith(workloadName, StringComparison.Ordinal)
            || podName.Length <= workloadName.Length + 1
            || podName[workloadName.Length] != '-')
        {
            return podName;
        }

        return podName[(workloadName.Length + 1)..];
    }
}
