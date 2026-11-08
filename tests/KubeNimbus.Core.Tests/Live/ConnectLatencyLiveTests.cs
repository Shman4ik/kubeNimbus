using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// The connect path's requests over a link with a round-trip time, through a TCP relay
/// that delays every byte and passes TLS through untouched, so the client and the
/// sandbox's API server negotiate HTTP/2 or HTTP/1.1 between themselves as they would
/// across a real network. The relay counts the TCP connections the client opens.
/// <para>
/// <see cref="ConnectLikeTheAppAsync"/> is <c>ClusterTabViewModel.ConnectAsync</c>'s order
/// with the Applications mode's start beside it: the client is built (the kubeconfig read,
/// the CA, any exec plugin), then <c>/version</c>, the namespace list and the Helm probe go
/// out together. Once <c>/version</c> answers, discovery and the metrics probe start, and so
/// do the Applications mode's seven watches, which do not wait for discovery. The sidebar is
/// discovery and the Helm probe; the first pod list follows the namespace list. Its phases
/// are the ones <c>KUBENIMBUS_STARTUP_TRACE</c> writes from the real app
/// (<c>docs/engineering/startup-performance.md</c>).
/// </para>
/// </summary>
public partial class ConnectLatencyLiveTests
{
    /// <summary>The Applications mode's kinds (<c>ApplicationsViewModel.WatchedKinds</c>), at the versions every supported cluster serves.</summary>
    private static readonly ResourceDescriptor[] ApplicationKinds =
    [
        new("apps", "v1", "Deployment", "deployments", "deployment", true, [], []),
        new("apps", "v1", "StatefulSet", "statefulsets", "statefulset", true, [], []),
        new("apps", "v1", "DaemonSet", "daemonsets", "daemonset", true, [], []),
        new("batch", "v1", "CronJob", "cronjobs", "cronjob", true, [], []),
        new("batch", "v1", "Job", "jobs", "job", true, [], []),
        new("apps", "v1", "ReplicaSet", "replicasets", "replicaset", true, [], []),
        ResourceDescriptor.Pods,
    ];

    /// <summary>
    /// Under HTTP/2 the whole connect, its seven open watches included, travels on the one
    /// connection <c>/version</c> opened. Under HTTP/1.1 (<c>DISABLE_HTTP2</c>) every
    /// request in flight opened a connection of its own; that is kept as the comparison,
    /// and as proof that the switch still works.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_connect_travels_on_one_connection_under_http2(CancellationToken ct)
    {
        var context = await SandboxContextAsync();
        using var directory = new TempDirectory("kubenimbus-live-latency");

        var http2 = await MeasureAsync(context, directory.Path, HttpVersion.Version20, TimeSpan.Zero, ct);
        var http11 = await MeasureAsync(context, directory.Path, HttpVersion.Version11, TimeSpan.Zero, ct);
        Console.WriteLine($"HTTP/2: {http2.Connections} connection(s), answered as HTTP/{http2.Negotiated}");
        Console.WriteLine($"HTTP/1.1: {http11.Connections} connection(s), answered as HTTP/{http11.Negotiated}");

        await Assert.That(http2.Negotiated).IsEqualTo(HttpVersion.Version20);
        await Assert.That(http2.Connections).IsEqualTo(1);
        await Assert.That(http11.Negotiated).IsEqualTo(HttpVersion.Version11);
        await Assert.That(http11.Connections).IsGreaterThanOrEqualTo(ApplicationKinds.Length);
    }

