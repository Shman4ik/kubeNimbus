using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #363: after a cut and a restore, the QA run's Service pane stayed on "Watch connection lost
/// (IOException); retrying in 16s." for minutes. The watch had failed twice in a row (the
/// second time while reconnecting, as an IOException mid-stream), and the retry after that
/// succeeded, but it resumed from the last resourceVersion on a quiet collection: no frame
/// ever arrived, and a reader clears its "connection lost" line on the next frame. The first
/// resumed watch the server answers after a loss is now followed by a <c>Synced</c>, and the
/// reader's rows are never reset for it: an outage keeps what is on screen.
/// </summary>
public class WatchReconnectTests
{
    /// <summary>
    /// Mutation-checked: without the <c>Synced</c> written when the resumed watch opens
    /// (<c>ClusterClient.PumpAsync</c>), the third watch is held open with nothing to say and no
    /// frame follows the second warning, so the wait below times out.
    /// </summary>
    [Test]
    public async Task A_watch_that_fails_twice_in_a_row_tells_its_reader_when_it_is_back()
    {
        using var server = new FailingWatchServer();
        using var client = await ClusterClient.ConnectAsync(server.Context());

        // What the reader sees, in order: a frame type, or "lost" for a connectionLost warning.
        var seen = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                                   ResourceDescriptor.Pods, "team", connectionLost: _ => seen.Enqueue("lost"),
                                   cancellationToken: stop.Token))
                {
                    seen.Enqueue(evt.Type.ToString());
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Two warnings, then a Synced after the second one.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!(seen.Count(s => s == "lost") >= 2 && seen.Skip(LastLost(seen) + 1).Contains(nameof(ResourceEventType.Synced))))
        {
            if (DateTime.UtcNow > deadline)
            {
                await stop.CancelAsync();
                await reading;
                throw new TimeoutException(
                    $"No Synced reached the reader after the second lost connection ({string.Join(", ", seen)}; {server.Watches} watches).");
            }

            await Task.Delay(50);
        }

        await stop.CancelAsync();
        await reading;

        // The outage reset nothing: one Reset, the first list's, and one list request.
        await Assert.That(seen.Count(s => s == nameof(ResourceEventType.Reset))).IsEqualTo(1);
        await Assert.That(server.Lists).IsEqualTo(1);
        await Assert.That(server.Watches).IsGreaterThanOrEqualTo(3);
    }

    private static int LastLost(ConcurrentQueue<string> seen)
    {
        var last = -1;
        var i = 0;
        foreach (var s in seen)
        {
            if (s == "lost")
            {
                last = i;
            }

            i++;
        }

        return last;
    }

    /// <summary>
    /// A raw socket stand-in for an API server, so a failure is a real one on every platform:
    /// the first watch connection is reset before an answer, the second is answered and then
    /// reset mid-stream, and every later one is answered and held open with nothing to say.
    /// <c>HttpListenerResponse.Abort()</c> did not reach the client as a failure on Linux.
    /// </summary>
    private sealed class FailingWatchServer : IDisposable
    {
        private const string EmptyPods = """{"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"7"},"items":[]}""";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _directory = Directory.CreateTempSubdirectory("kubenimbus-reconnect").FullName;
        private int _watches;
        private int _lists;

        public FailingWatchServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Watches => Volatile.Read(ref _watches);

        public int Lists => Volatile.Read(ref _lists);

        public ClusterContext Context()
        {
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var path = Path.Combine(_directory, "kubeconfig.yaml");
            File.WriteAllText(path, $"""
                apiVersion: v1
                kind: Config
                clusters:
                - name: stub
                  cluster:
                    server: http://127.0.0.1:{port}
                contexts:
                - name: stub
                  context:
                    cluster: stub
                    user: stub
                current-context: stub
                users:
                - name: stub
                  user:
                    token: not-a-credential
                """);
            return new ClusterContext("stub", "stub", null, "stub", path);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(socket));
            }
        }

        private async Task ServeAsync(Socket socket)
        {
            using var _ = socket;
            try
            {
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                while (await ReadRequestLineAsync(stream) is { } target)
                {
                    if (!target.Contains("watch=true", StringComparison.Ordinal))
                    {
                        var isVersion = target.StartsWith("/version", StringComparison.Ordinal);
                        if (!isVersion)
                        {
                            Interlocked.Increment(ref _lists);
                        }

                        var body = Encoding.UTF8.GetBytes(isVersion ? ScriptedApiServer.VersionBody : EmptyPods);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(body, _stop.Token);
                        continue;
                    }

                    var watch = Interlocked.Increment(ref _watches);
                    if (watch > 1)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }

                    if (watch <= 2)
                    {
                        // A reset, not a close: what a cut network looks like to the client.
                        await Task.Delay(50, _stop.Token);
                        socket.LingerState = new LingerOption(true, 0);
                        socket.Close();
                        return;
                    }

                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception)
            {
                // stopped, or the client went away
            }
        }

        /// <summary>The request target, after reading the rest of the head; null at the end of the connection.</summary>
        private async Task<string?> ReadRequestLineAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return null;
                }

                head.Append((char)one[0]);
            }

            return head.ToString().Split(' ')[1];
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
