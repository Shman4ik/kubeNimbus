using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// UI rule 18 in the resource list: while the app is waiting for a list it must say so,
/// and it must never render a verdict it does not have yet.
///
/// <para>
/// The bug these exist to prevent was reported from a real, distant cluster and is worth
/// stating precisely, because it is invisible on a local one. The informer writes a
/// <see cref="ResourceEventType.Reset"/> <em>before</em> it issues the list request, and
/// the tab used to end its loading state on the first frame of any kind. So clicking
/// Pods produced: Reset (loading off, rows cleared) → <c>IsListEmpty</c> true → the "No
/// pods found" panel, for however long the list took to come back — a second or more
/// against a remote API server — and only then the rows. The two states had swapped
/// places: the app showed a confident answer while it was still asking the question.
/// </para>
///
/// <para>
/// Against a sandbox on localhost the gap is a few milliseconds and nothing is ever seen,
/// which is exactly why this needs a test rather than a screenshot: no rendered frame can
/// distinguish the two implementations, only the order of the frames can.
/// </para>
/// </summary>
public class ClusterTabLoadingStateTests
{
    private static ClusterTabViewModel LoadingTab()
    {
        var tab = TestObjects.Tab();

        // What RestartWatch does the moment a kind is selected and a client exists.
        tab.IsListLoading = true;
        return tab;
    }

    // ------------------------------------------------------- the reported bug

    /// <summary>
    /// The headline case. A Reset is the start of a list, so on its own it must leave the
    /// list waiting — never announce that the kind has nothing in it.
    /// </summary>
    [Test]
    public async Task A_reset_alone_keeps_the_list_loading_and_never_claims_it_is_empty()
    {
        var tab = LoadingTab();

        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        await Assert.That(tab.IsListLoading).IsTrue();
        await Assert.That(tab.IsListEmpty).IsFalse();
    }

    /// <summary>
    /// And the same on a relist. A 410 Gone mid-session re-issues Reset, so a tab that
    /// had settled has to go back to waiting rather than flashing its empty state.
    /// </summary>
    [Test]
    public async Task A_relist_returns_the_settled_list_to_waiting_rather_than_to_empty()
    {
        var tab = LoadingTab();
        tab.Apply(ResourceEvent<DynamicResource>.Reset);
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "api-7f9")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        await Assert.That(tab.IsListLoading).IsFalse();

        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        await Assert.That(tab.IsListLoading).IsTrue();
        await Assert.That(tab.IsListEmpty).IsFalse();
    }

    // --------------------------------------------------- the two honest endings

    /// <summary>
    /// An empty namespace produces a Reset and no Added at all, so Synced is the only
    /// frame that can ever settle it. Without it the spinner would never stop — which is
    /// the failure in the other direction and just as much a lie.
    /// </summary>
    [Test]
    public async Task Synced_with_no_rows_is_what_settles_an_empty_namespace()
    {
        var tab = LoadingTab();
        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        tab.Apply(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsTrue();
    }

    /// <summary>
    /// The other ending: the list paginates, so the first row is enough to stop waiting.
    /// Holding the overlay until the last page landed would hide a page of results behind
    /// a spinner, which is the same unresponsiveness wearing the opposite costume.
    /// </summary>
    [Test]
    public async Task The_first_row_ends_the_wait_without_waiting_for_the_last_page()
    {
        var tab = LoadingTab();
        tab.Apply(ResourceEvent<DynamicResource>.Reset);

        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "api-7f9")));

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.IsListEmpty).IsFalse();
        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9");
    }

    // ------------------------------------------------------------ fleet mode

    /// <summary>
    /// A fleet Reset is scoped to the member that sent it, and so is its meaning: the
    /// other clusters' rows are still on screen, so putting the whole list back into a
    /// loading state would cover four healthy lists because the fifth reconnected. This
    /// is the same reasoning that keeps a fleet Reset from clearing every row.
    /// </summary>
    [Test]
    public async Task A_fleet_member_relisting_does_not_put_the_whole_list_back_to_waiting()
    {
        var tab = LoadingTab();
        tab.ApplyFleet(new FleetResourceEvent("eu", TestObjects.Added(TestObjects.Pod("payments", "api-7f9"))));
        tab.ApplyFleet(new FleetResourceEvent("us", TestObjects.Added(TestObjects.Pod("payments", "api-7f9"))));
        await Assert.That(tab.IsListLoading).IsFalse();

        tab.ApplyFleet(new FleetResourceEvent("us", ResourceEvent<DynamicResource>.Reset));

        await Assert.That(tab.IsListLoading).IsFalse();
        await Assert.That(tab.RowNames()).IsEqualTo("api-7f9");
    }

    /// <summary>
    /// Partial is the normal state of a fleet view and the header already says how many
    /// clusters are in it, so the first member to finish its list is enough to stop
    /// waiting — the slowest member must not hold the others' rows off the screen.
    /// </summary>
    [Test]
    public async Task One_fleet_member_finishing_its_list_is_enough_to_stop_waiting()
    {
        var tab = LoadingTab();

        tab.ApplyFleet(new FleetResourceEvent("eu", ResourceEvent<DynamicResource>.Synced));

        await Assert.That(tab.IsListLoading).IsFalse();
    }
}
