using System.Net;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Requests ask for HTTP/2 and take HTTP/1.1 from a server that offers nothing better;
/// <c>DISABLE_HTTP2</c> turns it off the way client-go reads it; the handler carries the
/// pings and the extra connections HTTP/2 needs (<see cref="ApiServerHttpVersion"/>). The
/// live half, a real API server negotiating HTTP/2 and one connection carrying a whole
/// connect, is <c>Live/ConnectLatencyLiveTests</c>.
/// </summary>
public class ApiServerHttpVersionTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task Http2_is_asked_for_unless_the_variable_has_a_value(string? value)
    {
        await Assert.That(ApiServerHttpVersion.For(value)).IsEqualTo(HttpVersion.Version20);
    }

    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("false")]
    public async Task Any_value_of_DISABLE_HTTP2_asks_for_http11_as_client_go_does(string value)
    {
        // client-go checks len(os.Getenv("DISABLE_HTTP2")) > 0, so even "false" disables it.
        await Assert.That(ApiServerHttpVersion.For(value)).IsEqualTo(HttpVersion.Version11);
    }

    [Test]
    public async Task A_request_asks_for_its_version_or_lower()
    {
        using var request = ApiServerHttpVersion.NewRequest(
            HttpMethod.Get, new Uri("https://127.0.0.1/version"), null, HttpVersion.Version20);

        await Assert.That(request.Version).IsEqualTo(HttpVersion.Version20);
        await Assert.That(request.VersionPolicy).IsEqualTo(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Test]
    public async Task The_handler_pings_a_quiet_connection_and_opens_another_when_one_is_full()
    {
        using var handler = new SocketsHttpHandler();

        ApiServerHttpVersion.Configure(handler);

        await Assert.That(handler.EnableMultipleHttp2Connections).IsTrue();
        await Assert.That(handler.InitialHttp2StreamWindowSize).IsEqualTo(4 * 1024 * 1024);
        await Assert.That(handler.KeepAlivePingPolicy).IsEqualTo(HttpKeepAlivePingPolicy.WithActiveRequests);
        await Assert.That(handler.KeepAlivePingDelay).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(handler.KeepAlivePingTimeout).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task A_server_that_offers_only_http11_is_answered_in_http11()
    {
        // ScriptedApiServer's TLS offers no ALPN protocol, which is what a server or a
        // TLS-terminating proxy that does not speak HTTP/2 looks like to the client.
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(
            _ => new ScriptedResponse(200, ScriptedApiServer.VersionBody), pki.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, pki.AuthorityData)));

        using var response = await client.SendRequestAsync(
            HttpMethod.Get, "version", null, HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        await Assert.That(client.RequestVersion).IsEqualTo(HttpVersion.Version20);
        await Assert.That(response.RequestMessage!.Version).IsEqualTo(HttpVersion.Version20);
        await Assert.That(response.Version).IsEqualTo(HttpVersion.Version11);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
