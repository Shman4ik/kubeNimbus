using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A throwaway certificate authority and the server certificates it signs, for tests that
/// need a real TLS server: an API server stand-in whose certificate names the right or the
/// wrong host, comes from another CA, or is not for server use.
/// </summary>
internal sealed class TestPki : IDisposable
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    private readonly RSA _key;

    public TestPki(string name = "kubenimbus-test-ca")
    {
        _key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>The CA certificate, with its private key.</summary>
    public X509Certificate2 Authority { get; }

    /// <summary>The CA as a kubeconfig carries it: base64 of the PEM.</summary>
    public string AuthorityData =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(Authority.ExportCertificatePem()));

    /// <summary>
    /// A leaf certificate signed by this CA, with its private key, for the given DNS names and
    /// IP addresses (subject alternative names only — the common name is a decoy, as in real
    /// clusters) and extended key usage.
    /// </summary>
    public X509Certificate2 Issue(
        IEnumerable<string>? dnsNames = null,
        IEnumerable<IPAddress>? addresses = null,
        string usage = ServerAuthentication,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=kubenimbus-test-leaf", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames ?? [])
        {
            names.AddDnsName(dns);
        }

        foreach (var address in addresses ?? [])
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        using var signed = request.Create(
            Authority,
            DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(10),
            RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);

        // Through PKCS#12: SChannel on Windows will not serve a certificate whose key lives
        // only in an ephemeral handle.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public void Dispose()
    {
        Authority.Dispose();
        _key.Dispose();
    }
}
