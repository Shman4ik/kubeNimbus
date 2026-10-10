using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-34 (#173), the cluster half: FEAT-58's line diff over documents a real API server
/// produced. Every document the diff had ever seen was a ~60-line fixture; the case the
/// feature exists for — lines the <em>cluster</em> adds, which no local diff can contain —
/// and a document long enough to need collapsing had not. The panel builds its text as
/// <c>TextDiff.Between(ToDiffableYaml(live), ToDiffableYaml(previewed))</c>
/// (<c>YamlEditorTabViewModel</c>); these build the same two strings from real answers.
/// </summary>
public partial class ApplyPreviewTextDiffLiveTests
{
    /// <summary>
    /// A Deployment manifest that leaves every defaulted field out, previewed as a create:
    /// the server's dry-run answer carries the defaults, and the line diff of the manifest
    /// against that answer shows each as an added line.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Defaults_the_server_adds_to_a_new_deployment_are_added_lines(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("textdiff");
        var manifest = $$"""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              selector:
                matchLabels:
                  app: {{name}}
              template:
                metadata:
                  labels:
                    app: {{name}}
                spec:
                  containers:
                    - name: app
                      image: {{LiveCluster.Image}}
                      command: ["sleep", "3600"]
            """;

        var preview = await client.PreviewApplyAsync(
            LiveCluster.Deployments, LiveCluster.Namespace, name, manifest, LiveCluster.FieldManager, cancellationToken: ct);
        var diff = TextDiff.Between(LocalYaml(manifest), ResourceDiff.ToDiffableYaml(preview.Previewed.Raw));
        var added = diff.Lines.Where(l => l.Kind == TextDiffKind.Added).Select(l => l.Text.Trim()).ToList();

        foreach (var defaulted in new[]
                 {
                     "progressDeadlineSeconds: 600",
                     "revisionHistoryLimit: 10",
                     "replicas: 1",
                     "maxSurge: 25%",
                     "imagePullPolicy: IfNotPresent",
                     "terminationMessagePath: /dev/termination-log",
                     "schedulerName: default-scheduler",
                     "dnsPolicy: ClusterFirst",
                 })
        {
            await Assert.That(added).Contains(defaulted);
        }

        // Server-written identity is in the answer too; the bookkeeping fields are not.
        await Assert.That(added.Any(l => l.StartsWith("uid: ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(added.Any(l => l.StartsWith("resourceVersion", StringComparison.Ordinal))).IsFalse();
        await Assert.That(await client.ReadResourceAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct)).IsNull();
    }

    /// <summary>
    /// A field a <b>mutating admission webhook</b> writes: cert-manager's webhook stamps the
    /// requesting user onto every CertificateRequest it admits (<c>spec.username</c>,
    /// <c>spec.uid</c>, <c>spec.groups</c>). A dry run calls webhooks that declare no side
    /// effects, so the preview of a create shows those lines — written by a program outside
    /// the API server, named nowhere in the manifest.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_mutating_webhooks_fields_are_added_lines_in_the_preview(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        await CertManager.RequireAsync(client, ct);
        var name = LiveCluster.Named("csr");

        using var key = RSA.Create(2048);
        var csr = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
        var manifest = $$"""
            apiVersion: cert-manager.io/v1
            kind: CertificateRequest
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              issuerRef:
                name: nowhere
              request: {{Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(csr))}}
            """;

        var preview = await client.PreviewApplyAsync(
            CertManager.CertificateRequests, LiveCluster.Namespace, name, manifest, LiveCluster.FieldManager,
            cancellationToken: ct);
        var diff = TextDiff.Between(LocalYaml(manifest), ResourceDiff.ToDiffableYaml(preview.Previewed.Raw));
        var added = diff.Lines.Where(l => l.Kind == TextDiffKind.Added).Select(l => l.Text.Trim()).ToList();

        var user = await client.GetCurrentUsernameAsync(ct);
        await Assert.That(added).Contains($"username: {user}");
        await Assert.That(added.Any(l => l.StartsWith("groups:", StringComparison.Ordinal))).IsTrue();
        await Assert.That(manifest).DoesNotContain("username");
        await Assert.That(await client.ReadResourceAsync(CertManager.CertificateRequests, LiveCluster.Namespace, name, ct)).IsNull();
    }

    /// <summary>
    /// A several-hundred-line document from the server — cert-manager's Certificate CRD —
    /// with two descriptions edited far apart and previewed (a forced dry run: the CRD is
    /// shared and owned by its installer, and nothing is written). The diff is exactly the
    /// two edits; collapsed, it is two hunks of three context lines either side with the rest
    /// counted in separators; and under a cell budget too small for the alignment table the
    /// diff says it is approximate while still showing both edits.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_long_crd_collapses_to_its_two_edits_and_says_when_it_is_approximate(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        await CertManager.RequireAsync(client, ct);
        const string crd = "certificates.cert-manager.io";

        var live = await client.ReadResourceAsync(CertManager.Crds, null, crd, ct);
        // The serializer writes the platform's line ending; the edit is made per line.
        var before = ResourceDiff.ToDiffableYaml(live!.Raw).ReplaceLineEndings(Lf);
        var lines = before.Split('\n');
        await Assert.That(lines.Length).IsGreaterThan(400);

        // One-line plain descriptions only: a scalar wrapped onto the next line cannot be
        // extended by appending to its first line.
        var editable = lines.Select((l, i) => (l, i))
            .Where(x => PlainDescription().IsMatch(x.l) && x.i + 1 < lines.Length && Indent(lines[x.i + 1]) <= Indent(x.l))
            .Select(x => x.i).ToList();
        var first = editable[editable.Count / 3];
        var second = editable[^1];
        await Assert.That(second - first).IsGreaterThan(20);
        lines[first] += " Edited by kubeNimbus live tests.";
        lines[second] += " Edited again.";
        var edited = string.Join('\n', lines);

        var preview = await client.PreviewApplyAsync(
            CertManager.Crds, null, crd, edited, LiveCluster.FieldManager, force: true, cancellationToken: ct);
        var after = ResourceDiff.ToDiffableYaml(preview.Previewed.Raw).ReplaceLineEndings(Lf);

        var diff = TextDiff.Between(before, after);
        await Assert.That(diff.IsApproximate).IsFalse();
        await Assert.That(diff.AddedCount).IsEqualTo(2);
        await Assert.That(diff.RemovedCount).IsEqualTo(2);
        await Assert.That(diff.Lines.Where(l => l.Kind == TextDiffKind.Added).All(l => l.Text.Contains("Edited"))).IsTrue();

        var collapsed = diff.Collapse();
        var kept = collapsed.Where(l => l.Kind != TextDiffKind.Skipped).ToList();
        var skipped = collapsed.Where(l => l.Kind == TextDiffKind.Skipped).ToList();
        await Assert.That(kept.Count).IsEqualTo(2 * (3 + 1 + 1 + 3));
        await Assert.That(skipped.Count).IsBetween(1, 3);
        await Assert.That(kept.Count + skipped.Sum(s => s.SkippedCount)).IsEqualTo(diff.Lines.Count);

        // A budget smaller than the trimmed middle (the span between the two edits).
        var approximate = TextDiff.Between(before, after, cellBudget: 100);
        await Assert.That(approximate.IsApproximate).IsTrue();
        await Assert.That(approximate.Lines.Where(l => l.Kind == TextDiffKind.Added).Count(l => l.Text.Contains("Edited")))
            .IsEqualTo(2);

        // A dry run: the CRD on the server is what it was.
        var unchanged = await client.ReadResourceAsync(CertManager.Crds, null, crd, ct);
        await Assert.That(ResourceDiff.ToDiffableYaml(unchanged!.Raw)).DoesNotContain("Edited");
    }

    /// <summary>A manifest as the diff sees it, through the same serializer as the server's answer.</summary>
    private static string LocalYaml(string manifest)
    {
        using var document = JsonDocument.Parse(YamlJson.ParseYamlToJson(manifest)!.ToJsonString());
        return ResourceDiff.ToDiffableYaml(document.RootElement);
    }

    private const string Lf = "\n";

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [GeneratedRegex(@"^\s+description: [A-Za-z][^'""|>]*$")]
    private static partial Regex PlainDescription();
}
