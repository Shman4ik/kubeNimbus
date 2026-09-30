using System.Collections.Specialized;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The watch now reaches the list in batches (<see cref="AsyncBatching.InBatches"/>), and
/// <c>ApplyBatch</c> appends a batch's new rows in one notification. What has to hold is
/// that a batch has exactly the effect of its events applied one at a time — the same rows,
/// the same objects, the same visible list through a filter and a sort — while the grid
/// hears about an initial list once instead of once per object.
/// </summary>
[NotInParallel]
public class ClusterTabBatchApplyTests
{
    private static List<ResourceEvent<DynamicResource>> Events()
    {
        var events = new List<ResourceEvent<DynamicResource>> { ResourceEvent<DynamicResource>.Reset };
        for (var i = 0; i < 300; i++)
        {
            events.Add(TestObjects.Added(TestObjects.Pod(i % 3 == 0 ? "shop" : "payments", $"pod-{i:D3}", restarts: i % 7)));
        }

        // A Modified and a Delete in the middle of the burst, of rows the same batch added.
        events.Add(TestObjects.Modified(TestObjects.Pod("payments", "pod-001", restarts: 40)));
        events.Add(TestObjects.Deleted(TestObjects.Pod("payments", "pod-002")));
        for (var i = 300; i < 320; i++)
        {
            events.Add(TestObjects.Added(TestObjects.Pod("payments", $"pod-{i:D3}")));
        }

        events.Add(ResourceEvent<DynamicResource>.Synced);
        return events;
    }

    [Test]
    public async Task A_batch_has_the_effect_of_its_events_one_at_a_time()
    {
        var oneByOne = TestObjects.Tab();
        oneByOne.RowFilter = "pod-0";
        oneByOne.ToggleSort(ResourceColumn.Restarts);
        foreach (var evt in Events())
        {
            oneByOne.Apply(evt);
        }

        var batched = TestObjects.Tab();
        batched.RowFilter = "pod-0";
        batched.ToggleSort(ResourceColumn.Restarts);
        batched.ApplyBatch(Events());

        await Assert.That(batched.RowNames()).IsEqualTo(oneByOne.RowNames());
        await Assert.That(batched.VisibleNames()).IsEqualTo(oneByOne.VisibleNames());
        await Assert.That(batched.Rows.Single(r => r.Name == "pod-001").Restarts).IsEqualTo(40);
        await Assert.That(batched.IsListLoading).IsFalse();
        await Assert.That(batched.VisibleRows.All(row => batched.Rows.Contains(row))).IsTrue();
    }

    [Test]
    public async Task An_initial_list_reaches_the_grid_in_a_few_notifications()
    {
        var tab = TestObjects.Tab();
        var notifications = 0;
        tab.VisibleRows.CollectionChanged += (_, _) => notifications++;

        var events = new List<ResourceEvent<DynamicResource>> { ResourceEvent<DynamicResource>.Reset };
        for (var i = 0; i < 5_000; i++)
        {
            events.Add(TestObjects.Added(TestObjects.Pod("payments", $"pod-{i:D4}")));
        }

        events.Add(ResourceEvent<DynamicResource>.Synced);
        tab.ApplyBatch(events);

        await Assert.That(tab.VisibleRows.Count).IsEqualTo(5_000);
        await Assert.That(notifications).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task A_fleet_members_relist_clears_its_rows_in_one_notification()
    {
        var tab = TestObjects.Tab();
        var events = new List<FleetResourceEvent>();
        for (var i = 0; i < 2_000; i++)
        {
            events.Add(new FleetResourceEvent(i % 2 == 0 ? "east" : "west", TestObjects.Added(TestObjects.Pod("payments", $"pod-{i:D4}"))));
        }

        tab.ApplyFleetBatch(events);
        var west = tab.Rows.Where(r => r.ClusterName == "west").ToList();

        var actions = new List<NotifyCollectionChangedAction>();
        tab.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        tab.ApplyFleet(new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Reset));

        await Assert.That(actions.Count).IsEqualTo(1);
        await Assert.That(tab.Rows.ToList()).IsEquivalentTo(west);
        await Assert.That(tab.VisibleRows.Count).IsEqualTo(1_000);
    }
}
