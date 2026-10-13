using System.Text.RegularExpressions;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #320: the exact times on the tooltips (a condition's last transition, a row's creation, an
/// event's last sighting) are local time, and say which zone: the API's own timestamps are UTC,
/// and a local time with no zone read as one of them.
/// </summary>
public partial class LocalTimeTextTests
{
    [Test]
    public async Task An_instant_prints_in_local_time_with_its_offset_from_UTC()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 1, 54, TimeSpan.Zero);
        var local = at.ToLocalTime();

        var text = LocalTimeText.Exact(at);

        await Assert.That(text).StartsWith(local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(OffsetPattern().IsMatch(text)).IsTrue();
    }

    [Test]
    public async Task The_condition_tooltip_says_its_zone()
    {
        var tooltip = ConditionSince.Tooltip(new DateTimeOffset(2026, 10, 4, 12, 1, 54, TimeSpan.Zero));

        await Assert.That(tooltip).IsNotNull();
        await Assert.That(OffsetPattern().IsMatch(tooltip!)).IsTrue();
    }

    [Test]
    public async Task A_time_of_day_prints_with_its_offset_too()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 1, 54, TimeSpan.Zero);
        var local = at.ToLocalTime();

        await Assert.That(LocalTimeText.Clock(at)).StartsWith(local.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(LocalTimeText.Clock(at, seconds: false)).StartsWith(local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC");
        await Assert.That(OffsetPattern().IsMatch(LocalTimeText.Clock(at))).IsTrue();
    }

    /// <summary>The Applications page's exit line said "at 14:02:11" with no zone.</summary>
    [Test]
    public async Task A_containers_exit_line_says_its_zone()
    {
        var ended = new KubeNimbus.Core.Applications.TerminatedState(
            1, "Error", "", null, new DateTimeOffset(2026, 10, 4, 12, 1, 54, TimeSpan.Zero), "", null);

        var line = ApplicationPageViewModel.ExitLine("app", ended);

        await Assert.That(line).StartsWith("Container app exited with code 1 (Error) at ");
        await Assert.That(OffsetPattern().IsMatch(line)).IsTrue();
    }

    [GeneratedRegex(@" UTC([+-]\d\d:\d\d)?$")]
    private static partial Regex OffsetPattern();
}
