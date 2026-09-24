using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The Event readers behind the Events list's columns: when an event was last seen,
/// what it is about, and how often it happened — for both shapes an Event is served in
/// (core/v1 and <c>events.k8s.io/v1</c>, which renames most of the fields).
///
/// <para>
/// Every failure here is quiet. A Last seen that reads <c>eventTime</c> before a series'
/// <c>lastObservedTime</c> does not throw: it prints "47m" for a FailedScheduling that
/// fired two minutes ago, and the list sorts it below things that are older, which is
/// the exact question ("what just happened?") the list exists to answer.
/// </para>
/// </summary>
public class EventFieldsTests
{
    private static DynamicResource Event(string body, string apiVersion = "v1") =>
        new(JsonDocument.Parse($$"""
            {
              "apiVersion": "{{apiVersion}}",
              "kind": "Event",
              "metadata": { "name": "e.1", "namespace": "payments", "creationTimestamp": "2026-08-01T09:00:00Z" }
              {{(body.Length == 0 ? "" : "," + body)}}
            }
            """).RootElement.Clone());

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------- Last seen

    [Test]
    public async Task Last_seen_prefers_lastTimestamp_over_everything_else()
    {
        var e = Event("""
            "lastTimestamp": "2026-08-01T10:05:00Z",
            "eventTime": "2026-08-01T10:09:00.000000Z",
            "series": { "count": 3, "lastObservedTime": "2026-08-01T10:08:00.000000Z" },
            "firstTimestamp": "2026-08-01T10:00:00Z"
            """);

        await Assert.That(e.LastSeen()).IsEqualTo(At("2026-08-01T10:05:00Z"));
    }

    /// <summary>
    /// A series event written through the new Events API has no lastTimestamp, and its
    /// eventTime is the <em>first</em> occurrence. The series' own last observation is
    /// when it last happened — which is also what kubectl prints.
    /// </summary>
    [Test]
    public async Task Last_seen_reads_a_series_before_its_eventTime()
    {
        var e = Event("""
            "lastTimestamp": null,
            "eventTime": "2026-08-01T10:00:00.123456Z",
            "series": { "count": 14, "lastObservedTime": "2026-08-01T10:45:00.000000Z" }
            """);

        await Assert.That(e.LastSeen()).IsEqualTo(At("2026-08-01T10:45:00Z"));
    }

    [Test]
    public async Task Last_seen_falls_back_to_eventTime_then_firstTimestamp_then_creation()
    {
        await Assert.That(Event(""" "eventTime": "2026-08-01T10:00:00.5Z", "firstTimestamp": "2026-08-01T09:30:00Z" """).LastSeen())
            .IsEqualTo(At("2026-08-01T10:00:00.5Z"));
        await Assert.That(Event(""" "firstTimestamp": "2026-08-01T09:30:00Z" """).LastSeen())
            .IsEqualTo(At("2026-08-01T09:30:00Z"));
        await Assert.That(Event("").LastSeen()).IsEqualTo(At("2026-08-01T09:00:00Z"));
    }

    /// <summary>The events.k8s.io spellings of the same fields.</summary>
    [Test]
    public async Task Last_seen_reads_the_events_api_deprecated_timestamps()
    {
        var e = Event("""
            "deprecatedLastTimestamp": "2026-08-01T11:00:00Z",
            "deprecatedFirstTimestamp": "2026-08-01T10:00:00Z"
            """, apiVersion: "events.k8s.io/v1");

        await Assert.That(e.LastSeen()).IsEqualTo(At("2026-08-01T11:00:00Z"));
        await Assert.That(e.FirstSeen()).IsEqualTo(At("2026-08-01T10:00:00Z"));
    }

    /// <summary>
    /// metav1.Time's zero value is a real thing on the wire. It is "unset", not an event
    /// two thousand years old — and an event with nothing at all set is null, which the
    /// list renders as "—" and sorts last.
    /// </summary>
    [Test]
    public async Task A_zero_or_missing_timestamp_is_unset()
    {
        await Assert.That(Event(""" "lastTimestamp": "0001-01-01T00:00:00Z", "firstTimestamp": "2026-08-01T09:30:00Z" """).LastSeen())
            .IsEqualTo(At("2026-08-01T09:30:00Z"));

        var bare = new DynamicResource(JsonDocument.Parse("""
            { "apiVersion": "v1", "kind": "Event", "metadata": { "name": "e.2", "namespace": "payments" } }
            """).RootElement.Clone());
        await Assert.That(bare.LastSeen()).IsNull();
    }

    // ------------------------------------------------------------- Object