    /// <summary>
    /// The numbers on <c>docs/engineering/startup-performance.md</c>: each phase's median over
    /// five cold connects (no discovery cache), for three round-trip times and both HTTP
    /// versions. Explicit: it takes a minute and asserts nothing; run it with
    /// <c>--treenode-filter "/*/*/ConnectLatencyLiveTests/Connect_phases_by_round_trip_time"</c>.
    /// </summary>
    [Test]
    [Explicit]
    [Timeout(600_000)]
    public async Task Connect_phases_by_round_trip_time(CancellationToken ct)
    {
        var context = await SandboxContextAsync();
        using var directory = new TempDirectory("kubenimbus-live-latency");
        const int runs = 5;

        var table = new StringBuilder();
        table.AppendLine("| RTT | HTTP | connections | credentials | version | namespaces | catalog | sidebar | resources list | applications read |");
        table.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var rtt in new[] { 0, 50, 150 })
        {
            foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
            {
                var samples = new List<Measurement>();
                await MeasureAsync(context, directory.Path, version, TimeSpan.FromMilliseconds(rtt), ct); // warm-up
                for (var i = 0; i < runs; i++)
                {
                    samples.Add(await MeasureAsync(context, directory.Path, version, TimeSpan.FromMilliseconds(rtt), ct));
                }

                string Median(string phase) => samples.Select(s => s.Phases[phase]).Order().ElementAt(runs / 2)
                    .TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
                table.AppendLine(CultureInfo.InvariantCulture,
                    $"| {rtt} ms | {version} | {samples.Select(s => s.Connections).Max()} | {Median("credentials")} | {Median("version")} | {Median("namespaces")} | {Median("catalog")} | {Median("sidebar")} | {Median("resources list")} | {Median("applications read")} |");
            }
        }

