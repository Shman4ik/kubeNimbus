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

    [Test]
    public async Task Response_ready_waits_for_successful_headers_even_when_a_follow_has_no_lines()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        // One byte without a newline forces HttpListener to flush headers while
        // ReadLineAsync still waits for a complete log line.
        server.Respond("GET", Path, HttpStatusCode.OK, " ");
        server.RequestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReleaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReleaseBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = server.Connect();
        var ready = 0;
        var callbackSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var read = Task.Run(async () =>
        {
            await foreach (var _ in client.StreamPodLogsAsync("payments", "worker", "app",
                               follow: true, sinceSeconds: 300,
                               responseReady: () =>
                               {
                                   Interlocked.Increment(ref ready);
                                   callbackSeen.TrySetResult(true);
                               })) { }
        });

        try
        {
            await server.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(Volatile.Read(ref ready)).IsEqualTo(0);

            server.ReleaseResponse.SetResult(true);
            await callbackSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(Volatile.Read(ref ready)).IsEqualTo(1);
            await Assert.That(read.IsCompleted).IsFalse(); // answered, quiet follow stays open
        }
        finally
        {
            server.ReleaseResponse.TrySetResult(true);
            server.ReleaseBody.TrySetResult(true);
        }

        await read.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
