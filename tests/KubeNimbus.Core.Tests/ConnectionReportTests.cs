using System.Net;
using System.Net.Sockets;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-51: what the content area says when a connect fails — the step, one sentence of
/// cause, the exception's own text, and the facts the attempt was made with. Every
/// classification is deterministic; these pin the branches and, just as much, that no
/// credential ever becomes a fact.
/// </summary>
public class ConnectionReportTests
{
    [Test]
    public async Task A_refused_connection_names_the_server_and_the_step()
    {
        var kubeconfig = Kubeconfig("http://127.0.0.1:1", TokenUser);
        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), failure!);

        await Assert.That(report.Step).IsEqualTo(ConnectionReport.ReachingServer);
        await Assert.That(report.Headline).IsEqualTo("Nothing is accepting connections at http://127.0.0.1:1.");
        await Assert.That(report.Detail).IsNotEmpty();
        await Assert.That(Fact(report, "Server")).IsEqualTo("http://127.0.0.1:1");
        await Assert.That(Fact(report, "Kubeconfig")).IsEqualTo(kubeconfig);
        await Assert.That(Fact(report, "Context")).IsEqualTo("stub");
        await Assert.That(Fact(report, "Signs in with")).IsEqualTo("bearer token in the kubeconfig");
    }

    [Test]
    public async Task No_fact_ever_carries_a_credential()
    {
        const string secret = "super-secret-token-value";
        var kubeconfig = Kubeconfig("https://a.example", $"""
              user:
                token: {secret}
            """, proxyUrl: "http://alice:hunter2@proxy.corp:3128");

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), new HttpRequestException("x"));
        var everything = string.Join("\n", report.Facts.Select(f => f.Label + " " + f.Value));

        await Assert.That(everything).DoesNotContain(secret);
        await Assert.That(everything).DoesNotContain("hunter2");
        await Assert.That(everything).DoesNotContain("alice");
        await Assert.That(Fact(report, "Proxy")).IsEqualTo("http://proxy.corp:3128/");
    }

    [Test]
    public async Task An_exec_plugin_that_is_nowhere_says_where_it_was_looked_for()
    {
        using var _ = ExecPluginPath.OverrideDirectories([]);
        var kubeconfig = Kubeconfig("https://a.example", """
              user:
                exec:
                  apiVersion: client.authentication.k8s.io/v1beta1
                  command: kubenimbus-plugin-nowhere
                  installHint: brew install kubenimbus-plugin
            """);

        var failure = await Assert.ThrowsAsync<ExecCredentialException>(() => ClusterClient.ConnectAsync(Context(kubeconfig)));
        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), failure!);

        await Assert.That(report.Step).IsEqualTo(ConnectionReport.RunningPlugin);
        await Assert.That(Fact(report, "Signs in with")).Contains("not found on PATH");
        await Assert.That(Fact(report, "Plugin install hint")).IsEqualTo("brew install kubenimbus-plugin");
    }

    [Test]
    public async Task A_401_reads_as_expired_credentials()
    {
        using var server = new ScriptedApiServer(_ => ScriptedApiServer.Unauthorized());
        var kubeconfig = Kubeconfig(server.Url, TokenUser);
        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));
        var failure = await Assert.ThrowsAsync<KubernetesApiException>(() => client.GetServerVersionAsync());

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), failure!);

        await Assert.That(report.Step).IsEqualTo(ConnectionReport.SigningIn);
        await Assert.That(report.Headline).Contains("401");
        await Assert.That(report.Advice!).Contains("expired");
    }

    [Test]
    public async Task Each_transport_failure_lands_on_its_own_step()
    {
        IReadOnlyList<ConnectionFact> facts = [];
        const string server = "https://api.private.example:6443";

        var dns = ConnectionReport.Explain(
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."), facts, server, []);
        var tls = ConnectionReport.Explain(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established."), facts, server, []);
        var proxy = ConnectionReport.Explain(
            new HttpRequestException(HttpRequestError.ProxyTunnelError, "The proxy tunnel request failed."), facts, server, []);
        var timeout = ConnectionReport.Explain(new TaskCanceledException("timed out"), facts, server, []);
        var refused = ConnectionReport.Explain(
            new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)),
            facts, server, []);

        await Assert.That(dns.Headline).IsEqualTo("api.private.example does not resolve from this machine.");
        await Assert.That(tls.Step).IsEqualTo(ConnectionReport.SettingUpTls);
        await Assert.That(proxy.Step).IsEqualTo(ConnectionReport.GoingThroughProxy);
        await Assert.That(timeout.Headline).IsEqualTo($"No answer from {server} in time.");
        await Assert.That(refused.Headline).IsEqualTo($"Nothing is accepting connections at {server}.");
    }

    [Test]
    public async Task An_unreadable_kubeconfig_is_a_fact_not_an_exception()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"kubenimbus-missing-{Guid.NewGuid():N}.yaml");

        var report = await ConnectionReport.CreateAsync(Context(missing), new FileNotFoundException("gone", missing));

        await Assert.That(report.Step).IsEqualTo(ConnectionReport.ReadingKubeconfig);
        await Assert.That(report.Facts.Any(f => f.Label == "Could not read it")).IsTrue();
    }

    // --------------------------------------------------------------------- helpers

    private const string TokenUser = """
          user:
            token: not-a-credential
        """;

    private static string Fact(ConnectionFailureReport report, string label) =>
        report.Facts.Single(f => f.Label == label).Value;

    private static ClusterContext Context(string kubeconfig) => new("stub", "stub", null, "stub", kubeconfig);

    private static string Kubeconfig(string server, string user, string? proxyUrl = null)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-report").FullName;
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
            {user}
            """);
        return path;
    }
}
