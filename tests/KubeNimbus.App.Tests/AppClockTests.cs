using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #350: every age the app shows is read from <see cref="AppClock"/>, which the screenshot
/// harness stops at one instant. A row that read the wall clock instead rendered "74d" in one
/// harness run and "75d" in the next, and every re-render of the published screenshots
/// differed in an Age cell. <c>BannedSymbols.Clock.txt</c> stops a new direct read at build
/// time; these pin that the list's ages actually follow the replaced clock.
/// </summary>
[NotInParallel(nameof(AppClock))]
public class AppClockTests
{
    private sealed class Stopped(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Test]
    public async Task A_rows_age_reads_the_app_clock_not_the_wall_clock()
    {
        var created = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var previous = AppClock.Time;
        try
        {
            AppClock.Time = new Stopped(created + TimeSpan.FromDays(3) + TimeSpan.FromHours(2));
            var row = new ResourceRowViewModel(TestObjects.Pod("payments", "api-7f9", created: "2026-08-01T10:00:00Z"));
            row.RefreshTimes();
            await Assert.That(row.AgeText).IsEqualTo("3d");

            // A day later on the app's clock, a day older — whatever the machine's date is.
            AppClock.Time = new Stopped(created + TimeSpan.FromDays(4) + TimeSpan.FromHours(2));
            row.RefreshTimes();
            await Assert.That(row.AgeText).IsEqualTo("4d");
        }
        finally
        {
            AppClock.Time = previous;
        }
    }

    [Test]
    public async Task The_app_reads_the_system_clock_unless_replaced()
    {
        await Assert.That(AppClock.Time).IsSameReferenceAs(TimeProvider.System);
    }
}
