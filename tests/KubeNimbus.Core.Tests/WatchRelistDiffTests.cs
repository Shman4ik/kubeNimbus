using System.Net;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A relist inside a live watch (410 Gone on the watch request, or an <c>ERROR</c> frame on the
/// stream) reaches the reader as a diff against what it already holds, never as a second Reset.
/// Before, every relist began with a Reset, on which every list cleared its rows, its selection
/// and its warning and showed the spinner: after a laptop sleep or a VPN drop longer than the
/// API server's watch cache keeps, the list blanked, and a 10 000-pod list delivered 10 000
/// <c>Added</c> again. And a relist the server asks for again straight away backs off, where it
/// used to be a tight loop of full lists.
/// </summary>
public class WatchRelistDiffTests
{
    private const string PodsPath = "/api/v1/namespaces/team/pods";

    private const string ExpiredFrame =
        """{"type":"ERROR","object":{"kind":"Status","apiVersion":"v1","status":"Failure","message":"too old resource version: 10 (40)","reason":"Expired","code":410}}""" + "\n";

    private static string PodList(string resourceVersion, params (string Name, string Version)[] pods)
    {
        var items = string.Join(",", pods.Select(p =>
            "{\"metadata\":{\"name\":\"" + p.Name + "\",\"namespace\":\"team\",\"resourceVersion\":\"" + p.Version + "\"}}"));
        return "{\"kind\":\"PodList\",\"apiVersion\":\"v1\",\"metadata\":{\"resourceVersion\":\"" + resourceVersion
            + "\"},\"items\":[" + items + "]}";
    }

    private static readonly string FirstList = PodList("10", ("a", "1"), ("b", "1"), ("c", "1"));

    // a unchanged, b changed, c gone, d new.
    private static readonly string SecondList = PodList("40", ("a", "1"), ("b", "2"), ("d", "3"));

    private static async Task<(List<string> Events, List<Exception> Warnings)> ReadUntilAsync(
        ClusterClient client, int synced, TimeSpan timeout)
    {
        var events = new List<string>();
        var warnings = new List<Exception>();
        using var cts = new CancellationTokenSource(timeout);
        var seen = 0;
        try
        {
            await foreach (var evt in client.WatchResourceAsync(ResourceDescriptor.Pods, "team", warnings.Add, cts.Token))
            {
                events.Add(evt.Resource is { } r ? $"{evt.Type} {r.Name}" : evt.Type.ToString());
                if (evt.Type == ResourceEventType.Synced && ++seen == synced)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        return (events, warnings);
    }

    private static readonly string[] Expected =
    [
        "Reset", "Added a", "Added b", "Added c", "Synced",
        "Modified b", "Added d", "Deleted c", "Synced",
    ];

    [Test]
    public async Task A_relist_after_an_ERROR_frame_is_a_diff_with_no_second_Reset()
    {
        var lists = 0;
        var watches = 0;
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (request.Path != PodsPath)
            {
                await response.NotFoundAsync();
            }
            else if (!request.IsWatch)
            {
                await response.WriteAsync(Interlocked.Increment(ref lists) == 1 ? FirstList : SecondList);
            }
            else if (Interlocked.Increment(ref watches) == 1)
            {
                await response.StreamAsync(ExpiredFrame);
            }
            else
            {
                await response.StreamAsync(_ => Task.CompletedTask);
            }
        });
        using var client = server.Connect();

        var (events, warnings) = await ReadUntilAsync(client, synced: 2, TimeSpan.FromSeconds(10));

        await Assert.That(events).IsEquivalentTo(Expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task A_relist_after_410_Gone_on_the_watch_request_is_a_diff_with_no_second_Reset()
    {
        var lists = 0;
        var watches = 0;
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (request.Path != PodsPath)
            {
                await response.NotFoundAsync();
            }
            else if (!request.IsWatch)
            {
                await response.WriteAsync(Interlocked.Increment(ref lists) == 1 ? FirstList : SecondList);
            }
            else if (Interlocked.Increment(ref watches) == 1)
            {
                await response.WriteAsync(
                    """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Expired","code":410}""", HttpStatusCode.Gone);
            }
            else
            {
                await response.StreamAsync(_ => Task.CompletedTask);
            }
        });
        using var client = server.Connect();

        var (events, warnings) = await ReadUntilAsync(client, synced: 2, TimeSpan.FromSeconds(10));

        await Assert.That(events).IsEquivalentTo(Expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task A_relist_with_nothing_changed_sends_only_Synced()
    {
        var watches = 0;
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (!request.IsWatch)
            {
                await response.WriteAsync(FirstList);
            }
            else if (Interlocked.Increment(ref watches) == 1)
            {
                await response.StreamAsync(ExpiredFrame);
            }
            else
            {
                await response.StreamAsync(_ => Task.CompletedTask);
            }
        });
        using var client = server.Connect();

        var (events, _) = await ReadUntilAsync(client, synced: 2, TimeSpan.FromSeconds(10));

        await Assert.That(events).IsEquivalentTo(
            new[] { "Reset", "Added a", "Added b", "Added c", "Synced", "Synced" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_watch_that_asks_for_a_relist_every_time_backs_off_instead_of_looping()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (!request.IsWatch)
            {
                await response.WriteAsync(FirstList);
            }
            else
            {
                // "Too large resource version" behind a load balancer: every watch fails at once.
                await response.StreamAsync(ExpiredFrame);
            }
        });
        using var client = server.Connect();

        // The first relist runs at once, the next after 1 s, the one after that after 2 more:
        // three lists in 2.5 s. With no pause it was hundreds.
        var (events, _) = await ReadUntilAsync(client, synced: int.MaxValue, TimeSpan.FromSeconds(2.5));

        var lists = server.Requests.Count(r => r is { Path: PodsPath, IsWatch: false });
        await Assert.That(lists).IsGreaterThanOrEqualTo(2);
        await Assert.That(lists).IsLessThanOrEqualTo(4);
        await Assert.That(events.Count(e => e == "Reset")).IsEqualTo(1);
    }
}
