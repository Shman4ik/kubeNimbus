using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A kubeconfig's <c>as</c>, <c>as-uid</c>, <c>as-groups</c> and <c>as-user-extra</c> reach the
/// API server as kubectl sends them, on both transports — the library reads them and sends
/// nothing, which made the app act as the base identity with more rights than kubectl has in
/// the same context.
/// </summary>
public class ImpersonationTests
{
    private const string Impersonating = """
        as: jane
        as-uid: "1234"
        as-groups: [devs]
        as-user-extra:
          acme.com/project: [payments]
          reason: on-call
        """;

    [Test]
    public async Task The_impersonation_headers_reach_the_api_server_over_http()
    {
        using var server = new ScriptedApiServer(AnswerEverything);
        using var client = await ClusterClient.ConnectAsync(
            ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null, userExtra: Impersonating)));

        await client.GetServerVersionAsync();
        (await client.GetJsonDocumentAsync("api/v1/namespaces", CancellationToken.None)).Dispose();

        foreach (var request in server.Requests)
        {
            await Assert.That(request.Values("Impersonate-User")).IsEquivalentTo(["jane"]);
            await Assert.That(request.Values("Impersonate-Uid")).IsEquivalentTo(["1234"]);
            await Assert.That(request.Values("Impersonate-Group")).IsEquivalentTo(["devs"]);
            await Assert.That(request.Values("Impersonate-Extra-acme.com%2Fproject")).IsEquivalentTo(["payments"]);
            await Assert.That(request.Values("Impersonate-Extra-reason")).IsEquivalentTo(["on-call"]);
            await Assert.That(request.Authorization).StartsWith("Bearer ");
        }

        await Assert.That(server.Requests.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task The_impersonation_headers_reach_the_api_server_over_the_websocket()
    {
        using var server = new ScriptedApiServer(AnswerEverything);
        using var client = await ClusterClient.ConnectAsync(
            ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null, userExtra: Impersonating)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            using var session = await client.ExecAsync("default", "pod", "app", ["sh"], cancellationToken: timeout.Token);
        }
        catch (Exception)
        {
            // The stand-in does not speak WebSocket; the upgrade request is what is checked.
        }

        var upgrade = server.Requests.First(r => r.Headers.ContainsKey("Upgrade"));
        await Assert.That(upgrade.Target).Contains("/exec");
        await Assert.That(upgrade.Values("Impersonate-User")).IsEquivalentTo(["jane"]);
        await Assert.That(upgrade.Values("Impersonate-Uid")).IsEquivalentTo(["1234"]);
        await Assert.That(upgrade.Values("Impersonate-Group")).IsEquivalentTo(["devs"]);
        await Assert.That(upgrade.Values("Impersonate-Extra-acme.com%2Fproject")).IsEquivalentTo(["payments"]);
    }

    [Test]
    public async Task Nothing_is_sent_without_as()
    {
        using var server = new ScriptedApiServer(AnswerEverything);
        using var client = await ClusterClient.ConnectAsync(
            ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await client.GetServerVersionAsync();
        try
        {
            using var session = await client.ExecAsync("default", "pod", "app", ["sh"], cancellationToken: timeout.Token);
        }
        catch (Exception)
        {
        }

        await Assert.That(server.Requests.Count).IsGreaterThanOrEqualTo(2);
        foreach (var request in server.Requests)
        {
            await Assert.That(request.HeaderLines.Any(h => h.Key.StartsWith("Impersonate-", StringComparison.OrdinalIgnoreCase))).IsFalse();
        }
    }

    [Test]
    public async Task More_than_one_group_is_refused_rather_than_sent_as_one_joined_group()
    {
        // .NET joins repeated header values into one line ("a, b"), which the API server
        // would read as a single group of that name. Acting as it would be acting as somebody
        // the kubeconfig did not name.
        var kubeconfig = ApiServerTlsTests.Kubeconfig("http://127.0.0.1:1", null, userExtra: "as: jane\nas-groups: [devs, ops]");

        var failure = await Assert.ThrowsAsync<KubeconfigSetupException>(
            () => ClusterClient.ConnectAsync(ApiServerTlsTests.Context(kubeconfig)));

        await Assert.That(failure!.Message).Contains("one");
        var report = await ConnectionReport.CreateAsync(ApiServerTlsTests.Context(kubeconfig), failure);
        await Assert.That(report.Step).IsEqualTo(ConnectionReport.ReadingKubeconfig);
    }

    [Test]
    public async Task Groups_without_a_user_are_refused()
    {
        var kubeconfig = ApiServerTlsTests.Kubeconfig("http://127.0.0.1:1", null, userExtra: "as-groups: [devs]");

        await Assert.ThrowsAsync<KubeconfigSetupException>(
            () => ClusterClient.ConnectAsync(ApiServerTlsTests.Context(kubeconfig)));
    }

    [Test]
    public async Task The_report_names_who_the_requests_act_as_and_nothing_secret()
    {
        var kubeconfig = ApiServerTlsTests.Kubeconfig("http://127.0.0.1:1", null, userExtra: Impersonating);

        var report = await ConnectionReport.CreateAsync(ApiServerTlsTests.Context(kubeconfig), new TimeoutException());

        var actsAs = report.Facts.Single(f => f.Label == "Acts as").Value;
        await Assert.That(actsAs).Contains("\"jane\"");
        await Assert.That(actsAs).Contains("devs");
        await Assert.That(report.Facts.Any(f => f.Value.Contains("kubenimbus-test-secret-token", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task The_reader_keeps_client_gos_shapes_the_library_model_cannot_hold()
    {
        var document = KubeconfigReader.Parse("""
            users:
            - name: Narrow
              user:
                as: jane
                as-uid: "42"
                as-groups: [devs]
                as-user-extra:
                  scopes: [view, edit]
                  team: platform
            - name: narrow
              user:
                as: someone-else
            """);

        // The library finds a context's user case-insensitively, first entry first; so does this.
        var impersonation = document.ImpersonationOf("NARROW")!;
        await Assert.That(impersonation.User).IsEqualTo("jane");
        await Assert.That(impersonation.Uid).IsEqualTo("42");
        await Assert.That(impersonation.Groups).IsEquivalentTo(["devs"]);
        await Assert.That(impersonation.Extra["scopes"]).IsEquivalentTo(["view", "edit"]);
        await Assert.That(impersonation.Extra["team"]).IsEquivalentTo(["platform"]);
    }

    [Test]
    [Arguments("reason", "reason")]
    [Arguments("acme.com/project", "acme.com%2Fproject")]
    [Arguments("with space", "with%20space")]
    [Arguments("ünï", "%C3%BCn%C3%AF")]
    public async Task Extra_keys_are_escaped_as_client_go_escapes_them(string key, string expected)
    {
        await Assert.That(KubeconfigImpersonation.EscapeExtraKey(key)).IsEqualTo(expected);
    }

    private static ScriptedResponse AnswerEverything(ScriptedRequest request) =>
        request.Target.StartsWith("/version", StringComparison.Ordinal)
            ? new ScriptedResponse(200, ScriptedApiServer.VersionBody)
            : request.Target.StartsWith("/api/v1/namespaces", StringComparison.Ordinal)
                ? new ScriptedResponse(200, """{"kind":"NamespaceList","apiVersion":"v1","metadata":{},"items":[]}""")
                : new ScriptedResponse(403, """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"forbidden","code":403}""");
}
