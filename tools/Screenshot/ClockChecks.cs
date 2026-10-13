using Avalonia.Controls;
using Avalonia.VisualTree;
using KubeNimbus.App;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The harness's clock: stopped at one instant. <c>AppClock.Time</c> is set to it before the
/// first scenario builds, so every age the app shows is read against <see cref="DemoData.Now"/>
/// and two runs of one commit render the same PNGs whatever the day (#350). The zone is not the
/// clock's business: local times go through <c>LocalTimeText.ToLocal</c>, which the harness sets
/// to UTC (#346, <see cref="LocalZoneCheck"/>).
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Every resource row a scenario rendered shows the age the fixed clock gives it. A row whose
/// Age was read from the wall clock instead (a time read that bypassed <c>AppClock</c>, which
/// <c>BannedSymbols.Clock.txt</c> stops at build time in the app and the harness but not in
/// Core) shows the dataset's age plus however many days the binary is old, and fails here. The
/// check also fails a run in which a scenario replaced the harness's clock and left it so.
/// </summary>
internal static class ClockChecks
{
    private static readonly List<string> Failures = [];
    private static int _probed;

    internal static void Walk(Window window, string name)
    {
        if (AppClock.Time is not FixedClock || AppClock.UtcNow != DemoData.Now)
        {
            Failures.Add($"{name}: AppClock is not the harness's fixed clock at DemoData.Now");
            return;
        }

        foreach (var row in window.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is not ResourceRowViewModel { CreatedAt: { } created } resource)
            {
                continue;
            }

            _probed++;
            var expected = RelativeTime.Compact(DemoData.Now - created);
            if (resource.AgeText != expected)
            {
                Failures.Add($"{name}: {resource.Name} shows Age \"{resource.AgeText}\", the fixed clock gives \"{expected}\"");
            }
        }
    }

    internal static void ThrowIfAnyFailed(bool filtered)
    {
        Console.WriteLine($"Ages checked against the fixed clock: {_probed}, failures: {Failures.Count}.");
        if (Failures.Count > 0)
        {
            throw new InvalidOperationException(
                "A rendered age did not come from the harness's fixed clock (#350):" + Environment.NewLine
                + string.Join(Environment.NewLine, Failures.Distinct()));
        }

        if (!filtered && _probed < ShardPlan.Current.Floor(100))
        {
            throw new InvalidOperationException($"Only {_probed} rows' ages were checked; the walk is not finding them.");
        }
    }
}
