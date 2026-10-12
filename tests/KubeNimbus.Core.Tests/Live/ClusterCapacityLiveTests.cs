using System.Net;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// FEAT-25 (#206) against a real API server: the capacity summary reads the sandbox's real
/// nodes, metrics-server's real node usage and every pod, and under narrow RBAC the two reads
/// the strip can lose fail the way the strip expects them to — a 403 on the cluster-wide pod
/// list and a 403 on node metrics, each distinguishable from "no metrics API at all".
/// </summary>
public class ClusterCapacityLiveTests
{
    [Test]
    [Timeout(60_000)]
    public async Task The_sandbox_sums_to_real_usage_and_requests(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);

        var nodes = await client.ListResourceOnceAsync(LiveCluster.Nodes, cancellationToken: ct);
        var pods = await client.ListResourceOnceAsync(ResourceDescriptor.Pods, cancellationToken: ct);
        IReadOnlyList<NodeMetrics> usage = [];
        await LiveCluster.WaitUntilAsync(async () =>
        {
            usage = await client.GetNodeMetricsAsync(ct);
            return usage.Count > 0;
        }, TimeSpan.FromSeconds(45), "metrics-server to report a node", ct);

        var summary = ClusterCapacity.Summarize(
            nodes,
            name => usage.FirstOrDefault(m => m.Name == name) is { } m ? (m.CpuNanocores, m.MemoryBytes) : null,
            pods);
        Console.WriteLine(
            $"{summary.Nodes} nodes, {summary.Ready} Ready; cpu used {summary.Cpu.Used:0.###} of {summary.Cpu.Allocatable} "
            + $"({summary.Cpu.UsedPercent:0}%), requested {summary.Cpu.Requested:0.###} ({summary.Cpu.RequestedPercent:0}%); "
            + $"memory used {summary.Memory.UsedPercent:0}%, requested {summary.Memory.RequestedPercent:0}%");

        await Assert.That(summary.Nodes).IsGreaterThanOrEqualTo(1);
        await Assert.That(summary.Ready).IsGreaterThanOrEqualTo(1);
        await Assert.That(summary.Cpu.Allocatable!.Value).IsGreaterThan(0d);
        await Assert.That(summary.Cpu.Used!.Value).IsGreaterThan(0d);
        await Assert.That(summary.Memory.Used!.Value).IsGreaterThan(0d);

        // The sandbox's system pods (CoreDNS, metrics-server itself) declare requests.
        await Assert.That(summary.Cpu.Requested!.Value).IsGreaterThan(0d);
        await Assert.That(summary.Memory.Requested!.Value).IsGreaterThan(0d);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Under_narrow_rbac_the_pod_list_and_node_metrics_are_refused_not_absent(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        using var narrow = await LiveCluster.CreateNarrowUserAsync(admin, LiveCluster.Named("capacity-viewer"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [list]
            """, ct);

        // What the strip's pod read catches as "requests not permitted".
        var pods = await Assert.ThrowsAsync<KubernetesApiException>(
            () => narrow.Client.ListResourceOnceAsync(ResourceDescriptor.Pods, cancellationToken: ct));
        await Assert.That(pods!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(pods.ServerMessage).Contains("cannot list resource \"pods\"");

        // What the metrics poll reports as "usage not permitted": a 403, and not the
        // MetricsUnavailableException that would say the cluster has no metrics API.
        Exception? metrics = null;
        try
        {
            await narrow.Client.GetNodeMetricsAsync(ct);
        }
        catch (Exception ex)
        {
            metrics = ex;
        }

        await Assert.That(metrics is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }).IsTrue()
            .Because(metrics?.ToString() ?? "no exception");

        // And what the poll actually hands the strip (0.7.0 QA finding M-4): a refusal carrying
        // the server's own sentence, never a read with no node in it — which the strip words as
        // "no node has reported usage yet", a verdict it does not have.
        var read = await narrow.Client.ReadNodeUsageAsync(ct);
        await Assert.That(read.Outcome).IsEqualTo(NodeUsageOutcome.Refused);
        await Assert.That(read.Nodes).IsEmpty();
        await Assert.That(read.Reason).Contains("cannot list resource \"nodes\" in API group \"metrics.k8s.io\"");

        // The same client's admin twin reads real usage through the same call.
        var adminRead = await admin.ReadNodeUsageAsync(ct);
        await Assert.That(adminRead.Outcome).IsEqualTo(NodeUsageOutcome.Read);

        // The Pods list's poll (#310): a 403 on pod usage is the HttpRequestException with
        // Forbidden that the poll treats as an answer — hiding the columns and asking no more
        // — not the MetricsUnavailableException of a cluster with no metrics API, and not a
        // transient failure it would retry every interval.
        Exception? podMetrics = null;
        try
        {
            await narrow.Client.GetPodMetricsAsync(LiveCluster.Namespace, ct);
        }
        catch (Exception ex)
        {
            podMetrics = ex;
        }

        await Assert.That(podMetrics is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }).IsTrue()
            .Because(podMetrics?.ToString() ?? "no exception");
        await Assert.That(((KubernetesApiException)podMetrics!).ServerMessage)
            .Contains("cannot list resource \"pods\" in API group \"metrics.k8s.io\"");
    }
}
