using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #363 in the resource list: through an outage the rows and the selection stay where they are,
/// and once the resumed watch is answered (a <c>Synced</c> with no <c>Reset</c> before it,
/// <c>WatchReconnectTests</c> on the Core side) the "connection lost; retrying" line goes, on a
/// quiet list too.
/// </summary>
public class ClusterTabReconnectTests
{
    private const string Lost = "Watch connection lost (IOException); retrying in 16s.";

    private static ClusterTabViewModel ListedTab()
    {
        var tab = TestObjects.Tab();
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.Apply(TestObjects.Added(TestObjects.Pod("team", "web-1")));
        tab.Apply(TestObjects.Added(TestObjects.Pod("team", "web-2")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        tab.SelectedRow = tab.Rows[1];
        return tab;
    }

    /// <summary>
    /// Mutation-checked: without the clearing in the Synced case of <c>ApplyOne</c>, the line
    /// stays after the reconnect.
    /// </summary>
    [Test]
    public async Task A_reconnect_keeps_the_rows_and_the_selection_and_clears_the_retry_line()
    {
        var tab = ListedTab();
        var rows = tab.Rows.ToList();
        var selected = tab.SelectedRow;

        tab.OnListConnectionLost(new WatchConnectionException(Lost, new IOException()));
        await Assert.That(tab.ConnectionWarning).IsEqualTo(Lost);
        await Assert.That(tab.Rows.SequenceEqual(rows)).IsTrue();

        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.ConnectionWarning).IsNull();
        await Assert.That(tab.Rows.SequenceEqual(rows)).IsTrue();
        await Assert.That(tab.SelectedRow).IsSameReferenceAs(selected);
        await Assert.That(tab.IsListLoading).IsFalse();
    }

    [Test]
    public async Task A_reconnect_clears_only_the_watchs_own_line()
    {
        var tab = ListedTab();
        tab.OnListConnectionLost(new WatchConnectionException(Lost, new IOException()));

        // Something else said its piece after the watch's line; the reconnect is not its answer.
        tab.ConnectionWarning = "Could not refresh the resource catalog: timed out";
        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.ConnectionWarning).IsEqualTo("Could not refresh the resource catalog: timed out");
    }

    /// <summary>
    /// #386, the fleet half: two members lose their watch, one resumes (its <c>Synced</c> with no
    /// <c>Reset</c> before it). That member's line goes and the other member's, still retrying,
    /// is the line on screen; once that one resumes too, the line is empty. Mutation-checked:
    /// without <c>ClearFleetMemberLostWarning</c> in the fleet Synced case, east's line stays.
    /// </summary>
    [Test]
    public async Task A_fleet_members_reconnect_clears_its_line_and_keeps_the_others()
    {
        var tab = TestObjects.Tab();
        tab.ApplyFleetBatch(
        [
            new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Reset),
            new FleetResourceEvent("east", TestObjects.Added(TestObjects.Pod("team", "web-1"))),
            new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Synced),
            new FleetResourceEvent("west", ResourceEvent<DynamicResource>.Reset),
            new FleetResourceEvent("west", TestObjects.Added(TestObjects.Pod("team", "web-2"))),
            new FleetResourceEvent("west", ResourceEvent<DynamicResource>.Synced),
        ]);

        tab.OnFleetMemberConnectionLost("west", new WatchConnectionException(Lost, new IOException()));
        tab.OnFleetMemberConnectionLost("east", new WatchConnectionException(Lost, new IOException()));
        await Assert.That(tab.ConnectionWarning).IsEqualTo($"east: {Lost}");
        await Assert.That(tab.ConnectionWarningOffersReconnect).IsTrue();

        tab.ApplyFleetBatch([new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Synced)]);

        await Assert.That(tab.ConnectionWarning).IsEqualTo($"west: {Lost}");
        await Assert.That(tab.ConnectionWarningOffersReconnect).IsTrue();
        await Assert.That(tab.Rows.Count).IsEqualTo(2);

        tab.ApplyFleetBatch([new FleetResourceEvent("west", ResourceEvent<DynamicResource>.Synced)]);
        await Assert.That(tab.ConnectionWarning).IsNull();
    }

    /// <summary>
    /// A member's resume is not the answer to another member's line: west resumes while east's
    /// line is on screen, and east's line stays.
    /// </summary>
    [Test]
    public async Task A_fleet_members_reconnect_leaves_another_members_line_on_screen()
    {
        var tab = TestObjects.Tab();
        tab.OnFleetMemberConnectionLost("west", new WatchConnectionException(Lost, new IOException()));
        tab.OnFleetMemberConnectionLost("east", new WatchConnectionException(Lost, new IOException()));

        tab.ApplyFleetBatch([new FleetResourceEvent("west", ResourceEvent<DynamicResource>.Synced)]);

        await Assert.That(tab.ConnectionWarning).IsEqualTo($"east: {Lost}");

        // And east relisting from scratch (a 410 Gone) clears its own line too.
        tab.ApplyFleetBatch([new FleetResourceEvent("east", ResourceEvent<DynamicResource>.Reset)]);
        await Assert.That(tab.ConnectionWarning).IsNull();
    }
}
