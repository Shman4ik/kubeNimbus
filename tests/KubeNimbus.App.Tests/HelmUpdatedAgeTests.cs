using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-72: the Helm list's and a release history's Updated column reads as an age, the way
/// the resource list's Age column does, with the exact instant on the tooltip. It printed the
/// <c>DateTimeOffset</c> itself and was cut in the middle of its offset at 1280px. The sort
/// stays on the instant (<see cref="HelmArgoSortTests"/>).
/// </summary>
public class HelmUpdatedAgeTests
{
    private static HelmReleaseRowViewModel Row(DateTimeOffset? updated) =>
        new(new HelmRelease("checkout", "payments", 7, "deployed", "checkout", "1.4.2", "2.14.3", updated, "Upgrade complete"));

    [Test]
    public async Task Updated_reads_as_an_age_with_the_instant_on_the_tooltip()
    {
        var at = new DateTimeOffset(2026, 7, 20, 8, 41, 2, TimeSpan.Zero);
        var row = Row(at);

        row.RefreshTimes(at.AddDays(3).AddHours(5));
        await Assert.That(row.UpdatedText).IsEqualTo("3d");
        await Assert.That(row.UpdatedTooltip).IsEqualTo($"Updated {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        // The clock moves the age with nothing else changing.
        row.RefreshTimes(at.AddMinutes(4));
        await Assert.That(row.UpdatedText).IsEqualTo("4m");
    }

    [Test]
    public async Task A_revision_with_no_time_says_so()
    {
        var row = Row(null);

        await Assert.That(row.UpdatedText).IsEqualTo("—");
        await Assert.That(row.UpdatedTooltip).IsEqualTo("Helm recorded no time for this revision");
    }

    [Test]
    public async Task A_new_row_already_carries_its_age()
    {
        var row = Row(DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-1));

        await Assert.That(row.UpdatedText).IsEqualTo("2h");
    }
}
