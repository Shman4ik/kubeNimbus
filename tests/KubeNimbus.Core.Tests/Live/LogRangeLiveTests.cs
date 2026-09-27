using System.Globalization;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-43: T3's log ranges against a real kubelet. <c>LogRequestHttpTests</c> pins the
/// query string each range sends; these check what a real log endpoint does with it —
/// that a line range is exactly that many of the newest lines, that a time range leaves
/// out what is older, that a quiet pod answers "no lines" rather than hanging, that the
/// previous container honours the same range, and that a follow's headers arrive before
/// its first line (the delayed-header case T3 was built around).
/// </summary>
public class LogRangeLiveTests
{
    /// <summary>A burst of 500 numbered lines at start-up, then one line every half second.</summary>
    private const string Chatty =
        "i=0; while [ $i -lt 500 ]; do echo \"burst $i\"; i=$((i+1)); done; "
        + "trap 'exit 0' TERM; j=0; while true; do echo \"tick $j\"; j=$((j+1)); sleep 0.5 & wait $!; done";

    [Test]
    [Timeout(120_000)]
    public async Task Line_and_time_ranges_select_what_they_say_on_a_running_pod(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("chatty");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name, LiveCluster.DeploymentYaml(name, 1, Chatty), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        var pod = (await LiveCluster.PodsOfAsync(client, name, ct)).Single().Name;

        // Past the burst, so a short time range can exclude all of it.
        await Task.Delay(TimeSpan.FromSeconds(6), ct);

        var everything = await ReadAsync(client, pod, tail: null, since: null, ct);
        await Assert.That(everything.Count).IsGreaterThan(505);
        await Assert.That(everything[0]).IsEqualTo("burst 0");

        // "Last 200 lines": exactly 200, and they are the newest — the end of what
        // "Everything" returned a moment ago is inside them, and they run on from it.
        var last200 = await ReadAsync(client, pod, tail: 200, since: null, ct);
        await Assert.That(last200.Count).IsEqualTo(200);
        var overlap = everything.IndexOf(last200[0]);
        await Assert.That(overlap).IsGreaterThan(0);
        await Assert.That(last200.Take(everything.Count - overlap).SequenceEqual(everything.Skip(overlap))).IsTrue();

        // A time range leaves out what is older than it: the burst is six seconds old.
        var lastFewSeconds = await ReadAsync(client, pod, tail: null, since: 3, ct, timestamps: true);
        await Assert.That(lastFewSeconds.Count).IsGreaterThan(0);
        await Assert.That(lastFewSeconds.Count).IsLessThan(20);
        await Assert.That(lastFewSeconds.Any(l => l.Contains(" burst ", StringComparison.Ordinal))).IsFalse();
        var stamps = lastFewSeconds.Select(l => DateTimeOffset.Parse(l[..l.IndexOf(' ')], CultureInfo.InvariantCulture)).ToList();
        await Assert.That((stamps[^1] - stamps[0]).TotalSeconds).IsLessThanOrEqualTo(4);

        // A line range while following: the backfill, then new lines keep arriving.
        var followed = new List<string>();
        var ready = false;
        using (var follow = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            follow.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await foreach (var line in client.StreamPodLogsAsync(
                                   LiveCluster.Namespace, pod, follow: true, tailLines: 5,
                                   responseReady: () => ready = true, cancellationToken: follow.Token))
                {
                    followed.Add(line);
                    if (followed.Count == 9)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // reported below as too few lines
            }
        }

        await Assert.That(ready).IsTrue();
        await Assert.That(followed.Count).IsEqualTo(9);
        var ticks = followed.Select(l => int.Parse(l["tick ".Length..], CultureInfo.InvariantCulture)).ToList();
        await Assert.That(ticks.SequenceEqual(Enumerable.Range(ticks[0], 9))).IsTrue();
    }

    /// <summary>
    /// A quiet pod: a time range with nothing in it is answered — the request completes
    /// with no lines — and a follow over the same range gets its headers (the pane's
    /// "following, nothing yet" state) without a line to carry them.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_quiet_pod_answers_an_empty_range_and_a_follow_is_answered_before_its_first_line(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("quiet");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, "echo hello; trap 'exit 0' TERM; sleep 3600 & wait $!"), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        var pod = (await LiveCluster.PodsOfAsync(client, name, ct)).Single().Name;
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var ready = false;
        var empty = await ReadAsync(client, pod, tail: null, since: 2, ct, onReady: () => ready = true);
        await Assert.That(empty).IsEmpty();
        await Assert.That(ready).IsTrue();

        // Everything still has the one line: the range, not the pod, was empty.
        await Assert.That(await ReadAsync(client, pod, tail: null, since: null, ct)).IsEquivalentTo(new[] { "hello" });

        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.StreamPodLogsAsync(
                                   LiveCluster.Namespace, pod, follow: true, sinceSeconds: 2,
                                   responseReady: () => headers.TrySetResult(), cancellationToken: follow.Token))
                {
                    received++;
                }
            }
            catch (OperationCanceledException)
            {
                // ended by the test
            }
        }, follow.Token);

        await headers.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await follow.CancelAsync();
        await reading;
        await Assert.That(received).IsEqualTo(0);
    }

    /// <summary>
    /// The previous container honours the same ranges. The container prints fifty numbered
    /// lines and exits non-zero, so after its first restart "previous" is a known, finished
    /// run to measure against.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task Previous_container_logs_use_the_same_range(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("crasher");
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
                  command: ["sh", "-c", "i=1; while [ $i -le 50 ]; do echo \"line $i\"; i=$((i+1)); done; sleep 2; exit 1"]
            """, ct);

        await LiveCluster.WaitUntilAsync(async () =>
        {
            var pod = await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct);
            return PodDetails.ContainerRunOf(pod!.Raw, "app") is { RestartCount: >= 1 };
        }, TimeSpan.FromSeconds(120), "the container's first restart", ct);

        var lastTen = await ReadAsync(client, name, tail: 10, since: null, ct, previous: true);
        await Assert.That(string.Join('|', lastTen))
            .IsEqualTo(string.Join('|', Enumerable.Range(41, 10).Select(i => $"line {i}")));

        var lastHour = await ReadAsync(client, name, tail: null, since: 3600, ct, previous: true);
        await Assert.That(lastHour.Count).IsEqualTo(50);
        await Assert.That(lastHour[0]).IsEqualTo("line 1");
    }

    private static async Task<List<string>> ReadAsync(
        ClusterClient client, string pod, int? tail, int? since, CancellationToken ct,
        bool timestamps = false, bool previous = false, Action? onReady = null)
    {
        var lines = new List<string>();
        await foreach (var line in client.StreamPodLogsAsync(
                           LiveCluster.Namespace, pod, follow: false, tailLines: tail, sinceSeconds: since,
                           previous: previous, timestamps: timestamps, responseReady: onReady, cancellationToken: ct))
        {
            lines.Add(line);
        }

        return lines;
    }
}
