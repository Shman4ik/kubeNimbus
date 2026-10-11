using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Two things the first live pass against a real Argo CD (v3.5.4, VER-15) settled, pinned here
/// so they cannot drift back without a cluster to catch them:
/// <list type="bullet">
/// <item>a sync carries the Application's own <c>syncOptions</c> and <c>retry</c>, as Argo's API
/// server copies them — without them a <c>CreateNamespace=true</c> Application failed with
/// "namespaces … not found";</item>
/// <item>terminate (#193) is a JSON patch of the object with a <c>test</c> of the phase, never a
/// patch of <c>/status</c>, which Argo's CRD does not have (a 404 against the real server).</item>
/// </list>
/// Over real HTTP against a stand-in API server, so what is sent is observed.
/// </summary>
public class ArgoTerminateAndSyncOptionsTests
{
    private static readonly ResourceDescriptor Applications =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", Namespaced: true, ShortNames: [], Categories: []);

    private const string ApplicationPath = "/apis/argoproj.io/v1alpha1/namespaces/argocd/applications/checkout";

    private const string ApplicationWithOptions = """
        {"apiVersion":"argoproj.io/v1alpha1","kind":"Application",
         "metadata":{"name":"checkout","namespace":"argocd"},
         "spec":{"project":"default","syncPolicy":{
            "syncOptions":["CreateNamespace=true","ServerSideApply=true"],
            "retry":{"limit":3,"backoff":{"duration":"5s","factor":2}}}}}
        """;

    private static readonly ScriptedResponse NotFound = new(404,
        """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""");

    private static async Task<ClusterClient> ConnectAsync(ScriptedApiServer server) =>
        await ClusterClient.ConnectAsync(ApiServerTlsTests.Context(ApiServerTlsTests.Kubeconfig(server.Url, null)));

    [Test]
    public async Task Sync_patch_copies_the_applications_sync_options_and_retry_into_the_operation()
    {
        using var app = JsonDocument.Parse(ApplicationWithOptions);
        var policy = ArgoCd.SyncPolicyOf(new DynamicResource(app.RootElement.Clone()));

        using var patch = JsonDocument.Parse(ArgoCd.SyncPatch(prune: false, "jane", policy));
        var operation = patch.RootElement.GetProperty("operation");

        var options = operation.GetProperty("sync").GetProperty("syncOptions").EnumerateArray().Select(o => o.GetString()!).ToArray();
        await Assert.That(options).IsEquivalentTo(["CreateNamespace=true", "ServerSideApply=true"]);
        await Assert.That(operation.GetProperty("retry").GetProperty("limit").GetInt32()).IsEqualTo(3);
        await Assert.That(operation.GetProperty("retry").GetProperty("backoff").GetProperty("duration").GetString()).IsEqualTo("5s");
    }

    [Test]
    public async Task Sync_patch_has_no_options_when_the_application_sets_none()
    {
        using var patch = JsonDocument.Parse(ArgoCd.SyncPatch(prune: true));
        var operation = patch.RootElement.GetProperty("operation");

        await Assert.That(operation.TryGetProperty("retry", out _)).IsFalse();
        await Assert.That(operation.GetProperty("sync").TryGetProperty("syncOptions", out _)).IsFalse();
    }

    [Test]
    public async Task A_sync_reads_the_application_and_sends_its_options()
    {
        using var server = new ScriptedApiServer(request => request.Target switch
        {
            ApplicationPath => new ScriptedResponse(200, ApplicationWithOptions),
            _ => NotFound,
        });
        using var client = await ConnectAsync(server);

        await client.SyncArgoApplicationAsync(Applications, "argocd", "checkout");

        var patch = server.Requests.Single(r => r.Method == "PATCH");
        using var body = JsonDocument.Parse(patch.Body);
        var options = body.RootElement.GetProperty("operation").GetProperty("sync").GetProperty("syncOptions")
            .EnumerateArray().Select(o => o.GetString()!).ToArray();
        await Assert.That(options).Contains("CreateNamespace=true");
    }

    [Test]
    public async Task Terminate_is_a_conditional_json_patch_of_the_phase()
    {
        using var doc = JsonDocument.Parse(ArgoCd.TerminatePatch);
        var ops = doc.RootElement.EnumerateArray().ToArray();

        await Assert.That(ops.Length).IsEqualTo(2);
        await Assert.That(ops[0].GetProperty("op").GetString()).IsEqualTo("test");
        await Assert.That(ops[0].GetProperty("value").GetString()).IsEqualTo("Running");
        await Assert.That(ops[1].GetProperty("op").GetString()).IsEqualTo("replace");
        await Assert.That(ops[1].GetProperty("path").GetString()).IsEqualTo("/status/operationState/phase");
        await Assert.That(ops[1].GetProperty("value").GetString()).IsEqualTo("Terminating");
    }

    [Test]
    public async Task Terminate_patches_the_object_not_a_status_subresource()
    {
        using var server = new ScriptedApiServer(request => request.Target switch
        {
            ApplicationPath => new ScriptedResponse(200, "{}"),
            _ => NotFound,
        });
        using var client = await ConnectAsync(server);

        var accepted = await client.TerminateArgoOperationAsync(Applications, "argocd", "checkout");

        var patch = server.Requests.Single(r => r.Method == "PATCH");
        await Assert.That(accepted).IsTrue();
        await Assert.That(patch.Target).IsEqualTo(ApplicationPath);
        await Assert.That(patch.Values("Content-Type").Single()).StartsWith("application/json-patch+json");
        await Assert.That(patch.Body).IsEqualTo(ArgoCd.TerminatePatch);
    }

    /// <summary>A sync that finished before the request: the test op fails, 422, and that is "nothing to stop", not an error.</summary>
    [Test]
    public async Task Terminate_reports_nothing_to_stop_when_the_server_refuses_the_test()
    {
        using var server = new ScriptedApiServer(_ => new ScriptedResponse(422,
            """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"the server rejected our request due to an error in our request","reason":"Invalid","code":422}"""));
        using var client = await ConnectAsync(server);

        await Assert.That(await client.TerminateArgoOperationAsync(Applications, "argocd", "checkout")).IsFalse();
    }

    [Test]
    public async Task Terminate_lets_a_403_through_as_the_servers_sentence()
    {
        using var server = new ScriptedApiServer(_ => new ScriptedResponse(403,
            """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"applications.argoproj.io \"checkout\" is forbidden","reason":"Forbidden","code":403}"""));
        using var client = await ConnectAsync(server);

        await Assert.That(async () => await client.TerminateArgoOperationAsync(Applications, "argocd", "checkout"))
            .Throws<KubernetesApiException>();
    }

    [Test]
    public async Task Only_a_running_operation_can_be_terminated()
    {
        static ArgoApplication WithPhase(string phase) => ArgoCd.ReadApplication(new DynamicResource(JsonDocument.Parse(
            """{"metadata":{"name":"a","namespace":"argocd"},"status":{"operationState":{"phase":"PHASE"}}}"""
                .Replace("PHASE", phase, StringComparison.Ordinal)).RootElement.Clone()));

        await Assert.That(WithPhase("Running").CanTerminate).IsTrue();
        await Assert.That(WithPhase("Terminating").CanTerminate).IsFalse();
        await Assert.That(WithPhase("Succeeded").CanTerminate).IsFalse();
        await Assert.That(WithPhase("Failed").CanTerminate).IsFalse();
    }
}
