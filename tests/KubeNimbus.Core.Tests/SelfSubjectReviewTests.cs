using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-56: the access review's "Signed in as" line asks the server who this connection is
/// (<c>kubectl auth whoami</c>) and states each way it can fail to say. Over real HTTP against
/// a stand-in API server, as <see cref="ArgoSyncIdentityTests"/> does for the sync's half.
/// </summary>
public class SelfSubjectReviewTests
{
    private const string ReviewPath = "/apis/authentication.k8s.io/{0}/selfsubjectreviews";

    private static readonly ScriptedResponse NotFound = new(404,
        """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""");

    private const string Review = """
        {"kind":"SelfSubjectReview","apiVersion":"authentication.k8s.io/v1","metadata":{},
         "status":{"userInfo":{"username":"arn:aws:iam::111122223333:role/dev","uid":"aws-iam-authenticator:111122223333:AROA",
                               "groups":["system:authenticated","payments-oncall"],
                               "extra":{"accessKeyId":["ASIAEXAMPLE"],"sessionName":["jane"]}}}}
        """;

    private static async Task<ClusterClient> ConnectAsync(ScriptedApiServer server) =>
        await ClusterClient.ConnectAsync(ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null)));

    private static int Reviews(ScriptedApiServer server) =>
        server.Requests.Count(r => r.Target.Contains("selfsubjectreviews", StringComparison.Ordinal));

    [Test]
    public async Task The_server_names_the_user_and_its_groups_and_nothing_else_is_read()
    {
        using var server = new ScriptedApiServer(request => request.Target == string.Format(ReviewPath, "v1")
            ? new ScriptedResponse(201, Review)
            : NotFound);
        using var client = await ConnectAsync(server);

        var identity = await client.ReviewSelfSubjectAsync();

        await Assert.That(identity.Outcome).IsEqualTo(SelfSubjectReviewOutcome.Answered);
        await Assert.That(identity.Username).IsEqualTo("arn:aws:iam::111122223333:role/dev");
        await Assert.That(identity.Groups).IsEquivalentTo(["system:authenticated", "payments-oncall"]);
        await Assert.That(identity.Detail).IsNull();
        await Assert.That(identity.ToString()).DoesNotContain("ASIAEXAMPLE");
        await Assert.That(identity.ToString()).DoesNotContain("AROA");
    }

    /// <summary>The pane asks on every load: it must not reuse the sync's cached name, which carries no groups.</summary>
    [Test]
    public async Task Every_review_is_asked_afresh()
    {
        using var server = new ScriptedApiServer(request => request.Target == string.Format(ReviewPath, "v1")
            ? new ScriptedResponse(201, Review)
            : NotFound);
        using var client = await ConnectAsync(server);

        await client.ReviewSelfSubjectAsync();
        await client.ReviewSelfSubjectAsync();

        await Assert.That(Reviews(server)).IsEqualTo(2);
    }

    /// <summary>Older than 1.27, neither version is served: a stated state, not an error.</summary>
    [Test]
    public async Task A_server_serving_neither_version_says_so()
    {
        using var server = new ScriptedApiServer(_ => NotFound);
        using var client = await ConnectAsync(server);

        var identity = await client.ReviewSelfSubjectAsync();

        await Assert.That(identity.Outcome).IsEqualTo(SelfSubjectReviewOutcome.NotServed);
        await Assert.That(identity.Username).IsNull();
        await Assert.That(Reviews(server)).IsEqualTo(2);
    }

    [Test]
    public async Task A_refusal_carries_the_servers_own_sentence()
    {
        using var server = new ScriptedApiServer(_ => new ScriptedResponse(403,
            """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Forbidden","code":403,"message":"selfsubjectreviews.authentication.k8s.io is forbidden: User \"system:anonymous\" cannot create resource"}"""));
        using var client = await ConnectAsync(server);

        var identity = await client.ReviewSelfSubjectAsync();

        await Assert.That(identity.Outcome).IsEqualTo(SelfSubjectReviewOutcome.Refused);
        await Assert.That(identity.Detail).StartsWith("selfsubjectreviews.authentication.k8s.io is forbidden");
    }

    [Test]
    public async Task No_answer_is_a_failure_with_the_reason_not_an_exception()
    {
        using var client = await ClusterClient.ConnectAsync(
            ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig("https://127.0.0.1:1", null)));

        var identity = await client.ReviewSelfSubjectAsync();

        await Assert.That(identity.Outcome).IsEqualTo(SelfSubjectReviewOutcome.Failed);
        await Assert.That(identity.Detail).IsNotNull();
    }

    [Test]
    public async Task Groups_are_read_in_order_and_anything_odd_is_skipped()
    {
        static IReadOnlyList<string> Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ClusterClient.ParseSelfSubjectReviewGroups(doc.RootElement);
        }

        await Assert.That(Parse(Review)).IsEquivalentTo(["system:authenticated", "payments-oncall"]);
        await Assert.That(Parse("""{"status":{"userInfo":{"groups":["b","",42,"a"]}}}""")).IsEquivalentTo(["b", "a"]);
        await Assert.That(Parse("""{"status":{"userInfo":{"username":"x"}}}""")).IsEmpty();
        await Assert.That(Parse("""{"status":{"userInfo":{"groups":"admins"}}}""")).IsEmpty();
        await Assert.That(Parse("""[]""")).IsEmpty();
    }
}
