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
    public static string Exact(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var zone = offset == TimeSpan.Zero
            ? "UTC"
            : $"UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {zone}";
    }
}
