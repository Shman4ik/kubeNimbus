namespace KubeNimbus.Core;

/// <summary>
/// kubectl's AGE column, at one unit instead of two. kubectl's own
/// <c>duration.HumanDuration</c> mixes units below its thresholds ("3d2h",
/// "5m30s"); in a list two hundred rows deep that trailing unit is noise that
/// differs on every row and stops the column lining up, and the exact timestamp
/// is one tooltip away. If exact kubectl parity ever matters more than that,
/// this is the one function to change.
/// </summary>
/// <remarks>
/// In Core rather than beside the list row that first needed it because a CRD's
/// <c>additionalPrinterColumns</c> can declare <c>type: date</c> for any field, and
/// the API server humanizes those itself before kubectl ever sees them
/// (<c>tableconvertor.cellForJSONValue</c> → <c>ConvertToHumanReadableDateString</c>).
/// So the same formatting is now a property of reading an object, not of one column
/// in one view — and it names no UI type, which is the membership test.
/// </remarks>
public static class RelativeTime
{
    public static string Compact(TimeSpan elapsed)
    {
        // Clock skew, or a creationTimestamp in the future: "0s", never "-3s".
        if (elapsed.Ticks <= 0)
        {
            return "0s";
        }

        return elapsed switch
        {
            { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h",
            { TotalDays: < 365 } => $"{(int)elapsed.TotalDays}d",
            _ => $"{(int)(elapsed.TotalDays / 365)}y",
        };
    }
}
