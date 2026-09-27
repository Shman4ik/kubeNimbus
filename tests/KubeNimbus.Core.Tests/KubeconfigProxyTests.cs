using System.Net;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-54: the cluster's <c>proxy-url</c>, which the client library parses away. A local
/// stand-in plays the proxy: an HTTP proxy receives the request line in absolute form
/// (<c>GET http://host/version</c>), so it can answer for a host that does not exist.
/// </summary>
public class KubeconfigProxyTests
{
    [Test]
    public async Task Requests_go_through_the_clusters_proxy_url()
    {
        // The API server's name ends in .invalid, which no resolver answers (RFC 6761), so
        // the only way this request can succeed is through the proxy.
        using var proxy = new ScriptedApiServer(request =>
            request.Target.StartsWith("http://kubenimbus-behind-a-proxy.invalid", StringComparison.Ordinal)
                ? new ScriptedResponse(200, ScriptedApiServer.VersionBody)
                : new ScriptedResponse(502, "not for me"));

        var kubeconfig = Kubeconfig("http://kubenimbus-behind-a-proxy.invalid:6443", proxy.Url);
        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(proxy.Requests.Single().Target).IsEqualTo("http://kubenimbus-behind-a-proxy.invalid:6443/version");
    }

    [Test]
    public async Task The_proxy_is_read_from_the_contexts_own_cluster_only()
    {
        const string text = """
            apiVersion: v1
            kind: Config
            clusters:
            - name: direct
              cluster:
                server: https://a.example
            - name: bastion
              cluster:
                server: https://b.example
                proxy-url: socks5://localhost:1080
            """;

        await Assert.That(KubeconfigProxy.Read(text, "bastion")).IsEqualTo("socks5://localhost:1080");
        await Assert.That(KubeconfigProxy.Read(text, "direct")).IsNull();
        await Assert.That(KubeconfigProxy.Read(text, "missing")).IsNull();
    }

    [Test]
    public async Task Socks5_and_http_proxies_are_accepted()
    {
        var socks = KubeconfigProxy.Create("socks5://localhost:1080");
        var http = KubeconfigProxy.Create("http://proxy.corp:3128");

        await Assert.That(socks!.Address!.ToString()).IsEqualTo("socks5://localhost:1080/");
        await Assert.That(http!.Address!.ToString()).IsEqualTo("http://proxy.corp:3128/");
        await Assert.That(KubeconfigProxy.Create(null)).IsNull();
        await Assert.That(KubeconfigProxy.Create("  ")).IsNull();
    }

    [Test]
    public async Task A_scheme_that_cannot_be_proxied_through_is_refused_by_name_rather_than_ignored()
    {
        // Connecting direct instead would send traffic where the kubeconfig said not to.
        var ex = await Assert.ThrowsAsync<KubeconfigSetupException>(() =>
            Task.FromResult(KubeconfigProxy.Create("ftp://proxy.corp:21")));

        await Assert.That(ex!.Message).Contains("ftp");
        await Assert.That(ex.Message).Contains("socks5");
    }

    [Test]
    public async Task A_bad_proxy_url_fails_the_connect_before_the_plugin_runs()
    {
        var kubeconfig = Kubeconfig("https://a.example", "ftp://proxy.corp:21");

        await Assert.ThrowsAsync<KubeconfigSetupException>(() => ClusterClient.ConnectAsync(Context(kubeconfig)));
    }

    [Test]
    public async Task Proxy_credentials_are_used_and_never_displayed()
    {
        var proxy = KubeconfigProxy.Create("http://alice:s%40cret@proxy.corp:3128");
        var credential = proxy!.Credentials!.GetCredential(new Uri("http://proxy.corp:3128"), "Basic");

        await Assert.That(credential!.UserName).IsEqualTo("alice");
        await Assert.That(credential.Password).IsEqualTo("s@cret");
        await Assert.That(proxy.Address!.ToString()).DoesNotContain("alice");
        await Assert.That(KubeconfigProxy.Redact("http://alice:s%40cret@proxy.corp:3128")).DoesNotContain("cret");
        await Assert.That(KubeconfigProxy.Redact("http://alice:s%40cret@proxy.corp:3128")).DoesNotContain("alice");
    }

    [Test]
    public async Task Without_a_proxy_url_the_client_has_no_proxy_of_its_own()
    {
        // The ambient HTTPS_PROXY behaviour is left exactly as it was.
        using var server = new ScriptedApiServer(_ => new ScriptedResponse(200, ScriptedApiServer.VersionBody));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, proxyUrl: null)));

        await Assert.That(client.Proxy).IsNull();
        await Assert.That((await client.GetServerVersionAsync()).GitVersion).IsEqualTo("v1.31.0");
    }

    private static ClusterContext Context(string kubeconfig) => new("stub", "stub", null, "stub", kubeconfig);

    private static string Kubeconfig(string server, string? proxyUrl)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-proxy").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        var proxyLine = proxyUrl is null ? "" : $"\n    proxy-url: {proxyUrl}";
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: stub
              cluster:
                server: {server}{proxyLine}
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
        return path;
    }
}
