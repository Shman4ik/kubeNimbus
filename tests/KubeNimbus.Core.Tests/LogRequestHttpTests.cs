using System.Net;

namespace KubeNimbus.Core.Tests;

/// <summary>Pin log range parameters on the actual HTTP request, including previous snapshots.</summary>
public class LogRequestHttpTests
{
    private const string Path = "/api/v1/namespaces/payments/pods/worker/log";

    [Test]
    public async Task Time_range_sends_sinceSeconds_without_a_tail_limit()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("GET", Path, HttpStatusCode.OK, "first\nsecond\n");
        using var client = server.Connect();

        var lines = new List<string>();
        await foreach (var line in client.StreamPodLogsAsync("payments", "worker", "app",
                           follow: false, sinceSeconds: 300, timestamps: true))
        {
            lines.Add(line);
        }

        var query = server.Requests.Single().Query;
        await Assert.That(query).Contains("sinceSeconds=300");
        await Assert.That(query).DoesNotContain("tailLines=");
        await Assert.That(query).Contains("follow=false");
        await Assert.That(lines.SequenceEqual(["first", "second"])).IsTrue();
    }

    [Test]
    public async Task Previous_line_range_sends_tail_and_previous()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("GET", Path, HttpStatusCode.OK, "old\n");
        using var client = server.Connect();

        await foreach (var _ in client.StreamPodLogsAsync("payments", "worker", "app",
                           follow: false, tailLines: 1000, previous: true)) { }

        var query = server.Requests.Single().Query;
        await Assert.That(query).Contains("tailLines=1000");
        await Assert.That(query).Contains("previous=true");
        await Assert.That(query).DoesNotContain("sinceSeconds=");
    }

    [Test]
    public async Task Everything_omits_both_range_parameters()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("GET", Path, HttpStatusCode.OK, "retained\n");
        using var client = server.Connect();

        await foreach (var _ in client.StreamPodLogsAsync("payments", "worker", "app", follow: false)) { }

        var query = server.Requests.Single().Query;
        await Assert.That(query).DoesNotContain("tailLines=");
        await Assert.That(query).DoesNotContain("sinceSeconds=");
    }
}
