using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-45: <c>proxy-url</c> end to end against a real API server, over both of its schemes
/// and both transports. FEAT-54 was verified only with an HTTP proxy stand-in answering a plain
/// request; SOCKS5, and the proxy on the WebSocket that exec and port-forward open (a separate
/// <c>ClientWebSocket</c> the HTTP handler's proxy never reaches), had not carried a byte.
/// </summary>
/// <remarks>
/// The proxied client is given the API server as <c>kubernetes.default.svc</c> — a name in the
/// sandbox certificate's subject alternative names that does not resolve on this machine — and
/// <see cref="TunnelProxy"/> sends every tunnel to the sandbox's real address. So a request that
/// succeeds came through the stand-in, and the stand-in's record of the tunnel (the name asked
/// for, the bytes carried) is checked as well. A name rather than <c>127.0.0.1</c> for a second
/// reason: .NET never proxies a loopback destination.
/// </remarks>
public class ProxyLiveTests
{
    private const string ProxiedHost = "kubernetes.default.svc";

    private static readonly SemaphoreSlim PodGate = new(1, 1);
    private static string? _pod;

    [Test]
    [Arguments("http")]
    [Arguments("socks5")]
    [Timeout(120_000)]
    public async Task A_list_goes_through_the_proxy(string scheme, CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        using var proxy = new TunnelProxy(await UpstreamAsync(admin, ct));
        using var client = await ProxiedClientAsync(admin, scheme, proxy, ct);

        var version = await client.GetServerVersionAsync(ct);
        var namespaces = await client.ListResourceOnceAsync(ResourceDescriptor.Namespaces, null, cancellationToken: ct);

        await Assert.That(version.GitVersion).IsNotEmpty();
        await Assert.That(namespaces.Any(n => n.Name == LiveCluster.Namespace)).IsTrue();
        await AssertCarriedAsync(proxy, scheme, since: 0);
    }

