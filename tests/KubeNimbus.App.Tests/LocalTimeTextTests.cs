using System.Globalization;
using System.Text.RegularExpressions;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #320: the exact times on the tooltips (a condition's last transition, a row's creation, an
/// event's last sighting) and the times of day on the Applications page are local time, and
/// say which zone: the API's own timestamps are UTC, and a local time with no zone read as one
/// of them. #346: every one of them goes through <see cref="LocalTimeText.ToLocal"/>, the seam
/// the screenshot harness pins to UTC.
/// </summary>
/// <remarks>
/// <c>[NotInParallel]</c> on the seam's key: one test here assigns it, as <c>LogPaneTests</c>
/// does, and the others read it.
/// </remarks>
[NotInParallel(nameof(LogLineViewModel.ToLocal))]
public partial class LocalTimeTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 1, 54, TimeSpan.Zero);

    [Test]
    public async Task An_instant_prints_in_local_time_with_its_offset_from_UTC()
    {
        var text = LocalTimeText.Exact(At);

        await Assert.That(text).StartsWith(At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(OffsetPattern().IsMatch(text)).IsTrue();
    }

    [Test]
    public async Task The_condition_tooltip_says_its_zone()
    {
        var tooltip = ConditionSince.Tooltip(At);

        await Assert.That(tooltip).IsNotNull();
        await Assert.That(OffsetPattern().IsMatch(tooltip!)).IsTrue();
    }

    [Test]
    public async Task A_time_of_day_prints_with_its_offset_too()
    {
        var local = At.ToLocalTime();

        await Assert.That(LocalTimeText.Clock(At)).StartsWith(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(LocalTimeText.Clock(At, seconds: false)).StartsWith(local.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(OffsetPattern().IsMatch(LocalTimeText.Clock(At))).IsTrue();
    }

    /// <summary>The Applications page's exit line said "at 14:02:11" with no zone.</summary>
    [Test]
    public async Task A_containers_exit_line_says_its_zone()
    {
        var line = ApplicationPageViewModel.ExitLine("app", new TerminatedState(1, "Error", "", null, At, "", null));

        await Assert.That(line).StartsWith("Container app exited with code 1 (Error) at ");
        await Assert.That(OffsetPattern().IsMatch(line)).IsTrue();
    }

    /// <summary>
    /// The seam the harness sets: once it is assigned, every local-time text follows it,
    /// whatever the machine's zone — the tooltip, the time of day, the exit line and the log
    /// pane's name for the same seam. Without it a published screenshot printed the
    /// renderer's own offset.
    /// </summary>
    [Test]
    public async Task Every_local_time_follows_the_one_seam()
    {
        var previous = LocalTimeText.ToLocal;
        try
        {
            LocalTimeText.ToLocal = at => at.ToOffset(TimeSpan.FromHours(3));

            await Assert.That(LocalTimeText.Exact(At)).IsEqualTo("2026-10-04 15:01:54 UTC+03:00");
            await Assert.That(LocalTimeText.Clock(At)).IsEqualTo("15:01:54 UTC+03:00");
            await Assert.That(ConditionSince.Tooltip(At)).IsEqualTo("Last transition 2026-10-04 15:01:54 UTC+03:00");
            await Assert.That(ApplicationPageViewModel.ExitLine("app", new TerminatedState(1, "Error", "", null, At, "", null)))
                .EndsWith(" at 15:01:54 UTC+03:00");
            await Assert.That(LogLineViewModel.ToLocal(At).Offset).IsEqualTo(TimeSpan.FromHours(3));

            // As the harness sets it.
            LocalTimeText.ToLocal = at => at.ToUniversalTime();
            await Assert.That(LocalTimeText.Exact(At.ToOffset(TimeSpan.FromHours(-7)))).IsEqualTo("2026-10-04 12:01:54 UTC");
        }
        finally
        {
            LocalTimeText.ToLocal = previous;
        }
    }

    [GeneratedRegex(@" UTC([+-]\d\d:\d\d)?$")]
    private static partial Regex OffsetPattern();
}
