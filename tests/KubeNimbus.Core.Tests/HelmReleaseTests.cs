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

    /// <summary>
    /// A decompression bomb: <paramref name="mebibytes"/> MiB of zeros, which gzip squeezes
    /// into a few tens of KiB — a Secret anyone with write access to Secrets can create.
    /// </summary>
    private static string Bomb(int mebibytes)
    {
        using var compressed = new MemoryStream();
        using (var stream = new GZipStream(compressed, CompressionLevel.SmallestSize))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < mebibytes; i++)
            {
                stream.Write(zeros);
            }
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToBase64String(compressed.ToArray())));
    }

    private static DynamicResource LabelledSecret(string releaseValue) =>
        new(JsonDocument.Parse($$$"""
            {"apiVersion":"v1","kind":"Secret","type":"helm.sh/release.v1",
             "metadata":{"name":"sh.helm.release.v1.checkout.v4","namespace":"payments",
                         "labels":{"name":"checkout","owner":"helm","status":"deployed","version":"4"}},
             "data":{"release":"{{{releaseValue}}}"}}
            """).RootElement.Clone());

    [Test]
    public async Task A_decompression_bomb_stops_at_the_cap_and_says_why()
    {
        var bomb = Bomb(200);
        await Assert.That(bomb.Length).IsLessThan(ClusterClient.MaxEncodedReleaseChars);

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var document = ClusterClient.TryReadReleaseRecord(LabelledSecret(bomb), out var problem);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(document).IsNull();
        await Assert.That(problem).Contains("larger than 32 MiB once decompressed");

        // The cap is what it costs, not the 200 MiB it expands to (a doubling buffer allocates
        // about twice what it ends up holding; uncapped, this was 200 MiB and more).
        await Assert.That(allocated).IsLessThan(3L * ClusterClient.MaxDecompressedReleaseBytes);

        var release = ClusterClient.UnreadableRelease(LabelledSecret(bomb), problem!);
        await Assert.That(release.Name).IsEqualTo("checkout");
        await Assert.That(release.Revision).IsEqualTo(4);
        await Assert.That(release.Status).IsEqualTo(ClusterClient.UnreadableStatus);
        await Assert.That(release.Description).IsEqualTo(problem);
    }

    [Test]
    public async Task A_record_too_large_for_any_Secret_is_not_decoded_at_all()
    {
        var huge = new string('A', ClusterClient.MaxEncodedReleaseChars + 4);

        using var document = ClusterClient.TryReadReleaseRecord(LabelledSecret(huge), out var problem);

        await Assert.That(document).IsNull();
        await Assert.That(problem).Contains("more than a Secret can hold");
    }

    [Test]
    public async Task Gunzip_returns_everything_under_the_cap_and_nothing_over_it()
    {
        using var compressed = new MemoryStream();
        using (var stream = new GZipStream(compressed, CompressionMode.Compress))
        {
            stream.Write(new byte[1000]);
        }

        await Assert.That(ClusterClient.Gunzip(compressed.ToArray(), 1000)!.Length).IsEqualTo(1000);
        await Assert.That(ClusterClient.Gunzip(compressed.ToArray(), 999)).IsNull();
    }

    [Test]
    public async Task Opening_a_release_whose_record_is_a_bomb_says_why_it_cannot_be_read()
    {
        var bomb = Bomb(200);
        await using var server = new NodeWatchHttpTests.ConcurrentStubServer((_, response) => response.WriteAsync($$$"""
            {"kind":"SecretList","apiVersion":"v1","metadata":{"resourceVersion":"1"},"items":[
              {"apiVersion":"v1","kind":"Secret","type":"helm.sh/release.v1",
               "metadata":{"name":"sh.helm.release.v1.checkout.v4","namespace":"payments",
                           "labels":{"name":"checkout","owner":"helm","version":"4"}},
               "data":{"release":"{{{bomb}}}"}},
              {"apiVersion":"v1","kind":"Secret","type":"helm.sh/release.v1",
               "metadata":{"name":"sh.helm.release.v1.checkout.v3","namespace":"payments"},
               "data":{"release":"{{{EncodeLikeHelm(ReleaseJson)}}}"}}]}
            """));
        using var client = server.Connect();

        var releases = await client.ListHelmReleasesAsync("payments");
        await Assert.That(releases.Single().Status).IsEqualTo(ClusterClient.UnreadableStatus);
        await Assert.That(releases.Single().Revision).IsEqualTo(4);

        var error = await Assert.That(() => client.GetHelmReleaseAsync("payments", "checkout")).Throws<InvalidDataException>();
        await Assert.That(error!.Message).Contains("Revision 4 of checkout");

        // The older revision is still readable on its own.
        var revision3 = await client.GetHelmReleaseAsync("payments", "checkout", revision: 3);
        await Assert.That(revision3!.Release.Revision).IsEqualTo(3);
    }

    [Test]
    public async Task Skips_a_secret_with_no_release_payload()
    {
        using var doc = JsonDocument.Parse("""{ "kind": "Secret", "data": { "other": "eA==" } }""");

        await Assert.That(ClusterClient.TryReadReleaseRecord(new DynamicResource(doc.RootElement.Clone()))).IsNull();
    }
}
