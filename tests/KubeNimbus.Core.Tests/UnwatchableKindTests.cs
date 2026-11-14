using System.Net;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A kind discovery says cannot be watched (metrics.k8s.io's PodMetrics and NodeMetrics: get
/// and list only), and a list response with no resourceVersion, are listed once and held: a
/// Reset, the rows and a Synced, then nothing until the reader leaves. Before, the watch was
/// asked for anyway, failed on every attempt (a 405, or an <see cref="ArgumentNullException"/>
/// from escaping a null resourceVersion), and the pane said "Watch connection lost … retrying"
/// for ever.
/// </summary>
public class UnwatchableKindTests
{
    private static readonly ResourceDescriptor PodMetrics = new(
        Group: "metrics.k8s.io", Version: "v1beta1", Kind: "PodMetrics", Plural: "pods", SingularName: "",
        Namespaced: true, ShortNames: [], Categories: [])
    {
        Verbs = ["get", "list"],
    };

    private const string MetricsPath = "/apis/metrics.k8s.io/v1beta1/namespaces/team/pods";

    private const string MetricsList = """
        {"kind":"PodMetricsList","apiVersion":"metrics.k8s.io/v1beta1","metadata":{"resourceVersion":"9"},
         "items":[{"metadata":{"name":"web-1","namespace":"team"},"containers":[]}]}
        """;

    private static async Task<(List<ResourceEventType> Events, List<Exception> Warnings, Exception? Ended)> ReadForAsync(
        ClusterClient client, ResourceDescriptor descriptor, TimeSpan duration)
    {
        var events = new List<ResourceEventType>();
        var warnings = new List<Exception>();
        using var cts = new CancellationTokenSource(duration);
        try
        {
            await foreach (var evt in client.WatchResourceAsync(descriptor, "team", connectionLost: warnings.Add, cancellationToken: cts.Token))
            {
                events.Add(evt.Type);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return (events, warnings, null);
        }
        catch (Exception ex)
        {
            return (events, warnings, ex);
        }

        return (events, warnings, null);
    }

    /// <summary>
    /// Long enough for the first retry (one-second backoff) and a second one to have happened.
    /// </summary>
    private static readonly TimeSpan PastTwoRetries = TimeSpan.FromSeconds(3.5);

    [Test]
    public async Task A_kind_without_watch_lists_once_and_neither_retries_nor_reports_a_lost_connection()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (request.IsWatch)
            {
                await response.WriteAsync("""{"kind":"Status","code":405,"reason":"MethodNotAllowed"}""", HttpStatusCode.MethodNotAllowed);
            }
            else
            {
                await response.WriteAsync(MetricsList);
            }
        });
        using var client = server.Connect();

        var (events, warnings, ended) = await ReadForAsync(client, PodMetrics, PastTwoRetries);

        await Assert.That(ended).IsNull();
        await Assert.That(events).IsEquivalentTo(
            new[] { ResourceEventType.Reset, ResourceEventType.Added, ResourceEventType.Synced }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(warnings).IsEmpty();
        await Assert.That(server.Requests.Count(r => r is { Path: MetricsPath, IsWatch: false })).IsEqualTo(1);
        await Assert.That(server.Requests.Count(r => r.IsWatch)).IsEqualTo(0);
    }

    [Test]
    public async Task A_list_with_no_resource_version_is_held_rather_than_thrown_on()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
            await response.WriteAsync("""{"kind":"PodList","apiVersion":"v1","metadata":{},"items":[]}"""));
        using var client = server.Connect();

        // Verbs not known: the watch would be allowed, but there is nothing to resume from.
        var (events, warnings, ended) = await ReadForAsync(client, ResourceDescriptor.Pods, PastTwoRetries);

        await Assert.That(ended).IsNull();
        await Assert.That(events).IsEquivalentTo(
            new[] { ResourceEventType.Reset, ResourceEventType.Synced }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(warnings).IsEmpty();
        await Assert.That(server.Requests.Count(r => r is { Path: "/api/v1/namespaces/team/pods", IsWatch: false })).IsEqualTo(1);
        await Assert.That(server.Requests.Count(r => r.IsWatch)).IsEqualTo(0);
    }

    [Test]
    public async Task A_held_shared_entry_is_relisted_by_Refresh()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
            await response.WriteAsync(MetricsList));
        using var client = server.Connect();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var synced = 0;
        var warnings = new List<Exception>();
        await foreach (var evt in client.Watches.WatchAsync(PodMetrics, "team", warnings.Add, cts.Token))
        {
            if (evt.Type == ResourceEventType.Synced && ++synced == 1)
            {
                client.Watches.Relist(PodMetrics, "team");
            }
            else if (synced == 2)
            {
                break;
            }
        }

        await Assert.That(synced).IsEqualTo(2);
        await Assert.That(warnings).IsEmpty();
        await Assert.That(server.Requests.Count(r => r is { Path: MetricsPath, IsWatch: false })).IsEqualTo(2);
        await Assert.That(server.Requests.Count(r => r.IsWatch)).IsEqualTo(0);
    }
}
