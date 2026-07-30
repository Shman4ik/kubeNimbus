using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Unit tests (no cluster needed) for the Helm release storage format: a Secret
/// whose <c>release</c> value is Kubernetes-base64 over Helm-base64 over gzip
/// over JSON. Getting any layer wrong silently yields "no releases", so the
/// decoding is pinned here rather than only exercised against a live cluster.
/// </summary>
public class HelmReleaseTests
{
    private const string ReleaseJson = """
        {
          "name": "checkout",
          "namespace": "payments",
          "version": 3,
          "info": {
            "status": "deployed",
            "description": "Upgrade complete",
            "last_deployed": "2026-07-20T08:41:02.114Z",
            "notes": "Checkout is available at http://checkout.payments.svc"
          },
          "chart": { "metadata": { "name": "checkout", "version": "1.4.2", "appVersion": "2.14.3" } },
          "config": { "replicaCount": 3, "image": { "tag": "2.14.3" } },
          "manifest": "apiVersion: v1\nkind: Service\nmetadata:\n  name: checkout\n"
        }
        """;

    private static DynamicResource ReleaseSecret(string releaseValue)
    {
        var secret = $$"""
            {
              "apiVersion": "v1",
              "kind": "Secret",
              "type": "helm.sh/release.v1",
              "metadata": { "name": "sh.helm.release.v1.checkout.v3", "namespace": "payments" },
              "data": { "release": "{{releaseValue}}" }
            }
            """;

        using var doc = JsonDocument.Parse(secret);
        return new DynamicResource(doc.RootElement.Clone());
    }

    /// <summary>Encodes exactly the way Helm 3 does, then the way Kubernetes does on top of it.</summary>
    private static string EncodeLikeHelm(string json, bool gzip = true)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (gzip)
        {
            using var compressed = new MemoryStream();
            using (var stream = new GZipStream(compressed, CompressionMode.Compress))
            {
                stream.Write(payload);
            }

            payload = compressed.ToArray();
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToBase64String(payload)));
    }

    [Test]
    public async Task Reads_a_gzipped_release_record()
    {
        using var document = ClusterClient.TryReadReleaseRecord(ReleaseSecret(EncodeLikeHelm(ReleaseJson)));

        await Assert.That(document).IsNotNull();
        var release = ClusterClient.ReadRelease(document!.RootElement, "payments");

        await Assert.That(release.Name).IsEqualTo("checkout");
        await Assert.That(release.Namespace).IsEqualTo("payments");
        await Assert.That(release.Revision).IsEqualTo(3);
        await Assert.That(release.Status).IsEqualTo("deployed");
        await Assert.That(release.ChartName).IsEqualTo("checkout");
        await Assert.That(release.ChartVersion).IsEqualTo("1.4.2");
        await Assert.That(release.AppVersion).IsEqualTo("2.14.3");
        await Assert.That(release.Chart).IsEqualTo("checkout-1.4.2");
        await Assert.That(release.Description).IsEqualTo("Upgrade complete");
        await Assert.That(release.Updated).IsNotNull();
    }

    [Test]
    public async Task Reads_an_uncompressed_release_record()
    {
        using var document = ClusterClient.TryReadReleaseRecord(ReleaseSecret(EncodeLikeHelm(ReleaseJson, gzip: false)));

        await Assert.That(document).IsNotNull();
        await Assert.That(ClusterClient.ReadRelease(document!.RootElement, null).Name).IsEqualTo("checkout");
    }

    [Test]
    public async Task Falls_back_to_the_secret_namespace_when_the_record_omits_one()
    {
        using var document = JsonDocument.Parse("""{ "name": "orphan", "version": 1 }""");

        var release = ClusterClient.ReadRelease(document.RootElement, "fallback-ns");

        await Assert.That(release.Namespace).IsEqualTo("fallback-ns");
        await Assert.That(release.Status).IsEqualTo("unknown");
        await Assert.That(release.Chart).IsEmpty();
    }

    [Test]
    [Arguments("bm90LWJhc2U2NC1pbnNpZGU=")] // decodes to text that isn't base64
    [Arguments("!!!not base64 at all!!!")]
    public async Task Skips_records_it_cannot_unwrap(string releaseValue) =>
        await Assert.That(ClusterClient.TryReadReleaseRecord(ReleaseSecret(releaseValue))).IsNull();

    [Test]
    public async Task Skips_a_secret_with_no_release_payload()
    {
        using var doc = JsonDocument.Parse("""{ "kind": "Secret", "data": { "other": "eA==" } }""");

        await Assert.That(ClusterClient.TryReadReleaseRecord(new DynamicResource(doc.RootElement.Clone()))).IsNull();
    }
}
