using System.Globalization;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// An exact instant as the tooltips print it: local time with its UTC offset, so
/// "2026-10-04 05:01:54 UTC-07:00". The API's timestamps are UTC and the tooltips convert
/// them to local time, which read as UTC with nothing saying otherwise (#320, QA checks
/// P-1a/b/c, a VM at UTC-7). The offset is printed rather than a zone name: the name of the
/// local zone depends on the platform's zone database, and the offset is the fact.
/// </summary>
public static class LocalTimeText
{
    /// <summary>The instant in local time, with its offset from UTC.</summary>
    public static string Exact(DateTimeOffset at) => Format(at, "yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// The time of day only, in local time with its UTC offset ("14:02:11 UTC-07:00"), for a
    /// line that already says which day it is about (a container's exit on the Applications
    /// page, a timeline mark). <paramref name="seconds"/> false prints "14:02 UTC-07:00".
    /// </summary>
    public static string Clock(DateTimeOffset at, bool seconds = true) => Format(at, seconds ? "HH:mm:ss" : "HH:mm");

    private static string Format(DateTimeOffset at, string pattern)
    {
        var local = at.ToLocalTime();
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var zone = offset == TimeSpan.Zero
            ? "UTC"
            : $"UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
        return $"{local.ToString(pattern, CultureInfo.InvariantCulture)} {zone}";
    }
}