    [Test]
    [Arguments("http")]
    [Arguments("socks5")]
    [Timeout(180_000)]
    public async Task An_exec_goes_through_the_proxy(string scheme, CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        var pod = await PodAsync(admin, ct);
        using var proxy = new TunnelProxy(await UpstreamAsync(admin, ct));
        using var client = await ProxiedClientAsync(admin, scheme, proxy, ct);
        var before = proxy.Tunnels.Count;

        using var session = await client.ExecAsync(
            LiveCluster.Namespace, pod, "app", ["sh", "-c", "echo through-$((40+2))"], tty: false, cancellationToken: ct);
        var status = session.ReadTerminalStatusAsync(ct);
        using var reader = new StreamReader(session.StdOut, Encoding.UTF8);
        var stdout = await reader.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        await Assert.That(stdout.Trim()).IsEqualTo("through-42");
        await Assert.That(await status.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsNull();
        await AssertCarriedAsync(proxy, scheme, since: before);
    }

    [Test]
    [Arguments("http")]
    [Arguments("socks5")]
    [Timeout(180_000)]
    public async Task A_port_forward_goes_through_the_proxy(string scheme, CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        var pod = await PodAsync(admin, ct);
        using var proxy = new TunnelProxy(await UpstreamAsync(admin, ct));
        using var client = await ProxiedClientAsync(admin, scheme, proxy, ct);
        var before = proxy.Tunnels.Count;

        await using var forward = client.StartPortForward(LiveCluster.Namespace, pod, 8080);
        Exception? failure = null;
        forward.ConnectionFailed += e => failure = e;
        await forward.StartAsync(ct);

        // busybox httpd in the pod serves this file; the request crosses the forward's own
        // WebSocket, which is what has to have gone through the proxy.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await http.GetStringAsync($"http://127.0.0.1:{forward.LocalPort}/index.html", ct);

        await Assert.That(failure).IsNull();
        await Assert.That(body.Trim()).IsEqualTo("forwarded-through-the-proxy");
        await AssertCarriedAsync(proxy, scheme, since: before);
    }

    // --------------------------------------------------------------------- helpers

    /// <summary>A tunnel since <paramref name="since"/> asked for the proxied name over <paramref name="scheme"/> and carried bytes both ways.</summary>
    private static async Task AssertCarriedAsync(TunnelProxy proxy, string scheme, int since)
    {
        var tunnels = proxy.Tunnels.Skip(since).ToList();
        var described = $"tunnels: [{string.Join(", ", tunnels)}]; refused: [{string.Join(", ", proxy.Refusals)}]";

        await Assert.That(tunnels.Count).IsGreaterThan(0).Because(described);
        await Assert.That(tunnels.All(t => t.Protocol == scheme && t.Host == ProxiedHost)).IsTrue().Because(described);
        await Assert.That(tunnels.Any(t => Interlocked.Read(ref t.BytesUp) > 0 && Interlocked.Read(ref t.BytesDown) > 0)).IsTrue().Because(described);
    }

    /// <summary>One pod for the class: a busybox shell that also serves a file on 8080.</summary>
    private static async Task<string> PodAsync(ClusterClient admin, CancellationToken ct)
    {
        await PodGate.WaitAsync(ct);
        try
        {
            if (_pod is null)
            {
                var name = LiveCluster.Named("proxied");
                await LiveCluster.ApplyAsync(admin, LiveCluster.Deployments, name, LiveCluster.DeploymentYaml(name, 1,
                    "mkdir -p /tmp/www && echo forwarded-through-the-proxy > /tmp/www/index.html && "
                    + "trap 'exit 0' TERM; httpd -f -p 8080 -h /tmp/www & wait $!"), ct);
                await LiveCluster.WaitForReadyPodsAsync(admin, name, 1, ct);
                _pod = (await LiveCluster.PodsOfAsync(admin, name, ct)).Single().Name;
            }

            return _pod;
        }
        finally
        {
            PodGate.Release();
        }
    }

    /// <summary>The sandbox API server's real address, read from its kubeconfig.</summary>
    private static async Task<IPEndPoint> UpstreamAsync(ClusterClient admin, CancellationToken ct)
    {
        var server = new Uri((await SandboxEntryAsync(admin, ct)).Cluster.Server);
        var addresses = await Dns.GetHostAddressesAsync(server.Host, AddressFamily.InterNetwork, ct);
        return new IPEndPoint(addresses[0], server.Port);
    }

    /// <summary>
    /// A client for the sandbox's own credentials, whose cluster entry names
    /// <see cref="ProxiedHost"/> and the stand-in as its <c>proxy-url</c>. The kubeconfig is a
    /// temp file holding the sandbox's own client credentials, deleted at once after the
    /// client is built (the client keeps what it needs; hard rule 4 is the app's, and a test
    /// copy lying around is still a copy).
    /// </summary>
    private static async Task<ClusterClient> ProxiedClientAsync(ClusterClient admin, string scheme, TunnelProxy proxy, CancellationToken ct)
    {
        var (cluster, user) = await SandboxEntryAsync(admin, ct);
        var server = new Uri(cluster.Server);
        if (string.IsNullOrEmpty(cluster.CertificateAuthorityData))
        {
            Skip.Test("The sandbox kubeconfig carries no certificate-authority-data to verify the proxied name against.");
        }

        var credentials = new StringBuilder();
        void Line(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                credentials.Append($"\n    {key}: {value}");
            }
        }

        Line("client-certificate-data", user.ClientCertificateData);
        Line("client-key-data", user.ClientKeyData);
        Line("token", user.Token);

        var directory = Directory.CreateTempSubdirectory("kubenimbus-live-proxy").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        await File.WriteAllTextAsync(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: proxied
              cluster:
                server: https://{ProxiedHost}:{server.Port}
                certificate-authority-data: {cluster.CertificateAuthorityData}
                proxy-url: {scheme}://127.0.0.1:{proxy.Port}
            users:
            - name: proxied
              user:{credentials}
            contexts:
            - name: proxied
              context:
                cluster: proxied
                user: proxied
            current-context: proxied
            """, ct);

        try
        {
            return await ClusterClient.ConnectAsync(new ClusterContext("proxied", "proxied", LiveCluster.Namespace, "proxied", path), ct);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(k8s.KubeConfigModels.ClusterEndpoint Cluster, k8s.KubeConfigModels.UserCredentials User)> SandboxEntryAsync(
        ClusterClient admin, CancellationToken ct)
    {
        var sandbox = (await KubeconfigReader.LoadAsync(admin.Context.KubeconfigPath, ct)).Configuration;
        var context = sandbox.Contexts.First(c => c.Name == admin.Context.Name).ContextDetails;
        var cluster = sandbox.Clusters.First(c => c.Name == context.Cluster).ClusterEndpoint;
        var user = sandbox.Users.First(u => u.Name == context.User).UserCredentials;
        return (cluster, user);
    }
}
