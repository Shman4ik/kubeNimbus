using System.Net;

namespace KubeNimbus.Core.Tests.Live;

public partial class PrinterColumnsLiveTests
{
    /// <summary>
    /// VER-23 (#166)'s "one cluster with a real cert-manager install": a self-signed Issuer
    /// and a Certificate in this run's namespace, then cert-manager's own Certificate list
    /// compared with the server's Table column for column. Its <c>Ready</c> column is
    /// <c>.status.conditions[?(@.type == "Ready")].status</c> — a condition filter written
    /// with spaces around <c>==</c>, by people who are not this repository — and its
    /// <c>Issuer</c> and <c>Status</c> columns are <c>priority: 1</c> (<c>-o wide</c>).
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task Cert_managers_certificate_list_matches_kubectl_get_including_its_ready_condition(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        await CertManager.RequireAsync(client, ct);
        var issuer = LiveCluster.Named("selfsigned");
        var name = LiveCluster.Named("cert");

        await LiveCluster.WaitUntilAsync(async () =>
        {
            try
            {
                await LiveCluster.ApplyAsync(client, CertManager.Issuers, issuer, $$"""
                    apiVersion: cert-manager.io/v1
                    kind: Issuer
                    metadata:
                      name: {{issuer}}
                      namespace: {{LiveCluster.Namespace}}
                    spec:
                      selfSigned: {}
                    """, ct);
                return true;
            }
            catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.InternalServerError)
            {
                // The webhook's serving certificate is still being injected after an install.
                return false;
            }
        }, TimeSpan.FromSeconds(60), "cert-manager's webhook to admit an Issuer", ct);

        await LiveCluster.ApplyAsync(client, CertManager.Certificates, name, $$"""
            apiVersion: cert-manager.io/v1
            kind: Certificate
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              secretName: {{name}}-tls
              commonName: {{name}}.live.kubenimbus.test
              dnsNames: [{{name}}.live.kubenimbus.test]
              issuerRef:
                name: {{issuer}}
                kind: Issuer
            """, ct);

        await LiveCluster.WaitUntilAsync(async () =>
        {
            var cert = await client.ReadResourceAsync(CertManager.Certificates, LiveCluster.Namespace, name, ct);
            return PodDetails.Conditions(cert!).Any(c => c.Type == "Ready" && c.Status == "True");
        }, TimeSpan.FromSeconds(90), "the Certificate to be issued", ct);

        var columns = await client.GetPrinterColumnsAsync(CertManager.Certificates, ct);
        await Assert.That(columns.Select(c => (c.Name, c.Priority)).ToArray())
            .IsEquivalentTo(new[] { ("Ready", 0), ("Secret", 0), ("Issuer", 1), ("Status", 1), ("Age", 0) });

        var result = await CompareAsync(client, CertManager.Certificates, LiveCluster.Namespace, ct);
        await Assert.That(result.Rows).IsGreaterThanOrEqualTo(1);
        await Assert.That(string.Join('\n', result.Problems)).IsEmpty();

        // The named cell, both ways: kubectl's READY is "True", and so is the app's.
        var cert = await client.ReadResourceAsync(CertManager.Certificates, LiveCluster.Namespace, name, ct);
        using var table = await LiveCluster.GetTableAsync(client, CertManager.Certificates.CollectionPath(LiveCluster.Namespace), ct);
        var row = table.Row($"{LiveCluster.Namespace}/{name}");
        await Assert.That(ServerTable.Text(row[table.IndexOf("Ready")])).IsEqualTo("True");
        await Assert.That(PrinterColumns.Evaluate(columns.Single(c => c.Name == "Ready"), cert!.Raw)).IsEqualTo("True");
        await Assert.That(PrinterColumns.Evaluate(columns.Single(c => c.Name == "Issuer"), cert.Raw)).IsEqualTo(issuer);
    }
}
