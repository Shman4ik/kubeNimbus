using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-55 (#252): a metrics poll that lands before the list's rows exist used to be applied
/// to no rows and kept nowhere, so every row read "—" until the next poll, up to an interval
/// (15 s) later. The last poll of the current watch is now kept, in memory, and a row that
/// arrives after it takes its sample; a new watch drops it.
/// </summary>
[NotInParallel]
public class MetricsLastSampleTests
{
    [Test]
    public async Task A_row_that_arrives_after_a_poll_takes_its_sample()
    {
        var tab = TestObjects.Tab();
        var pod = TestObjects.Pod("payments", "checkout-0");
        tab.ApplyUsage(new() { [pod.Key] = (250_000_000, 64L * 1024 * 1024) });

        tab.Apply(TestObjects.Added(pod));
        tab.ApplyBatch([TestObjects.Added(TestObjects.Pod("payments", "checkout-1"))]);

        var rows = tab.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["checkout-0"].CpuText).IsNotEqualTo("—");
        await Assert.That(rows["checkout-0"].MemoryText).IsNotEqualTo("—");

        // A row the poll had nothing for stays "—" until a poll does.
        await Assert.That(rows["checkout-1"].CpuText).IsEqualTo("—");
    }

    [Test]
    public async Task A_new_watch_drops_the_last_poll()
    {
        var tab = TestObjects.Tab();
        var pod = TestObjects.Pod("payments", "checkout-0");
        tab.ApplyUsage(new() { [pod.Key] = (250_000_000, 64L * 1024 * 1024) });

        // Changing the namespace restarts the watch, and with it the poll.
        tab.SelectedNamespace = "ledger";
        tab.Apply(TestObjects.Added(pod));

        await Assert.That(tab.Rows.Single().CpuText).IsEqualTo("—");
    }

    [Test]
    public async Task A_poll_that_finishes_after_its_watch_was_cancelled_is_not_kept()
    {
        var tab = TestObjects.Tab();
        var pod = TestObjects.Pod("payments", "checkout-0");

        tab.ApplyUsage(new() { [pod.Key] = (250_000_000, 64L * 1024 * 1024) }, new CancellationToken(canceled: true));
        tab.Apply(TestObjects.Added(pod));

        await Assert.That(tab.Rows.Single().CpuText).IsEqualTo("—");
    }
}
