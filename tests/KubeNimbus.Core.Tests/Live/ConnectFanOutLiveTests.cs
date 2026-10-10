using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-37 (#220), the cluster half: the 2026-09-22 fewer-clicks pass against a real API
/// server. Connect reads <c>/version</c> first and alone — the request that runs an exec
/// credential plugin — and only then fans out discovery, the namespace list, the metrics
/// probe and the first pod list together (<c>ClusterTabViewModel.ConnectAsync</c>). These
/// drive that same sequence of Core calls and observe it from the outside: how often the
/// plugin ran, and how many discovery requests a loopback proxy in front of the real server
/// saw in flight at once.
/// </summary>
public partial class ConnectFanOutLiveTests
{
    private static readonly ResourceDescriptor Crds = new(
        "apiextensions.k8s.io", "v1", "CustomResourceDefinition", "customresourcedefinitions",
        "customresourcedefinition", false, ["crd"], []);

    /// <summary>
    /// A kubeconfig whose user is an exec plugin (a script that counts its runs and prints a
    /// real ServiceAccount token): the connect's fan-out never runs the plugin beyond what
    /// <c>/version</c> did, a Reconnect (a forced credential refresh) costs no more than a
    /// connect, and every connect costs the same. "Once per connect" does not hold: see the
    /// assertion on <c>afterVersion</c>.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task An_exec_plugin_runs_the_same_number_of_times_per_connect_whatever_the_fan_out(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        using var narrow = await LiveCluster.CreateNarrowUserAsync(admin, LiveCluster.Named("plugin-user"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [get, list, watch]
            """, ct);
        var text = await File.ReadAllTextAsync(narrow.KubeconfigPath, ct);
        var plugin = CountingPlugin.Write(TokenLine().Match(text).Groups[1].Value);
        var path = Path.Combine(Path.GetDirectoryName(plugin.Path)!, "kubeconfig.yaml");
        await File.WriteAllTextAsync(path, TokenLine().Replace(text,
            $"exec:\n        apiVersion: client.authentication.k8s.io/v1\n        interactiveMode: Never\n        command: '{plugin.Path.Replace("'", "''")}'"), ct);
        var context = new ClusterContext(narrow.Client.Context.Name, "sandbox", LiveCluster.Namespace, "plugin", path);

        int perConnect;
        using (var client = await ClusterClient.ConnectAsync(context, ct))
        {
            var afterBuild = plugin.Runs;
            await client.GetServerVersionAsync(ct);
            var afterVersion = plugin.Runs;
            await ConnectLikeTheAppAsync(client, ct);
            Console.WriteLine($"plugin runs: {afterBuild} building the client, {afterVersion} after /version, {plugin.Runs} after the fan-out");

            // The fan-out reuses the token /version got: discovery, namespaces, the metrics
            // probe and the pod list together run the plugin no more times.
            await Assert.That(plugin.Runs).IsEqualTo(afterVersion);

            // Observed on k3s v1.33.4 with KubernetesClient.Aot 19.0.2: the plugin runs
            // TWICE per connect — once when the library builds the configuration, and again
            // on /version, because the token provider it installs does not start from the
            // credential the build already obtained. Pinned at its observed value so a
            // change either way is noticed; filed for the backlog (see the PR).
            await Assert.That(afterBuild).IsEqualTo(1);
            await Assert.That(afterVersion).IsEqualTo(2);
            perConnect = afterVersion;

            // Reconnect (a forced re-read of the kubeconfig) runs it again, once per run of
            // the build-then-request sequence, and never per pane.
            var beforeRefresh = plugin.Runs;
            await client.RefreshCredentialsAsync(force: true, ct);
            await client.GetServerVersionAsync(ct);
            await ConnectLikeTheAppAsync(client, ct);
            await Assert.That(plugin.Runs - beforeRefresh).IsLessThanOrEqualTo(perConnect);
        }

        var beforeSecond = plugin.Runs;
        using (var again = await ClusterClient.ConnectAsync(context, ct))
        {
            await ConnectLikeTheAppAsync(again, ct);
            await Assert.That(plugin.Runs - beforeSecond).IsEqualTo(perConnect);
        }
    }

    /// <summary>
    /// A cluster serving 40+ API groups (the sandbox's own, cert-manager's when installed,
    /// and throwaway CRDs in groups of this run's own, deleted afterwards), read through a
    /// loopback proxy that adds 100 ms to every request — a distant cluster. Discovery
    /// returns every group either way, and the requests overlap: the aggregated path's two
    /// requests go out together, and the legacy path (forced by the proxy answering as a
    /// pre-1.30 server would, with no aggregated discovery) fans its per-group requests out
    /// many at a time and never more than the bound.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task Discovery_of_forty_groups_overlaps_its_requests_on_both_paths(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        var groups = Enumerable.Range(0, 14).Select(i => $"g{i:D2}-{LiveCluster.RunId}.live.kubenimbus.io").ToList();
        try
        {
            foreach (var group in groups)
            {
                await admin.ApplyYamlAsync(Crds, null, $"probes.{group}", CrdYaml(group), LiveCluster.FieldManager, cancellationToken: ct);
            }

            await LiveCluster.WaitUntilAsync(async () =>
            {
                var catalog = await admin.DiscoverResourcesAsync(ct);
                return groups.All(g => catalog.Any(d => d.Group == g));
            }, TimeSpan.FromSeconds(60), "the throwaway groups to be served", ct);

            using var narrow = await LiveCluster.CreateNarrowUserAsync(admin, LiveCluster.Named("discoverer"), """
                - apiGroups: [""]
                  resources: [pods]
                  verbs: [list]
                """, ct);
            var text = await File.ReadAllTextAsync(narrow.KubeconfigPath, ct);
            var upstream = new Uri(ServerLine().Match(text).Groups[1].Value);

            foreach (var legacy in new[] { false, true })
            {
                using var proxy = new ObservingProxy(upstream, legacy);
                var path = Path.Combine(Directory.CreateTempSubdirectory("kubenimbus-live-proxy").FullName, "kubeconfig.yaml");
                await File.WriteAllTextAsync(path, ServerLine().Replace(
                    CaLine().Replace(text, ""), $"server: {proxy.Url}"), ct);
                using var client = await ClusterClient.ConnectAsync(
                    new ClusterContext(narrow.Client.Context.Name, "sandbox", LiveCluster.Namespace, "proxied", path), ct);

                var catalog = await client.DiscoverResourcesAsync(ct);
                var served = catalog.Select(d => d.Group).Distinct().ToList();
                Console.WriteLine($"legacy={legacy}: {served.Count} groups, {proxy.Requests} requests, at most {proxy.MaxInFlight} in flight");

                await Assert.That(served.Count).IsGreaterThanOrEqualTo(40);
                await Assert.That(groups.All(served.Contains)).IsTrue();
                if (legacy)
                {
                    await Assert.That(proxy.Requests).IsGreaterThanOrEqualTo(served.Count);
                    await Assert.That(proxy.MaxInFlight).IsGreaterThan(4);
                    await Assert.That(proxy.MaxInFlight).IsLessThanOrEqualTo(ClusterClient.MaxConcurrentDiscoveryRequests + 1);
                }
                else
                {
                    await Assert.That(proxy.Requests).IsEqualTo(2);
                    await Assert.That(proxy.MaxInFlight).IsEqualTo(2);
                }
            }
        }
        finally
        {
            foreach (var group in groups)
            {
                await admin.DeleteResourceAsync(Crds, null, $"probes.{group}", CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// <c>ClusterTabViewModel.ConnectAsync</c>'s requests in its order: <c>/version</c> alone,
    /// then discovery, namespaces and the metrics probe together, then the first pod list.
    /// The namespace list is refused for this user (the narrow-RBAC case connect expects).
    /// </summary>
    private static async Task ConnectLikeTheAppAsync(ClusterClient client, CancellationToken ct)
    {
        await client.GetServerVersionAsync(ct);
        var namespaces = Task.Run(async () =>
        {
            try
            {
                await client.ListResourceOnceAsync(ResourceDescriptor.Namespaces, cancellationToken: ct);
            }
            catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                // the expected answer for this user
            }
        }, ct);
        await Task.WhenAll(client.DiscoverResourcesAsync(ct), namespaces, client.IsMetricsApiAvailableAsync(ct));
        await client.ListResourceOnceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, cancellationToken: ct);
    }

    private static string CrdYaml(string group) => $$"""
        apiVersion: apiextensions.k8s.io/v1
        kind: CustomResourceDefinition
        metadata:
          name: probes.{{group}}
          labels:
            app.kubernetes.io/part-of: kubenimbus-live-tests
        spec:
          group: {{group}}
          scope: Namespaced
          names: { plural: probes, singular: probe, kind: Probe }
          versions:
            - name: v1
              served: true
              storage: true
              schema:
                openAPIV3Schema: { type: object, x-kubernetes-preserve-unknown-fields: true }
        """;

    [GeneratedRegex(@"token: (\S+)")]
    private static partial Regex TokenLine();

    [GeneratedRegex(@"server: (\S+)")]
    private static partial Regex ServerLine();

    [GeneratedRegex(@"\n\s*(certificate-authority-data|insecure-skip-tls-verify):[^\n]*")]
    private static partial Regex CaLine();

    /// <summary>A plugin that counts its runs in a file and prints a fixed, real token.</summary>
    private sealed record CountingPlugin(string Path, string Counter)
    {
        public int Runs => File.ReadAllLines(Counter).Length;

        public static CountingPlugin Write(string token)
        {
            var directory = Directory.CreateTempSubdirectory("kubenimbus-live-plugin").FullName;
            var counter = System.IO.Path.Combine(directory, "runs.txt");
            File.WriteAllText(counter, "");
            var credential = System.IO.Path.Combine(directory, "credential.json");
            File.WriteAllText(credential,
                $$$"""{"apiVersion":"client.authentication.k8s.io/v1","kind":"ExecCredential","status":{"token":"{{{token}}}","expirationTimestamp":"2999-01-01T00:00:00Z"}}""");

            if (OperatingSystem.IsWindows())
            {
                var cmd = System.IO.Path.Combine(directory, "plugin.cmd");
                File.WriteAllText(cmd, $"@echo off\r\n>>\"{counter}\" echo run\r\ntype \"{credential}\"\r\n");
                return new CountingPlugin(cmd, counter);
            }

            var sh = System.IO.Path.Combine(directory, "plugin");
            File.WriteAllText(sh, $"#!/bin/sh\necho run >> '{counter}'\ncat '{credential}'\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new CountingPlugin(sh, counter);
        }
    }

    /// <summary>
    /// A plain-HTTP loopback proxy in front of the real API server: it forwards each request
    /// (bearer token and all) after 100 ms, and counts how many are in flight at once. With
    /// <c>legacy</c> it asks the server for plain JSON instead of aggregated discovery, which
    /// is what a server older than the aggregated API answers with.
    /// </summary>
    private sealed class ObservingProxy : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpClient _upstream;
        private readonly Uri _target;
        private readonly bool _legacy;
        private int _inFlight;
        private int _maxInFlight;
        private int _requests;

        public ObservingProxy(Uri target, bool legacy)
        {
            _target = target;
            _legacy = legacy;
            // Loopback to the sandbox only; the narrow user's token is what authenticates.
            _upstream = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                MaxConnectionsPerServer = 64,
            });
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _ = Task.Run(PumpAsync);
        }

        public string Url { get; }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int Requests => Volatile.Read(ref _requests);

        private readonly ConcurrentBag<Task> _answers = [];

        private async Task PumpAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _answers.Add(Task.Run(() => ForwardAsync(context)));
            }
        }

        private async Task ForwardAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _requests);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight))
                   && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(100);
                using var request = new HttpRequestMessage(
                    new HttpMethod(context.Request.HttpMethod), new Uri(_target, context.Request.RawUrl));
                if (context.Request.Headers["Authorization"] is { } auth)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", auth);
                }

                var accept = _legacy ? "application/json" : context.Request.Headers["Accept"];
                if (accept is not null)
                {
                    request.Headers.TryAddWithoutValidation("Accept", accept);
                }

                using var response = await _upstream.SendAsync(request);
                var body = await response.Content.ReadAsByteArrayAsync();
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception)
            {
                context.Response.Abort();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose()
        {
            _listener.Close();
            _upstream.Dispose();
        }
    }
}
