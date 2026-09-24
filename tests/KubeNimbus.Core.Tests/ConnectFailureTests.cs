using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// What a connect that fails says. Each case here used to reach the tab's status line as
/// a JSON parser error rather than as the reason the cluster could not be reached: an
/// exec credential plugin that failed (its reason was on stderr, the library read the
/// empty stdout as JSON), and something other than an API server answering
/// <c>/version</c> with a web page.
/// </summary>
public class ConnectFailureTests
{
    [Test]
    public async Task A_failing_exec_plugin_is_reported_by_what_it_printed()
    {
        var kubeconfig = WriteKubeconfig("http://127.0.0.1:1", ExecUser(
            OperatingSystem.IsWindows()
                ? ("cmd", ["/c", "echo error: could not reach login.example.com 1>&2 & exit /b 1"])
                : ("sh", ["-c", "echo 'error: could not reach login.example.com' >&2; exit 1"])));

        var ex = await Assert.ThrowsAsync<ExecCredentialException>(
            () => ClusterClient.ConnectAsync(Context(kubeconfig)));

        await Assert.That(ex!.Message).Contains("could not reach login.example.com");
        await Assert.That(ex.Message).DoesNotContain("JsonException");
        await Assert.That(ex.Message).DoesNotContain("deserializ");
    }

    [Test]
    public async Task An_exec_plugin_that_cannot_be_started_says_so()
    {
        var kubeconfig = WriteKubeconfig("http://127.0.0.1:1", ExecUser(("kubenimbus-no-such-plugin", [])));

        var ex = await Assert.ThrowsAsync<ExecCredentialException>(
            () => ClusterClient.ConnectAsync(Context(kubeconfig)));

        await Assert.That(ex!.Message).Contains("kubenimbus-no-such-plugin");
        await Assert.That(ex.Message).DoesNotContain("deserializ");
    }

    [Test]
    public async Task A_web_page_answering_version_is_not_reported_as_a_parse_error()
    {
        using var server = new CannedServer("200 OK", "text/html", "<html><body>Sign in to continue</body></html>");
        using var client = await ClusterClient.ConnectAsync(Context(WriteKubeconfig(server.Url, TokenUser)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(ex!.Message).Contains("not as a Kubernetes API server");
        await Assert.That(ex.Message).Contains("Sign in to continue");
        await Assert.That(ex.Message).DoesNotContain("invalid start of a value");
    }

    [Test]
    public async Task A_status_body_on_version_is_reported_by_its_message()
    {
        using var server = new CannedServer("401 Unauthorized", "application/json",
            """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"Unauthorized","reason":"Unauthorized","code":401}""");
        using var client = await ClusterClient.ConnectAsync(Context(WriteKubeconfig(server.Url, TokenUser)));

        var ex = await Assert.ThrowsAsync<KubernetesApiException>(() => client.GetServerVersionAsync());

        await Assert.That(ex!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(ex.Message).IsEqualTo("Unauthorized (401 Unauthorized)");
    }

    [Test]
    public async Task A_real_version_still_parses()
    {
        using var server = new CannedServer("200 OK", "application/json",
            """{"major":"1","minor":"31","gitVersion":"v1.31.0","platform":"linux/amd64"}""");
        using var client = await ClusterClient.ConnectAsync(Context(WriteKubeconfig(server.Url, TokenUser)));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
    }

    // --------------------------------------------------------------------- helpers

    private const string TokenUser = """
          user:
            # Not a credential: the stand-in never checks it.
            token: stub-token
        """;

    private static string ExecUser((string Command, string[] Args) plugin) => $$"""
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: {{plugin.Command}}
              args: [{{string.Join(", ", plugin.Args.Select(a => $"'{a.Replace("'", "''")}'"))}}]
        """;

    private static ClusterContext Context(string kubeconfig) => new("stub", "stub", null, "stub", kubeconfig);

    private static string WriteKubeconfig(string server, string user)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-connect-failure").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: stub
              cluster:
                server: {server}
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

    /// <summary>Answers every request with the same raw HTTP response.</summary>
    private sealed class CannedServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public CannedServer(string status, string contentType, string body)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = connection.GetStream();
                    var buffer = new byte[16 * 1024];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
