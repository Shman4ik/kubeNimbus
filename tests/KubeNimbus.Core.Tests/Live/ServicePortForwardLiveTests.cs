using System.Net.Http;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// FEAT-29 against a real API server and a real kubelet: a forward of a Service port reaches
/// the pod its EndpointSlices name, through a <em>named</em> target port the slice resolved,
/// and when that pod is deleted the next connection moves to its replacement and says so —
/// it never keeps forwarding to nothing. Each pod serves its own name over HTTP (busybox
/// <c>httpd</c>), so "which pod answered" is read off the response, not inferred.
/// </summary>
public class ServicePortForwardLiveTests
{
    private static string Manifest(string name, int replicas) => $$"""
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: {{name}}
          namespace: {{LiveCluster.Namespace}}
        spec:
          replicas: {{replicas}}
          selector:
            matchLabels:
              app: {{name}}
          template:
            metadata:
              labels:
                app: {{name}}
            spec:
              terminationGracePeriodSeconds: 2
              containers:
                - name: web
                  image: {{LiveCluster.Image}}
                  imagePullPolicy: IfNotPresent
                  command: ["sh", "-c", "mkdir -p /www && hostname > /www/index.html && exec httpd -f -p 8080 -h /www"]
                  ports:
                    - name: web
                      containerPort: 8080
                  readinessProbe:
                    tcpSocket:
                      port: web
                    periodSeconds: 1
        """;

    private static string ServiceYaml(string name, string selectorApp) => $$"""
        apiVersion: v1
        kind: Service
        metadata:
          name: {{name}}
          namespace: {{LiveCluster.Namespace}}
        spec:
          selector:
            app: {{selectorApp}}
          ports:
            - name: http
              port: 80
              targetPort: web
        """;

    private static readonly ResourceDescriptor Services = ResourceDescriptor.Services;

    /// <summary>One GET on a fresh connection, so every call is a new forwarded connection.</summary>
    private static async Task<string?> GetAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/index.html");
        request.Headers.ConnectionClose = true;
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(ct)).Trim() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Retries a GET until a pod answers — readiness and the endpoint can lead the listener by a moment.</summary>
    private static async Task<string> AnswerAsync(int port, Func<string, bool> accept, string what, CancellationToken ct)
    {
        string? last = null;
        await LiveCluster.WaitUntilAsync(
            async () => (last = await GetAsync(port, ct)) is { } body && accept(body),
            TimeSpan.FromSeconds(60), what, ct);
        return last!;
    }

    [Test]
    [Timeout(240_000)]
    public async Task A_service_forward_reaches_the_pod_it_names_and_moves_when_that_pod_goes(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("svcfwd");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name, Manifest(name, 1), ct);
        await LiveCluster.ApplyAsync(client, Services, name, ServiceYaml(name, name), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        await LiveCluster.WaitUntilAsync(
            async () => (await client.ResolveServiceForwardTargetAsync(LiveCluster.Namespace, name, 80, cancellationToken: ct)).Target is not null,
            TimeSpan.FromSeconds(60), "a ready endpoint for the service", ct);

        await using var session = client.StartServicePortForward(LiveCluster.Namespace, name, 80);
        var moves = new List<(PortForwardTarget From, PortForwardTarget To)>();
        session.TargetChanged += (from, to) => { lock (moves) { moves.Add((from, to)); } };
        await session.StartAsync(ct);

        var first = session.Target!;
        await Assert.That(first.PodPort).IsEqualTo(8080); // the named targetPort, resolved by the slice
        await Assert.That(await AnswerAsync(session.LocalPort, b => b.Length > 0, "the first pod's answer", ct))
            .IsEqualTo(first.PodName);

        await client.DeleteResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, first.PodName, ct);
        await LiveCluster.WaitUntilAsync(
            async () => (await LiveCluster.PodsOfAsync(client, name, ct)).Any(p => p.Name != first.PodName && LiveCluster.IsReady(p)),
            TimeSpan.FromSeconds(90), "the replacement pod to be ready", ct);

        var answer = await AnswerAsync(
            session.LocalPort, b => b != first.PodName, "an answer from the replacement pod", ct);

        await Assert.That(answer).IsNotEqualTo(first.PodName);
        await Assert.That(session.Target!.PodName).IsEqualTo(answer);
        // Said, not done silently: the move from the deleted pod to the one that answered.
        bool announced;
        lock (moves)
        {
            announced = moves.Any(m => m.From.PodName == first.PodName && m.To.PodName == answer);
        }

        await Assert.That(announced).IsTrue();
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_service_that_selects_no_pod_refuses_to_start_with_the_reason(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("svcnone");
        await LiveCluster.ApplyAsync(client, Services, name, ServiceYaml(name, "matches-nothing"), ct);

        await using var session = client.StartServicePortForward(LiveCluster.Namespace, name, 80);
        var refused = await Assert.ThrowsAsync<PortForwardException>(() => session.StartAsync(ct));

        await Assert.That(refused!.Message).IsEqualTo(
            $"{name} has no endpoints for port 80 (http) — no pod is serving it, so there is nothing to forward to.");
    }
}
