using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Events list reads like <c>kubectl get events</c>: newest first, one row per event
/// with its reason, object and message, and a search box that finds an event by what
/// happened rather than by the Event object's generated name.
///
/// <para>
/// These drive the real <see cref="ClusterTabViewModel.Apply"/>, the real kind-change hook
/// and the real <c>ToggleSort</c>. The default sort is the part worth pinning hardest:
/// it lives in the view model's kind hook and in the layout store's "no choice" value, and
/// both ways of getting it wrong render a perfectly sorted list on the first open — one
/// forgets the reader's "clear the sort" the next time the kind is opened, the other
/// sorts <c>Rows</c> and loses the informer's order (UI rule 13).
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> for the same reason as <c>ClusterTabSortTests</c>: the stored
/// layout is a real file behind a process-global directory override.
/// </para>
/// </summary>
[NotInParallel]
public class ClusterTabEventsListTests
{
    private static readonly ResourceDescriptor EventDescriptor = ResourceDescriptor.Events;

    private static readonly ResourceDescriptor EventsApiDescriptor =
        new("events.k8s.io", "v1", "Event", "events", "event", Namespaced: true, ShortNames: [], Categories: []);

    private static DynamicResource Event(
        string name,
        string reason,
        string? lastTimestamp,
        string type = "Normal",
        string message = "",
        string involvedKind = "Pod",
        string involvedName = "checkout-worker-0",
        int count = 1)
    {
        var involved = involvedName.Length == 0
            ? ""
            : $$""" "involvedObject": { "apiVersion": "v1", "kind": "{{involvedKind}}", "name": "{{involvedName}}", "namespace": "payments" }, """;
        var last = lastTimestamp is null ? "" : $$""" "lastTimestamp": "{{lastTimestamp}}", """;
        var json = $$"""
            {
              "apiVersion": "v1",
              "kind": "Event",
              "metadata": { "name": "{{name}}", "namespace": "payments", "uid": "{{name}}" },
              {{involved}}
              {{last}}
              "type": "{{type}}",
              "reason": "{{reason}}",
              "message": "{{JsonEncodedText.Encode(message)}}",
              "count": {{count}}
            }
            """;
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static ClusterTabViewModel EventTab(ResourceDescriptor? descriptor = null)
    {
        var tab = TestObjects.Tab();
        tab.SelectedKind = new SidebarKindViewModel(descriptor ?? EventDescriptor, "config");
        return tab;
    }

    /// <summary>Arrival order: middle, oldest, newest, and one with no timestamp.</summary>
    private static ClusterTabViewModel SeededEventTab()
    {
        var tab = EventTab();
        tab.Apply(TestObjects.Added(Event("e-middle", "Pulled", "2026-08-01T10:30:00Z")));
        tab.Apply(TestObjects.Added(Event("e-oldest", "Scheduled", "2026-08-01T10:00:00Z")));
        tab.Apply(TestObjects.Added(Event("e-newest", "BackOff", "2026-08-01T11:00:00Z", type: "Warning",
            message: "Back-off restarting failed container", count: 42)));
        tab.Apply(TestObjects.Added(Event("e-untimed", "ScaleDown", null, involvedName: "")));
        tab.Apply(ResourceEvent<DynamicResource>.Synced);
        return tab;
    }

    // ------------------------------------------------------------ the default sort

    /// <summary>
    /// Opening Events lands newest first, with an event carrying no timestamp at the end
    /// — and the watch's own list untouched underneath.
    /// </summary>
    [Test]
    public async Task Events_open_newest_first_and_the_watch_list_keeps_arrival_order()
    {
        var tab = SeededEventTab();

        await Assert.That(tab.SortColumnId).IsEqualTo(ResourceColumn.EventLastSeen);
        await Assert.That(tab.VisibleNames()).IsEqualTo("e-newest, e-middle, e-oldest, e-untimed");
        await Assert.That(tab.RowNames()).IsEqualTo("e-middle, e-oldest, e-newest, e-untimed");
    }

    /// <summary>The default is the Events list's alone; every other kind still opens in arrival order.</summary>
    [Test]
    public async Task Other_kinds_keep_arrival_order_by_default()
    {
        await Assert.That(ClusterTabViewModel.DefaultSortFor(TestObjects.PodDescriptor).Column).IsNull();
        await Assert.That(ClusterTabViewModel.DefaultSortFor(EventsApiDescriptor).Column).IsEqualTo(ResourceColumn.EventLastSeen);

        var tab = EventTab();
        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        await Assert.That(tab.SortColumnId).IsNull();
    }

    /// <summary>
    /// A cleared sort is a choice, and it has to survive leaving the kind and coming
    /// back — otherwise the default would silently re-apply itself. Choosing the default
    /// again is "no choice", and leaves nothing in the file.
    /// </summary>
    [Test]
    public async Task Clearing_the_default_sort_is_remembered_and_choosing_it_again_is_not_stored()
    {
        var tab = SeededEventTab();
        var key = GridLayoutStore.KeyFor(EventDescriptor);

        tab.ToggleSort(ResourceColumn.EventLastSeen); // → descending (oldest first)
        tab.ToggleSort(ResourceColumn.EventLastSeen); // → cleared
        await Assert.That(tab.SortColumnId).IsNull();
        await Assert.That(GridLayoutStore.Load(key).SortColumn).IsEqualTo(ResourceColumn.Unsorted);

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        tab.SelectedKind = new SidebarKindViewModel(EventDescriptor, "config");
        await Assert.That(tab.SortColumnId).IsNull();

        tab.ToggleSort(ResourceColumn.EventLastSeen); // → ascending, which is the default
        await Assert.That(GridLayoutStore.Load(key).SortColumn).IsNull();
    }

    /// <summary>A watch event arriving under the default sort lands where the sort puts it.</summary>
    [Test]
    public async Task A_new_event_is_inserted_in_last_seen_order()
    {
        var tab = SeededEventTab();
        tab.Apply(TestObjects.Added(Event("e-latest", "Killing", "2026-08-01T12:00:00Z")));

        await Assert.That(tab.VisibleNames()).IsEqualTo("e-latest, e-newest, e-middle, e-oldest, e-untimed");
    }

    [Test]
    public async Task Count_and_reason_sort_by_what_they_mean()
    {
        var tab = SeededEventTab();

        tab.ToggleSort(ResourceColumn.EventCount);
        tab.ToggleSort(ResourceColumn.EventCount);
        await Assert.That(tab.VisibleRows[0].Name).IsEqualTo("e-newest");

        tab.ToggleSort(ResourceColumn.EventReason);
        await Assert.That(tab.VisibleNames()).IsEqualTo("e-newest, e-middle, e-untimed, e-oldest");
    }

    // ------------------------------------------------------------ the search box

    /// <summary>
    /// Reason, Object and Message identify an event the way a name identifies a pod, so
    /// the search box matches them — and still not Type, which is status by another name.
    /// </summary>
    [Test]
    public async Task Search_matches_reason_object_and_message_but_not_type()
    {
        var tab = SeededEventTab();

        tab.RowFilter = "backoff";
        await Assert.That(tab.VisibleNames()).IsEqualTo("e-newest");

        tab.RowFilter = "restarting failed";
        await Assert.That(tab.VisibleNames()).IsEqualTo("e-newest");

        tab.RowFilter = "Pod/checkout";
        await Assert.That(tab.VisibleNames()).IsEqualTo("e-newest, e-middle, e-oldest");

        tab.RowFilter = "Warning";
        await Assert.That(tab.IsFilterEmpty).IsTrue();
    }

    /// <summary>The widening is the Events list's alone: a pod row matches its identity only.</summary>
    [Test]
    public async Task A_non_event_row_matches_on_identity_only()
    {
        var row = new ResourceRowViewModel(TestObjects.Pod("payments", "web-1"));

        await Assert.That(row.IsEvent).IsFalse();
        await Assert.That(row.Matches("web")).IsTrue();
        await Assert.That(row.Matches("Running")).IsFalse();
    }

    // ------------------------------------------------------------ the cells and states

    [Test]
    public async Task An_event_row_carries_kubectl_columns()
    {
        var row = new ResourceRowViewModel(Event("e", "BackOff", "2026-08-01T11:00:00Z", type: "Warning",
            message: "  Back-off restarting\n\tfailed   container  ", count: 42));

        await Assert.That(row.IsEvent).IsTrue();
        await Assert.That(row.EventType).IsEqualTo("Warning");
        await Assert.That(row.IsWarningEvent).IsTrue();
        await Assert.That(row.EventReason).IsEqualTo("BackOff");
        await Assert.That(row.EventObject).IsEqualTo("Pod/checkout-worker-0");
        await Assert.That(row.EventCount).IsEqualTo(42);
        await Assert.That(row.EventMessage).IsEqualTo("Back-off restarting failed container");
        await Assert.That(row.EventMessageTooltip).IsEqualTo("Back-off restarting\n\tfailed   container");
        await Assert.That(row.LastSeenTooltip).StartsWith("Last seen ");

        // Warning is what the unhealthy-only chip keeps (T1): the verdict still comes
        // from ResourceStatusSummary, whatever the list now draws instead of Status.
        await Assert.That(row.IsUnhealthy).IsTrue();
    }

    [Test]
    public async Task An_event_with_no_object_and_no_timestamp_says_so()
    {
        var row = new ResourceRowViewModel(Event("e", "ScaleDown", null, involvedName: ""));

        await Assert.That(row.EventObject).IsEqualTo("—");
        await Assert.That(row.EventObjectTooltip).IsEqualTo("This event names no object");
        await Assert.That(row.LastSeenText).IsEqualTo("—");
        await Assert.That(row.LastSeenTooltip).IsEqualTo("This event carries no timestamp");
        await Assert.That(row.IsWarningEvent).IsFalse();
        await Assert.That(row.IsUnhealthy).IsFalse();
    }

    [Test]
    public async Task The_events_kind_names_its_search_and_explains_an_empty_list()
    {
        var tab = EventTab(EventsApiDescriptor);

        await Assert.That(tab.IsEventList).IsTrue();
        await Assert.That(tab.RowFilterPlaceholder).IsEqualTo("Search reason, object, message…");
        await Assert.That(tab.EmptyListHint).Contains("expire");

        tab.SelectedKind = new SidebarKindViewModel(TestObjects.PodDescriptor, "workload");
        await Assert.That(tab.IsEventList).IsFalse();
        await Assert.That(tab.RowFilterPlaceholder).IsEqualTo("Search by name…");
        await Assert.That(tab.EmptyListHint).IsEqualTo("");
    }
}
