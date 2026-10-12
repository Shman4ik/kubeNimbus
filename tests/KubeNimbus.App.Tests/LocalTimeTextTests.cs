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

    [GeneratedRegex(@" UTC([+-]\d\d:\d\d)?$")]
    private static partial Regex OffsetPattern();
}
