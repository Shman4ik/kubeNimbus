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
    /// <summary>
    /// The app's one way of turning an instant into local time: every local time the app
    /// prints goes through it (the tooltips here, the timeline's ticks, the log pane's
    /// timestamps through <c>LogLineViewModel.ToLocal</c>, the YAML editor's "Applied at").
    /// <see cref="DateTimeOffset.ToLocalTime"/> rather than a <c>TimeZoneInfo</c> lookup by id,
    /// which wants tzdata a NativeAOT binary on Linux may not have. A seam because .NET on
    /// Windows ignores <c>TZ</c>: the screenshot harness sets it to UTC at startup, so a
    /// published screenshot reads the same whichever machine renders it, and tests pin a zone
    /// that is not the machine's. Nothing in the app assigns it.
    /// </summary>
#pragma warning disable RS0030 // the one conversion BannedSymbols.Clock.txt leaves open
    internal static Func<DateTimeOffset, DateTimeOffset> ToLocal { get; set; } = at => at.ToLocalTime();
#pragma warning restore RS0030

    /// <summary>The instant in the app's local time (<see cref="ToLocal"/>).</summary>
    public static DateTimeOffset Local(DateTimeOffset at) => ToLocal(at);

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
        var local = ToLocal(at);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var zone = offset == TimeSpan.Zero
            ? "UTC"
            : $"UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
        return $"{local.ToString(pattern, CultureInfo.InvariantCulture)} {zone}";
    }
}