    [Test]
    public async Task Object_is_kind_slash_name()
    {
        var e = Event(""" "involvedObject": { "apiVersion": "v1", "kind": "Pod", "name": "checkout-worker-5d8f7b9c4-qz9pl", "namespace": "payments" } """);

        await Assert.That(e.ObjectText()).IsEqualTo("Pod/checkout-worker-5d8f7b9c4-qz9pl");
        await Assert.That(e.InvolvedObject()?.Name).IsEqualTo("checkout-worker-5d8f7b9c4-qz9pl");
    }

    /// <summary>events.k8s.io calls it <c>regarding</c>; double-click navigation reads it too.</summary>
    [Test]
    public async Task Object_reads_regarding_on_an_events_api_object()
    {
        var e = Event("""
            "regarding": { "apiVersion": "v1", "kind": "Pod", "name": "ledger-db-0", "namespace": "ledger" },
            "note": "0/3 nodes are available"
            """, apiVersion: "events.k8s.io/v1");

        await Assert.That(e.ObjectText()).IsEqualTo("Pod/ledger-db-0");
        await Assert.That(e.InvolvedObject()?.Kind).IsEqualTo("Pod");
        await Assert.That(e.InvolvedObjectNamespace()).IsEqualTo("ledger");
        await Assert.That(e.Message()).IsEqualTo("0/3 nodes are available");
    }

    [Test]
    public async Task Object_is_the_name_alone_without_a_kind_and_empty_without_an_object()
    {
        await Assert.That(Event(""" "involvedObject": { "name": "orphan" } """).ObjectText()).IsEqualTo("orphan");
        await Assert.That(Event(""" "involvedObject": { "name": "orphan" } """).InvolvedObject()).IsNull();
        await Assert.That(Event(""" "reason": "ScaleDown" """).ObjectText()).IsEqualTo("");
        await Assert.That(Event(""" "reason": "ScaleDown" """).InvolvedObject()).IsNull();
    }

    // ------------------------------------------------------------- Count and kind

    [Test]
    public async Task Occurrences_prefer_the_series_count_and_never_read_below_one()
    {
        await Assert.That(Event(""" "count": 3, "series": { "count": 14 } """).Occurrences()).IsEqualTo(14);
        await Assert.That(Event(""" "count": 42 """).Occurrences()).IsEqualTo(42);
        await Assert.That(Event(""" "deprecatedCount": 7 """, apiVersion: "events.k8s.io/v1").Occurrences()).IsEqualTo(7);
        await Assert.That(Event("").Occurrences()).IsEqualTo(1);
        await Assert.That(Event(""" "count": 0 """).Occurrences()).IsEqualTo(1);
    }

    [Test]
    public async Task Only_core_and_events_api_Events_are_events()
    {
        await Assert.That(Event("").IsEvent()).IsTrue();
        await Assert.That(Event("", apiVersion: "events.k8s.io/v1").IsEvent()).IsTrue();
        await Assert.That(Event("", apiVersion: "example.com/v1").IsEvent()).IsFalse();
        await Assert.That(ResourceDescriptor.Events.IsEventKind()).IsTrue();
        await Assert.That(EventFields.IsEventKind("audit.example.com", "Event")).IsFalse();
    }

    // ------------------------------------------------------------- against a real server

    /// <summary>
    /// The one check fixtures cannot make: the API server serves every Event under both
    /// groups, converting between the two shapes itself, so the same event read as
    /// core/v1 and as events.k8s.io/v1 must produce the same list cells. On the
    /// API-server-only sandbox the scheduler's FailedScheduling events are written through
    /// the new API (eventTime set, no lastTimestamp, no count), which is exactly the shape
    /// the fallback chain exists for.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Both_event_groups_read_the_same_against_a_real_server(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = await ClusterClient.ConnectAsync(context);
        var eventsApi = new ResourceDescriptor("events.k8s.io", "v1", "Event", "events", "event", true, [], []);

        var core = await client.ListResourceOnceAsync(ResourceDescriptor.Events, cancellationToken: ct);
        var modern = (await client.ListResourceOnceAsync(eventsApi, cancellationToken: ct))
            .ToDictionary(e => e.Key);

        await Assert.That(core.Count).IsGreaterThan(0);
        foreach (var e in core)
        {
            await Assert.That(e.IsEvent()).IsTrue();
            await Assert.That(e.LastSeen()).IsNotNull();
            await Assert.That(e.Occurrences()).IsGreaterThanOrEqualTo(1);

            if (modern.TryGetValue(e.Key, out var twin))
            {
                await Assert.That(twin.IsEvent()).IsTrue();
                await Assert.That(twin.ObjectText()).IsEqualTo(e.ObjectText());
                await Assert.That(twin.Message()).IsEqualTo(e.Message());
                await Assert.That(twin.Occurrences()).IsEqualTo(e.Occurrences());
                await Assert.That(twin.LastSeen()).IsEqualTo(e.LastSeen());
            }
        }
    }
}
