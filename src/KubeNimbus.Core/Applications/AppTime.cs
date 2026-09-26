using System.Globalization;

namespace KubeNimbus.Core.Applications;

/// <summary>
/// The relative phrasing the Applications mode prints ("12 min ago", "for 2 h"). Every rule
/// takes its "now" as an argument rather than reading the clock, so a verdict is a pure
/// function of what the cluster held at that moment — which is what lets the tests pin it,
/// and what lets the demo cluster read its fixed dataset at a fixed instant.
/// </summary>
public static class AppTime
{
    public static string Ago(DateTimeOffset now, DateTimeOffset at)
    {
        var elapsed = now - at;
        return elapsed.TotalSeconds < 60 ? "just now" : $"{Span(elapsed)} ago";
    }

    /// <summary>"42 s", "12 min", "2 h", "4 days".</summary>
    public static string Span(TimeSpan span)
    {
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        return span switch
        {
            { TotalSeconds: < 60 } => $"{(int)span.TotalSeconds} s",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min",
            { TotalHours: < 48 } => $"{(int)span.TotalHours} h",
            _ => (int)span.TotalDays == 1 ? "1 day" : $"{(int)span.TotalDays} days",
        };
    }

    /// <summary>A clock time in UTC, for timeline ticks and log footers: "08:54:36".</summary>
    public static string Clock(DateTimeOffset at) =>
        at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
