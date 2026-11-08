using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Events tabs (pod, node, Service, Ingress detail) count and date an event the way the
/// Events list does. The 2026-11-08 desktop run found the pod tab printing "×0" and a UTC time
/// beside an event the list showed as 23 times at a local time: the tab read the raw
/// <c>count</c>, which a series and an events.k8s.io object do not carry, and printed the raw
/// timestamp.
/// </summary>
[NotInParallel(nameof(LogLineViewModel.ToLocal))]
public class EventRowCountTests
{
    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    [Test]
    public async Task A_series_is_counted_by_its_series_and_dated_by_its_last_observation()
    {
        var series = Parse("""
            {"apiVersion":"events.k8s.io/v1","kind":"Event","type":"Warning","reason":"BackOff",
             "metadata":{"name":"web-1.17f","namespace":"shop","creationTimestamp":"2026-11-08T15:00:00Z"},
             "eventTime":"2026-11-08T15:00:00.000000Z",
             "series":{"count":23,"lastObservedTime":"2026-11-08T16:00:00.000000Z"},
             "regarding":{"kind":"Pod","name":"web-1","namespace":"shop"}}
            """);

        var row = new EventRowViewModel(series);

        await Assert.That(row.Count).IsEqualTo(23);
        await Assert.That(row.LastSeen).IsEqualTo(new DateTimeOffset(2026, 11, 8, 16, 0, 0, TimeSpan.Zero));
        await Assert.That(row.Count).IsEqualTo(new ResourceRowViewModel(series).EventCount);
    }

    [Test]
    public async Task An_event_with_no_count_happened_once()
    {
        var single = Parse("""
            {"apiVersion":"events.k8s.io/v1","kind":"Event","type":"Normal","reason":"Scheduled",
             "metadata":{"name":"web-1.180","namespace":"shop"},
             "eventTime":"2026-11-08T15:00:00.000000Z"}
            """);

        await Assert.That(new EventRowViewModel(single).Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_time_is_local_with_its_offset_and_a_dash_when_there_is_none()
    {
        var previous = LocalTimeText.ToLocal;
        try
        {
            LocalTimeText.ToLocal = at => at.ToOffset(TimeSpan.FromHours(3));
            var dated = Parse("""
                {"apiVersion":"v1","kind":"Event","type":"Normal","reason":"Pulled","count":2,
                 "metadata":{"name":"web-1.181","namespace":"shop"},
                 "lastTimestamp":"2026-11-08T16:00:00Z"}
                """);
            var undated = Parse("""
                {"apiVersion":"v1","kind":"Event","type":"Normal","reason":"Pulled",
                 "metadata":{"name":"web-1.182","namespace":"shop"}}
                """);

            await Assert.That(new EventRowViewModel(dated).LastSeenText).IsEqualTo("2026-11-08 19:00:00 UTC+03:00");
            await Assert.That(new EventRowViewModel(undated).LastSeenText).IsEqualTo("—");
        }
        finally
        {
            LocalTimeText.ToLocal = previous;
        }
    }
}
