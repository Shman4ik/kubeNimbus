using System.Globalization;
using System.Text.RegularExpressions;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-41: the Events list's harder cases against a real cluster that runs pods — a live
/// <b>series</b> event read through both Event groups, and a kubelet's counted events read
/// through both. The reference for "reads like <c>kubectl get events</c>" is the API
/// server's own Table for the list, which is what kubectl prints.
/// </summary>
public partial class EventsLiveTests
{
    /// <summary>
    /// The scheduler writes <c>FailedScheduling</c> through the new Events API, as a series:
    /// <c>eventTime</c> is the first occurrence and <c>series.lastObservedTime</c> the latest.
    /// Last seen must be the latest, in both groups, and match kubectl's LAST SEEN; the
    /// count is the series count. The sandbox's <c>demo-broken/unschedulable</c> pod keeps
    /// one alive (read-only).
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_live_series_event_reads_its_last_occurrence_in_both_event_groups(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var series = (await client.ListResourceOnceAsync(LiveCluster.EventsV1, cancellationToken: ct))
            .Where(e => e.Raw.TryGetProperty("series", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Take(5)
            .ToList();
        if (series.Count == 0)
        {
            Skip.Test("no event on the cluster is a series right now (the scheduler writes one for an unschedulable pod after its second attempt)");
        }

        foreach (var modern in series)
        {
            var s = modern.Raw.GetProperty("series");
            var lastObserved = DateTimeOffset.Parse(s.GetProperty("lastObservedTime").GetString()!, CultureInfo.InvariantCulture);
            var first = DateTimeOffset.Parse(modern.Raw.GetProperty("eventTime").GetString()!, CultureInfo.InvariantCulture);
            var count = s.GetProperty("count").GetInt32();

            await Assert.That(modern.LastSeen()).IsEqualTo(lastObserved);
            await Assert.That(modern.FirstSeen()).IsEqualTo(first);
            await Assert.That(modern.Occurrences()).IsEqualTo(count);

            // The same object through core/v1.
            var legacy = await client.ReadResourceAsync(ResourceDescriptor.Events, modern.Namespace, modern.Name, ct);
            await Assert.That(legacy).IsNotNull();
            await Assert.That(legacy!.LastSeen()).IsEqualTo(lastObserved);
            await Assert.That(legacy.Occurrences()).IsEqualTo(count);
            await Assert.That(legacy.Message()).IsEqualTo(modern.Message());
            await Assert.That(legacy.Reason()).IsEqualTo(modern.Reason());
            await Assert.That(legacy.ObjectText()).IsEqualTo(modern.ObjectText());
            await Assert.That(legacy.InvolvedObject()).IsEqualTo(modern.InvolvedObject());

            await AssertMatchesKubectlAsync(client, legacy, ct);
        }
    }

    /// <summary>
    /// The kubelet writes core/v1 events with a count that it increments in place — a
    /// readiness probe that fails every second is the reliable way to get one. Read through
    /// both groups (<c>count</c>/<c>lastTimestamp</c> on one, <c>deprecatedCount</c>/
    /// <c>deprecatedLastTimestamp</c> on the other), found by the pod's own UID the way pod
    /// detail's Events tab finds them, and compared with kubectl.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_kubelets_counted_event_reads_the_same_in_both_groups_and_as_kubectl_prints_it(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("unhealthy");
        await LiveCluster.ApplyAsync(client, ResourceDescriptor.Pods, name, $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              terminationGracePeriodSeconds: 1
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "trap 'exit 0' TERM; sleep 3600 & wait $!"]
                  readinessProbe:
                    exec:
                      command: ["cat", "/tmp/ready"]
                    periodSeconds: 1
            """, ct);

        var pod = (await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct))!;
        DynamicResource? unhealthy = null;
        await LiveCluster.WaitUntilAsync(async () =>
        {
            unhealthy = (await client.GetEventsForAsync(pod, ct)).FirstOrDefault(e => e.Reason() == "Unhealthy" && e.Occurrences() >= 3);
            return unhealthy is not null;
        }, TimeSpan.FromSeconds(90), "a repeated Unhealthy event for the pod", ct);

        var legacy = unhealthy!;
        await Assert.That(legacy.Message()).StartsWith("Readiness probe failed:");
        await Assert.That(legacy.InvolvedObject()!.Uid).IsEqualTo(pod.Uid);
        await Assert.That(legacy.ObjectText()).IsEqualTo($"Pod/{name}");
        await Assert.That(legacy.Type()).IsEqualTo("Warning");
        await Assert.That(legacy.LastSeen()!.Value).IsGreaterThan(legacy.FirstSeen()!.Value);

        var modern = await client.ReadResourceAsync(LiveCluster.EventsV1, legacy.Namespace, legacy.Name, ct);
        await Assert.That(modern).IsNotNull();
        await Assert.That(modern!.Raw.TryGetProperty("deprecatedCount", out _)).IsTrue();
        await Assert.That(modern.Message()).IsEqualTo(legacy.Message());
        await Assert.That(modern.ObjectText()).IsEqualTo(legacy.ObjectText());
        await Assert.That(modern.InvolvedObject()).IsEqualTo(legacy.InvolvedObject());
        await Assert.That(modern.InvolvedObjectNamespace()).IsEqualTo(LiveCluster.Namespace);

        // The count and time can have moved on between the two reads; both groups are one
        // object, so they move together — re-read the core one after the modern one and
        // accept either reading.
        var legacyAgain = (await client.ReadResourceAsync(ResourceDescriptor.Events, legacy.Namespace, legacy.Name, ct))!;
        await Assert.That(modern.Occurrences() == legacy.Occurrences() || modern.Occurrences() == legacyAgain.Occurrences()).IsTrue();
        await Assert.That(modern.LastSeen() == legacy.LastSeen() || modern.LastSeen() == legacyAgain.LastSeen()).IsTrue();

        await AssertMatchesKubectlAsync(client, legacyAgain, ct);
    }

    /// <summary>
    /// One event's row in the server's Table for its namespace's core/v1 Events — what
    /// <c>kubectl get events</c> prints — against the app's reading of the same event.
    /// Last Seen is compared as a time (the app prints one unit where kubectl prints two);
    /// OBJECT case-insensitively, because kubectl lower-cases the kind (<c>pod/x</c>) and
    /// the app does not (<c>Pod/x</c>).
    /// </summary>
    private static async Task AssertMatchesKubectlAsync(ClusterClient client, DynamicResource e, CancellationToken ct)
    {
        using var table = await LiveCluster.GetTableAsync(client, ResourceDescriptor.Events.CollectionPath(e.Namespace), ct);
        var row = table.Rows.FirstOrDefault(r => r.Key == $"{e.Namespace}/{e.Name}").Cells;
        if (row is null)
        {
            return; // expired between the two reads
        }

        string Cell(string column) => ServerTable.Text(row[table.IndexOf(column)]);

        await Assert.That(Cell("Type")).IsEqualTo(e.Type());
        await Assert.That(Cell("Reason")).IsEqualTo(e.Reason());
        // kubectl trims the message; the kubelet's probe output ends with a newline. The
        // list trims too (ResourceRowViewModel), so this is the text the row shows.
        await Assert.That(Cell("Message")).IsEqualTo(e.Message().Trim());
        await Assert.That(Cell("Object")).IsEqualTo(e.ObjectText()).IgnoringCase();

        // Count is one of kubectl's wide columns; for a series it is the series count.
        var count = int.Parse(Cell("Count"), CultureInfo.InvariantCulture);
        await Assert.That(Math.Abs(count - e.Occurrences())).IsLessThanOrEqualTo(1);

        var lastSeen = Cell("Last Seen");
        var appAge = DateTimeOffset.UtcNow - e.LastSeen()!.Value;
        await Assert.That(KubectlAge.Agrees(lastSeen, appAge)).IsTrue()
            .Because($"kubectl LAST SEEN \"{lastSeen}\" and the app's {appAge} should be the same time");
    }
}

/// <summary>
/// kubectl's <c>duration.HumanDuration</c>, read back. It truncates, and to a unit that
/// grows with the age: seconds below two minutes, then minutes, then whole hours from 8h to
/// 48h ("40h"), whole days from 8 days. So "40h" is any age from 40h to just under 41h, and
/// a comparison that allowed a minute either side failed for most of every hour once the
/// sandbox's series event was that old (ENG-60). The tolerance is the unit kubectl printed.
/// </summary>
internal static partial class KubectlAge
{
    /// <summary>Clock skew between this machine and the API server, and the time between the two reads.</summary>
    internal static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"(\d+)([ydhms])")]
    private static partial Regex DurationPart();

    private static TimeSpan UnitOf(string unit) => unit switch
    {
        "y" => TimeSpan.FromDays(365),
        "d" => TimeSpan.FromDays(1),
        "h" => TimeSpan.FromHours(1),
        "m" => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromSeconds(1),
    };

    /// <summary>The age as printed: "3d2h" is 3 days and 2 hours.</summary>
    internal static TimeSpan Parse(string text) =>
        DurationPart().Matches(text).Aggregate(TimeSpan.Zero, (sum, p) =>
            sum + UnitOf(p.Groups[2].Value) * int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture));

    /// <summary>The smallest unit printed, which is how much a truncated reading can be short by.</summary>
    internal static TimeSpan SmallestUnit(string text) =>
        DurationPart().Matches(text).Select(p => UnitOf(p.Groups[2].Value)).DefaultIfEmpty(TimeSpan.FromSeconds(1)).Min();

    /// <summary>
    /// Whether an exact age reads as <paramref name="printed"/>: no less than what kubectl
    /// printed, and short of it plus one of its smallest unit, give or take <see cref="Slack"/>.
    /// </summary>
    internal static bool Agrees(string printed, TimeSpan actual)
    {
        var floor = Parse(printed);
        return actual >= floor - Slack && actual < floor + SmallestUnit(printed) + Slack;
    }
}

/// <summary>The tolerance <see cref="KubectlAge"/> applies, pinned without a cluster.</summary>
public class KubectlAgeTests
{
    [Test]
    public async Task Whole_hours_between_8h_and_48h_cover_the_whole_hour()
    {
        await Assert.That(KubectlAge.Agrees("40h", TimeSpan.FromHours(40) + TimeSpan.FromMinutes(59))).IsTrue();
        await Assert.That(KubectlAge.Agrees("40h", TimeSpan.FromHours(40))).IsTrue();
        await Assert.That(KubectlAge.Agrees("40h", TimeSpan.FromHours(41) + TimeSpan.FromMinutes(1))).IsFalse();
        await Assert.That(KubectlAge.Agrees("40h", TimeSpan.FromHours(39) + TimeSpan.FromMinutes(50))).IsFalse();
    }

    [Test]
    public async Task Mixed_units_are_as_precise_as_their_smallest_part()
    {
        await Assert.That(KubectlAge.Agrees("3h12m", new TimeSpan(3, 12, 40))).IsTrue();
        await Assert.That(KubectlAge.Agrees("3h12m", new TimeSpan(3, 14, 0))).IsFalse();
        await Assert.That(KubectlAge.Agrees("5m3s", new TimeSpan(0, 5, 4))).IsTrue();
        await Assert.That(KubectlAge.Agrees("5m3s", new TimeSpan(0, 5, 30))).IsFalse();
        await Assert.That(KubectlAge.Agrees("10d", TimeSpan.FromDays(10.9))).IsTrue();
        await Assert.That(KubectlAge.Agrees("10d", TimeSpan.FromDays(11.1))).IsFalse();
    }
}
