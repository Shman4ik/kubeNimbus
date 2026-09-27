using System.Net;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The status code a real API server refuses a misspelled field with, replayed against the
/// loopback stand-in so the classification is pinned on machines with no cluster too.
/// </summary>
/// <remarks>
/// <c>ApplyPreviewHttpTests</c> modelled the refusal as a 400. A real server (k3s v1.33.4,
/// see <c>Live/ApplyLiveTests</c>) answers a server-side apply carrying an unknown field with
/// the body below — verbatim, apart from the object's name — and <b>HTTP 500</b>, because
/// the apply handler fails building the typed patch and reports that as an internal error.
/// Classified by status code alone, that fell through to a generic "Apply failed: … (500
/// Internal Server Error)" instead of the editor's rejected-field state.
/// </remarks>
public class ApplyRefusalStatusTests
{
    private const string DeploymentPath = "/apis/apps/v1/namespaces/shop/deployments/web";

    private static readonly ResourceDescriptor Deployments = new(
        "apps", "v1", "Deployment", "deployments", "deployment", true, [], []);

    private const string RealServerRefusal = """
        {"kind":"Status","apiVersion":"v1","metadata":{},"status":"Failure","message":"failed to create typed patch object (shop/web; apps/v1, Kind=Deployment): .spec.replicaz: field not declared in schema","code":500}
        """;

    private const string ApplyYaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: web
          namespace: shop
        spec:
          replicaz: 5
        """;

    [Test]
    public async Task A_500_naming_an_undeclared_field_is_a_rejected_field_on_the_apply()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.InternalServerError, RealServerRefusal);
        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<ServerSideApplyValidationException>(
            async () => await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.Message).Contains(".spec.replicaz: field not declared in schema");
        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(client.SupportsFieldValidation).IsTrue();
    }

    [Test]
    public async Task A_500_naming_an_undeclared_field_is_a_rejected_field_on_the_preview()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("GET", DeploymentPath, HttpStatusCode.NotFound,
            """{"kind":"Status","status":"Failure","reason":"NotFound","code":404}""");
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.InternalServerError, RealServerRefusal);
        using var client = server.Connect();

        await Assert.ThrowsAsync<ServerSideApplyValidationException>(
            async () => await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));
    }

    /// <summary>Any other 500 is still an ordinary server failure, not a field the user must fix.</summary>
    [Test]
    public async Task Any_other_500_stays_a_server_failure()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.InternalServerError,
            """{"kind":"Status","apiVersion":"v1","metadata":{},"status":"Failure","message":"Internal error occurred: etcdserver: request timed out","code":500}""");
        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<KubernetesApiException>(
            async () => await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(thrown.ServerMessage).IsEqualTo("Internal error occurred: etcdserver: request timed out");
    }
}
