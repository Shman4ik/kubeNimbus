using System.Net;
using System.Text.Json;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// What a real API server answered for a path with a dot segment in the name — the request
/// the app used to send for an owner reference or Event naming <c>..</c> — and that the
/// app sends nothing of the kind now. Read-only: it creates nothing, so it does not use the
/// shared live-test namespace.
/// </summary>
public class PathSegmentLiveTests
{
    [Test]
    [Timeout(60_000)]
    public async Task A_dot_segment_name_reached_a_different_object_and_now_reaches_nothing(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        using var client = await ClusterClient.ConnectAsync(context!, ct);
        client.DiscoveryCacheDirectory = Path.Combine(Path.GetTempPath(), "kubenimbus-live-tests", "discovery");

        // The old request: EscapeDataString leaves "..", and the URI resolver collapses it, so
        // "a pod called .. in kube-system" was a GET of the namespace kube-system itself.
        using (var response = await client.SendRequestAsync(
                   HttpMethod.Get, $"api/v1/namespaces/kube-system/pods/{Uri.EscapeDataString("..")}", null,
                   HttpCompletionOption.ResponseContentRead, ct))
        {
            await Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath).IsEqualTo("/api/v1/namespaces/kube-system/");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            await Assert.That(body.RootElement.GetProperty("kind").GetString()).IsEqualTo("Namespace");
        }

        // A namespace of ".." made a namespaced read cluster-wide: a list of every pod.
        using (var response = await client.SendRequestAsync(
                   HttpMethod.Get, $"api/v1/namespaces/{Uri.EscapeDataString("..")}/pods", null,
                   HttpCompletionOption.ResponseContentRead, ct))
        {
            await Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath).IsEqualTo("/api/v1/pods");
        }

        // Now: no request, no object, and the reference resolves to nothing.
        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.Pods, "kube-system", "..", ct)).IsNull();
        await Assert.That(await client.ResolveOwnerAsync(new OwnerRef("v1", "Pod", "..", null, false), "kube-system", ct)).IsNull();
        await Assert.That(await client.ResolveOwnerAsync(new OwnerRef("v1", "Pod", "", null, false), "kube-system", ct)).IsNull();
    }
}
