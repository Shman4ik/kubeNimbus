using System.Globalization;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// How long a condition has held, as the pod, node and workload Conditions cards show it
/// (#268): its <c>lastTransitionTime</c> as an age in the list's Age format
/// (<see cref="RelativeTime.Compact"/>), with the exact instant on the tooltip. "Not Ready for
/// 6d" and "for 20s" are different problems, and <c>kubectl describe</c> prints the time too.
/// A condition without the field shows nothing rather than a guess (UI rule 20).
/// </summary>
public static class ConditionSince
{
    /// <summary>The age of the last transition ("6d"), or empty when the server sent none.</summary>
    public static string Age(DateTimeOffset? lastTransition, DateTimeOffset now) =>
        lastTransition is { } at ? RelativeTime.Compact(now - at) : "";

    /// <summary>The exact instant, in local time, or null (no tooltip) when there is none.</summary>
    public static string? Tooltip(DateTimeOffset? lastTransition) =>
        lastTransition is { } at
            ? $"Last transition {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>Reads a <c>lastTransitionTime</c> string; null when it is absent or unreadable.</summary>
    public static DateTimeOffset? Parse(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
