using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// B3-3: an Argo CD sync from kubeNimbus records who asked. The username comes from a
/// SelfSubjectReview (<c>kubectl auth whoami</c>), asked once per credential, at v1 and then
/// v1beta1, with the tool's name alone when the server cannot say. Over real HTTP against a
/// stand-in API server, so what is sent is observed rather than argued.
/// </summary>
public class ArgoSyncIdentityTests
{
    private static readonly ResourceDescriptor Applications =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", Namespaced: true, ShortNames: [], Categories: []);

    private const string ReviewPath = "/apis/authentication.k8s.io/{0}/selfsubjectreviews";

    private const string ApplicationPath = "/apis/argoproj.io/v1alpha1/namespaces/argocd/applications/checkout";

    /// <summary>A SelfSubjectReview answer carrying more than a name — none of the rest may travel.</summary>
    private static string Review(string version, string username) => """
        {"kind":"SelfSubjectReview","apiVersion":"authentication.k8s.io/VERSION","metadata":{},
         "status":{"userInfo":{"username":"USERNAME","uid":"7f1c-uid","groups":["payments-oncall","system:authenticated"],
                               "extra":{"reason":["break-glass"]}}}}
        """.Replace("VERSION", version, StringComparison.Ordinal).Replace("USERNAME", username, StringComparison.Ordinal);

    private static readonly ScriptedResponse NotFound = new(404,
        """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""");

    private static async Task<ClusterClient> ConnectAsync(ScriptedApiServer server) =>
        await ClusterClient.ConnectAsync(ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null)));

    private static string InitiatorOf(ScriptedRequest patch)
    {
        using var doc = JsonDocument.Parse(patch.Body);
        return doc.RootElement.GetProperty("operation").GetProperty("initiatedBy").GetProperty("username").GetString() ?? "";
    }

    private static ScriptedRequest[] Reviews(ScriptedApiServer server) =>
        [.. server.Requests.Where(r => r.Target.Contains("selfsubjectreviews", StringComparison.Ordinal))];

    private static ScriptedRequest[] Patches(ScriptedApiServer server) =>
        [.. server.Requests.Where(r => r.Method == "PATCH")];

    [Test]
    public async Task A_sync_records_the_username_the_server_reports()
    {
        using var server = new ScriptedApiServer(request => request.Target switch
        {
            var t when t == string.Format(ReviewPath, "v1") => new ScriptedResponse(201, Review("v1", "jane@example.com")),
            ApplicationPath => new ScriptedResponse(200, "{}"),
            _ => NotFound,
        });
        using var client = await ConnectAsync(server);

        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");

        var review = Reviews(server).Single();
        await Assert.That(review.Method).IsEqualTo("POST");
        await Assert.That(review.Body).Contains("\"kind\":\"SelfSubjectReview\"");

        var patch = Patches(server).Single();
        await Assert.That(InitiatorOf(patch)).IsEqualTo("jane@example.com (kubeNimbus)");

        // Only the name: never a group, the UID or an extra.
        await Assert.That(patch.Body).DoesNotContain("payments-oncall");
        await Assert.That(patch.Body).DoesNotContain("7f1c-uid");
        await Assert.That(patch.Body).DoesNotContain("break-glass");
    }

    /// <summary>A 1.27 server serves the review at v1beta1 only; v1 is a 404 there.</summary>
    [Test]
    public async Task A_server_without_v1_is_asked_at_v1beta1()
    {
        using var server = new ScriptedApiServer(request => request.Target switch
        {
            var t when t == string.Format(ReviewPath, "v1beta1") => new ScriptedResponse(201, Review("v1beta1", "ops-bot")),
            ApplicationPath => new ScriptedResponse(200, "{}"),
            _ => NotFound,
        });
        using var client = await ConnectAsync(server);

        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");

        await Assert.That(Reviews(server).Select(r => r.Target)).IsEquivalentTo(
            [string.Format(ReviewPath, "v1"), string.Format(ReviewPath, "v1beta1")]);
        await Assert.That(InitiatorOf(Patches(server).Single())).IsEqualTo("ops-bot (kubeNimbus)");
    }

    /// <summary>
    /// Older than 1.27, neither version exists: the sync still goes through, as the tool alone,
    /// and the settled "not served" is not asked about again on this connection.
    /// </summary>
    [Test]
    public async Task A_server_with_no_review_falls_back_to_the_tool_and_is_not_asked_again()
    {
        using var server = new ScriptedApiServer(request => request.Target == ApplicationPath
            ? new ScriptedResponse(200, "{}")
            : NotFound);
        using var client = await ConnectAsync(server);

        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");
        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");

        await Assert.That(Patches(server).Select(InitiatorOf)).IsEquivalentTo(["kubeNimbus", "kubeNimbus"]);
        await Assert.That(Reviews(server).Length).IsEqualTo(2);
    }

    /// <summary>A refusal may be transient, so it falls back for this sync and is asked again for the next.</summary>
    [Test]
    public async Task A_refused_review_falls_back_and_is_asked_again()
    {
        using var server = new ScriptedApiServer(request => request.Target == ApplicationPath
            ? new ScriptedResponse(200, "{}")
            : new ScriptedResponse(403,
                """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Forbidden","code":403}"""));
        using var client = await ConnectAsync(server);

        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");
        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");

        await Assert.That(Patches(server).Select(InitiatorOf)).IsEquivalentTo(["kubeNimbus", "kubeNimbus"]);
        await Assert.That(Reviews(server).Length).IsEqualTo(2);
    }

    /// <summary>
    /// Asked once per credential: a second read is the cached answer, and a credential refresh
    /// (which can be somebody else's credential) forgets it.
    /// </summary>
    [Test]
    public async Task The_answer_is_kept_until_the_credential_is_refreshed()
    {
        using var server = new ScriptedApiServer(request => request.Target == string.Format(ReviewPath, "v1")
            ? new ScriptedResponse(201, Review("v1", "jane"))
            : NotFound);
        using var client = await ConnectAsync(server);

        await Assert.That(await client.GetCurrentUsernameAsync()).IsEqualTo("jane");
        await Assert.That(await client.GetCurrentUsernameAsync()).IsEqualTo("jane");
        await Assert.That(Reviews(server).Length).IsEqualTo(1);

        await client.RefreshCredentialsAsync(force: true);

        await Assert.That(await client.GetCurrentUsernameAsync()).IsEqualTo("jane");
        await Assert.That(Reviews(server).Length).IsEqualTo(2);
    }

    [Test]
    public async Task The_review_is_parsed_for_its_username_only()
    {
        static string? Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ClusterClient.ParseSelfSubjectReviewUsername(doc.RootElement);
        }

        await Assert.That(Parse(Review("v1", "system:serviceaccount:ci:deployer"))).IsEqualTo("system:serviceaccount:ci:deployer");
        await Assert.That(Parse("""{"status":{"userInfo":{"groups":["a"]}}}""")).IsNull();
        await Assert.That(Parse("""{"status":{"userInfo":{"username":""}}}""")).IsNull();
        await Assert.That(Parse("""{"status":{"userInfo":{"username":42}}}""")).IsNull();
        await Assert.That(Parse("""{"status":{}}""")).IsNull();
        await Assert.That(Parse("""[]""")).IsNull();
    }
}
