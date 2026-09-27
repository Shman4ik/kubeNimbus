using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-30: the certificates a Secret carries, read with the platform's own X.509 loader
/// over certificates generated here — a CA and a leaf it signed, bundled the way cert-manager
/// writes <c>tls.crt</c>.
/// </summary>
public class TlsCertificatesTests
{
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) Chain(DateTimeOffset notAfter)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Test Issuing CA, O=kubeNimbus tests", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(5));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=shop.example", leafKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("shop.example");
        names.AddDnsName("www.shop.example");
        names.AddIpAddress(IPAddress.Parse("10.0.0.7"));
        leafRequest.CertificateExtensions.Add(names.Build());
        var leaf = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-10), notAfter, [1, 2, 3, 4]);
        return (ca, leaf);
    }

    [Test]
    public async Task A_pem_bundle_reads_as_leaf_then_ca_with_names_and_validity()
    {
        var notAfter = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(40).Date, TimeSpan.Zero);
        var (ca, leaf) = Chain(notAfter);
        var bundle = Encoding.ASCII.GetBytes(leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n");

        var read = TlsCertificates.Read(bundle);

        await Assert.That(read.Count).IsEqualTo(2);
        await Assert.That(read[0].SubjectCommonName).IsEqualTo("shop.example");
        await Assert.That(read[0].IssuerCommonName).IsEqualTo("Test Issuing CA");
        await Assert.That(read[0].SubjectAlternativeNames).IsEquivalentTo(["shop.example", "www.shop.example", "10.0.0.7"]);
        await Assert.That(read[0].NotAfter).IsEqualTo(notAfter);
        await Assert.That(read[0].IsCertificateAuthority).IsFalse();
        await Assert.That(read[0].IsSelfIssued).IsFalse();
        await Assert.That(read[1].IsCertificateAuthority).IsTrue();
        await Assert.That(read[1].IsSelfIssued).IsTrue();
    }

    [Test]
    public async Task A_single_der_certificate_reads_too()
    {
        var (_, leaf) = Chain(DateTimeOffset.UtcNow.AddDays(5));

        var read = TlsCertificates.Read(leaf.RawData);

        await Assert.That(read.Single().SubjectCommonName).IsEqualTo("shop.example");
    }

    /// <summary>
    /// A private key in the same bundle is skipped, not decoded: only CERTIFICATE blocks
    /// are read, which is what lets the card sit outside the Reveal toggle.
    /// </summary>
    [Test]
    public async Task Non_certificate_blocks_and_garbage_are_skipped()
    {
        var (_, leaf) = Chain(DateTimeOffset.UtcNow.AddDays(5));
        var text = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n"
            + leaf.ExportCertificatePem()
            + "\n-----BEGIN CERTIFICATE-----\nbm90IGEgY2VydA==\n-----END CERTIFICATE-----\n";

        var read = TlsCertificates.Read(Encoding.ASCII.GetBytes(text));

        await Assert.That(read.Count).IsEqualTo(1);
        await Assert.That(TlsCertificates.Read("not a certificate"u8).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("tls.crt", "kubernetes.io/tls", true)]
    [Arguments("ca.crt", "Opaque", true)]
    [Arguments("client.crt", null, true)]
    [Arguments("tls.key", "kubernetes.io/tls", false)]
    [Arguments("password", "Opaque", false)]
    public async Task Only_certificate_keys_are_read(string key, string? type, bool expected)
    {
        await Assert.That(TlsCertificates.IsCertificateKey(key, type)).IsEqualTo(expected);
    }
}