        Console.WriteLine(table);
    }

    private sealed record Measurement(IReadOnlyDictionary<string, TimeSpan> Phases, int Connections, Version Negotiated);

    private static async Task<ClusterContext> SandboxContextAsync()
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            Skip.Test("no sandbox cluster");
            throw new UnreachableException();
        }

        return context;
    }

    /// <summary>One cold connect of <paramref name="context"/> through a fresh relay adding <paramref name="roundTrip"/>.</summary>
    private static async Task<Measurement> MeasureAsync(
        ClusterContext context, string directory, Version version, TimeSpan roundTrip, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(context.KubeconfigPath, ct);
        using var relay = new DelayingTcpRelay(new Uri(ServerLine().Match(text).Groups[1].Value), roundTrip);

        // The admin kubeconfig holds a client key: it lives in the test's own folder, deleted with it.
        var path = Path.Combine(directory, $"kubeconfig-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, ServerLine().Replace(text, $"server: https://127.0.0.1:{relay.Port}"), ct);
        try
        {
            var cache = Path.Combine(directory, "discovery-" + Guid.NewGuid().ToString("N"));
            var (phases, negotiated) = await ConnectLikeTheAppAsync(context with { KubeconfigPath = path }, version, cache, ct);
            return new Measurement(phases, relay.Connections, negotiated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The app's connect order (see the class remarks), timed from before the client is built.</summary>
    private static async Task<(Dictionary<string, TimeSpan> Phases, Version Negotiated)> ConnectLikeTheAppAsync(
        ClusterContext context, Version version, string discoveryCache, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var phases = new Dictionary<string, TimeSpan>();
        void Mark(string phase)
        {
            lock (phases)
            {
                phases[phase] = clock.Elapsed;
            }
        }

        using var client = await ClusterClient.ConnectAsync(context, ct);
        client.RequestVersion = version;
        client.DiscoveryCacheDirectory = discoveryCache;
        Mark("credentials");

        var namespaces = client.ListResourceOnceAsync(ResourceDescriptor.Namespaces, cancellationToken: ct);
        var helm = client.HasHelmReleasesAsync(ct);
        Version negotiated;
        using (var response = await client.SendRequestAsync(HttpMethod.Get, "version", null, HttpCompletionOption.ResponseContentRead, ct))
        {
            response.EnsureSuccessStatusCode();
            negotiated = response.Version;
        }

        Mark("version");

        using var watches = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var catalog = client.GetResourceCatalogAsync(ct);
        var metrics = client.IsMetricsApiAvailableAsync(ct);

        var sidebar = Task.Run(async () =>
        {
            await catalog;
            Mark("catalog");
            await helm;
            await metrics;
            Mark("sidebar");
        }, ct);

        var openWatches = new List<Task>();
        var applications = Task.Run(async () =>
        {
            var synced = ApplicationKinds.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            for (var i = 0; i < ApplicationKinds.Length; i++)
            {
                openWatches.Add(HoldWatchAsync(client, ApplicationKinds[i], synced[i], watches.Token));
            }

            await Task.WhenAll(synced.Select(s => s.Task));
            Mark("applications read");
        }, ct);

        var resources = Task.Run(async () =>
        {
            await namespaces;
            Mark("namespaces");
            await client.ListResourceOnceAsync(ResourceDescriptor.Pods, cancellationToken: ct);
            Mark("resources list");
        }, ct);

        await Task.WhenAll(sidebar, applications, resources);
        await watches.CancelAsync();
        await Task.WhenAll(openWatches);
        return (phases, negotiated);
    }

    /// <summary>A watch kept open, as the Applications mode keeps it, after it reports its list complete.</summary>
    private static async Task HoldWatchAsync(
        ClusterClient client, ResourceDescriptor kind, TaskCompletionSource synced, CancellationToken ct)
    {
        try
        {
            await foreach (var change in client.WatchResourceAsync(kind, cancellationToken: ct))
            {
                if (change.Type == ResourceEventType.Synced)
                {
                    synced.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            synced.TrySetException(e);
        }
    }

    [GeneratedRegex(@"server: (\S+)")]
    private static partial Regex ServerLine();
}

/// <summary>
/// A loopback TCP relay to an upstream address that delays every byte by half of
/// <paramref name="roundTrip"/> in each direction, and each new connection by a whole one
/// (the TCP handshake it stands in for). It never looks inside the bytes, so TLS, ALPN and
/// whichever HTTP version the two ends agree on pass through unchanged.
/// </summary>
internal sealed class DelayingTcpRelay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _roundTrip;
    private int _connections;

    public DelayingTcpRelay(Uri upstream, TimeSpan roundTrip)
    {
        _host = upstream.Host;
        _port = upstream.Port;
        _roundTrip = roundTrip;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>TCP connections the client opened through the relay.</summary>
    public int Connections => Volatile.Read(ref _connections);

    public void Dispose()
    {
        // Not disposed: relay tasks still unwinding read its token.
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient downstream;
            try
            {
                downstream = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => RelayAsync(downstream));
        }
    }

    private async Task RelayAsync(TcpClient downstream)
    {
        using (downstream)
        using (var upstream = new TcpClient { NoDelay = true })
        {
            try
            {
                downstream.NoDelay = true;
                await upstream.ConnectAsync(_host, _port, _stop.Token);
                if (_roundTrip > TimeSpan.Zero)
                {
                    await Task.Delay(_roundTrip, _stop.Token);
                }

                var up = PumpAsync(downstream.GetStream(), upstream.Client);
                var down = PumpAsync(upstream.GetStream(), downstream.Client);
                await Task.WhenAny(up, down);
                await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or TimeoutException)
            {
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="from"/> to <paramref name="to"/>, each chunk released half a round
    /// trip after it arrived, in order. Never throws: a connection the client or the server
    /// closed, or the relay being stopped, simply ends the copy.
    /// </summary>
    private async Task PumpAsync(Stream from, Socket to)
    {
        var oneWay = _roundTrip / 2;
        var chunks = Channel.CreateUnbounded<(byte[] Bytes, long Due)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var (bytes, due) in chunks.Reader.ReadAllAsync(_stop.Token))
                {
                    var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _stop.Token);
                    }

                    await to.SendAsync(bytes, SocketFlags.None, _stop.Token);
                }

                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        });

        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token)) > 0)
            {
                var due = Stopwatch.GetTimestamp() + (long)(oneWay.TotalSeconds * Stopwatch.Frequency);
                chunks.Writer.TryWrite((buffer[..read], due));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            chunks.Writer.TryComplete();
        }

        await writer;
    }
}
