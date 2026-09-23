using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

public class LogRangeTests
{
    [Test]
    public async Task Each_choice_maps_to_one_request_window()
    {
        await Assert.That(LogRange.Choices.Select(r => (r.TailLines, r.SinceSeconds)).SequenceEqual(
            [(200, null), (1000, null), (null, 300), (null, 3600), (null, 86400), (null, null)]))
            .IsTrue();
        await Assert.That(LogRange.FiveMinutes.EmptyMessage).IsEqualTo("No lines in the last 5 minutes.");
    }

    [Test]
    public async Task Multi_pod_line_ranges_share_the_panes_buffer_but_time_ranges_do_not_truncate_the_request()
    {
        await Assert.That(LogRange.Last200.TailForPod(4000, 40)).IsEqualTo(100);
        await Assert.That(LogRange.Last1000.TailForPod(4000, 4)).IsEqualTo(1000);
        await Assert.That(LogRange.Last1000.TailForPod(4000, 8)).IsEqualTo(500);
        await Assert.That(LogRange.FiveMinutes.TailForPod(4000, 8)).IsNull();
        await Assert.That(LogRange.Everything.TailForPod(4000, 8)).IsNull();
    }
}
