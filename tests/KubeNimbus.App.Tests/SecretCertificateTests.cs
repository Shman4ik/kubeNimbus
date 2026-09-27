using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-30 in the YAML editor: a Secret carrying a certificate says whose it is and when
/// it expires — worded, and coloured by how soon — without the Reveal toggle, and without
/// reading the private key beside it.
/// </summary>
public class SecretCertificateTests
{
    private static readonly ResourceDescriptor Secrets =
        new("", "v1", "Secret", "secrets", "secret", Namespaced: true, ShortNames: [], Categories: []);

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static string Pem(DateTimeOffset notBefore, DateTimeOffset notAfter, string cn = "shop.example")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.ExportCertificatePem();
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    private static YamlEditorTabViewModel Editor(string type, string crt, string key = "placeholder")
    {
        TestObjects.RedirectStores();
        var yaml = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: shop-tls
              namespace: shop
            type: {type}
            data:
              tls.crt: {B64(crt)}
              tls.key: {B64(key)}
            """;
        var editor = new YamlEditorTabViewModel(null, Secrets, "shop", "shop-tls", yaml);
        editor.RefreshCertificates(Now);
        return editor;
    }

    [Test]
    public async Task A_tls_secret_says_whose_certificate_it_is_and_when_it_expires()
    {
        var editor = Editor("kubernetes.io/tls", Pem(Now.AddDays(-60), Now.AddDays(90)));

        await Assert.That(editor.HasCertificates).IsTrue();
        await Assert.That(editor.CertificateSummary).IsEqualTo("shop.example · expires in 90 days");
        await Assert.That(editor.CertificateHealth).IsEqualTo(ResourceHealth.Ok);
        await Assert.That(editor.IsSecretValuesRevealed).IsFalse();
        await Assert.That(editor.Certificates.Single().IssuerText).IsEqualTo("self-signed");
    }

    [Test]
    public async Task The_last_thirty_days_are_a_warning()
    {
        var editor = Editor("kubernetes.io/tls", Pem(Now.AddDays(-60), Now.AddDays(12)));

        await Assert.That(editor.CertificateSummary).IsEqualTo("shop.example · expires in 12 days");
        await Assert.That(editor.CertificateHealth).IsEqualTo(ResourceHealth.Warn);
    }

    [Test]
    public async Task An_expired_certificate_is_an_error_that_says_how_long_ago()
    {
        var editor = Editor("kubernetes.io/tls", Pem(Now.AddDays(-90), Now.AddDays(-3)));

        await Assert.That(editor.CertificateSummary).IsEqualTo("shop.example · expired 3 days ago");
        await Assert.That(editor.CertificateHealth).IsEqualTo(ResourceHealth.Error);
    }

    [Test]
    public async Task A_certificate_not_yet_valid_is_an_error_too()
    {
        var editor = Editor("kubernetes.io/tls", Pem(Now.AddDays(2), Now.AddDays(92)));

        await Assert.That(editor.CertificateHealth).IsEqualTo(ResourceHealth.Error);
        await Assert.That(editor.CertificateSummary!).Contains("not valid until");
    }

    /// <summary>
    /// The key half is never read: a certificate smuggled into tls.key does not appear,
    /// and neither does the key's own text anywhere in the card.
    /// </summary>
    [Test]
    public async Task The_private_key_is_never_read()
    {
        var crt = Pem(Now.AddDays(-1), Now.AddDays(100), cn: "leaf.example");
        var inKey = Pem(Now.AddDays(-1), Now.AddDays(100), cn: "hidden-in-the-key.example");
        var editor = Editor("kubernetes.io/tls", crt, key: inKey);

        await Assert.That(editor.Certificates.Select(c => c.Name)).IsEquivalentTo(["leaf.example"]);
    }

    [Test]
    public async Task An_unreadable_tls_crt_is_stated_rather_than_hidden()
    {
        var editor = Editor("kubernetes.io/tls", "not a certificate");

        await Assert.That(editor.CertificateSummary).IsEqualTo("tls.crt holds no certificate this can read");
        await Assert.That(editor.CertificateHealth).IsEqualTo(ResourceHealth.Warn);
        await Assert.That(editor.Certificates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_opaque_secret_without_a_certificate_shows_nothing()
    {
        var editor = Editor("Opaque", "not a certificate");

        await Assert.That(editor.HasCertificates).IsFalse();
    }
}
