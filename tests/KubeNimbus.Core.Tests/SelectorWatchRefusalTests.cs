using System.Net;
using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #354: a selector watch — <see cref="ClusterClient.WatchResourceAsync"/> called directly, not
/// through <see cref="ClusterClient.Watches"/> (a workload's pods, a Service's endpoints, the
/// pods on a node) — ends on a 403 with a <see cref="WatchRefusedException"/> carrying the
/// server's sentence, after one request, instead of retrying it every backoff behind
/// "Watch connection lost". The shared-watch half is <c>WatchCacheTests</c>.
/// </summary>
public class SelectorWatchRefusalTests
{
    private const string Sentence =
        "pods is forbidden: User \"viewer\" cannot list resource \"pods\" in API group \"\" in the namespace \"team\"";

    private static string Forbidden => $$"""{"kind":"Status","code":403,"reason":"Forbidden","message":{{JsonSerializer.Serialize(Sentence)}}}""";

    private static async Task<(Exception? Ended, List<Exception> Warnings)> ReadToEndAsync(ClusterClient client)
    {
        var warnings = new List<Exception>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var _ in client.WatchResourceAsync(
                ResourceDescriptor.Pods, "team", connectionLost: warnings.Add,
                cancellationToken: timeout.Token, fieldSelector: "metadata.name=web"))
            {
            }
        }
        catch (Exception ex)
        {
            return (ex, warnings);
        }

        return (null, warnings);
    }

    /// <summary>
    /// Mutation-checked: without the 403 catch in <c>ClusterClient.PumpAsync</c> the stream runs
    /// until the test's timeout (an <see cref="OperationCanceledException"/>) and the list is
    /// asked for again after each backoff.
    /// </summary>
    [Test]
    public async Task A_refused_selector_list_ends_the_stream_after_one_request()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
            await response.WriteAsync(Forbidden, HttpStatusCode.Forbidden));
        using var client = server.Connect();

        var (ended, warnings) = await ReadToEndAsync(client);

        await Assert.That(ended).IsTypeOf<WatchRefusedException>();
        await Assert.That(ended!.Message).IsEqualTo(Sentence);
        await Assert.That(warnings).IsEmpty();

        // The first retry would have come after a one-second backoff.
        await Task.Delay(1500);
        await Assert.That(server.Requests.Count(r => r.Path == "/api/v1/namespaces/team/pods")).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_selector_watch_after_an_allowed_list_ends_the_stream_too()
    {
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer(async (request, response) =>
        {
            if (request.IsWatch)
            {
                await response.WriteAsync(Forbidden, HttpStatusCode.Forbidden);
            }
            else
            {
                await response.WriteAsync("""{"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"7"},"items":[]}""");
            }
        });
        using var client = server.Connect();

        var (ended, warnings) = await ReadToEndAsync(client);

        await Assert.That(ended).IsTypeOf<WatchRefusedException>();
        await Assert.That(warnings).IsEmpty();
        await Task.Delay(1500);
        await Assert.That(server.Requests.Count(r => r is { Path: "/api/v1/namespaces/team/pods", IsWatch: true })).IsEqualTo(1);
    }

    /// <summary>
    /// The same against the sandbox's real RBAC: its own kubeconfig, impersonating a service
    /// account no role is bound to (narrow RBAC at its narrowest), so the API server refuses
    /// the list. Nothing is created on the cluster, so the shared sandbox is not touched.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task A_live_selector_watch_the_cluster_refuses_ends_with_the_servers_sentence(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        // Only the users entry has a bare "user:" line; a context's "user: <name>" has a value.
        var text = await File.ReadAllTextAsync(context.KubeconfigPath, ct);
        var narrowed = new System.Text.RegularExpressions.Regex(@"(?m)^(\s*)user:[ \t]*\r?$")
            .Replace(text, "$1user:\n$1  as: system:serviceaccount:default:kn-no-role-at-all", 1);
        if (narrowed == text)
        {
            Skip.Test("the sandbox kubeconfig has no users entry this test can add impersonation to");
        }

        var directory = Directory.CreateTempSubdirectory("kubenimbus-narrow");
        try
        {
            var path = Path.Combine(directory.FullName, "kubeconfig.yaml");
            await File.WriteAllTextAsync(path, narrowed, ct);
            var narrowContext = (await Kubeconfig.LoadContextsAsync([path])).First(c => c.Name == context.Name);
            using var client = await ClusterClient.ConnectAsync(narrowContext, ct);

            var warnings = new List<Exception>();
            Exception? ended = null;
            try
            {
                await foreach (var _ in client.WatchResourceAsync(
                    ResourceDescriptor.Pods, "default", connectionLost: warnings.Add,
                    cancellationToken: ct, fieldSelector: "metadata.name=web"))
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ended = ex;
            }

            await Assert.That(ended).IsTypeOf<WatchRefusedException>();
            await Assert.That(ended!.Message).Contains("kn-no-role-at-all");
            await Assert.That(warnings).IsEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
