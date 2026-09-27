using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// Reads the X.509 certificates a Secret carries (FEAT-30): subject, issuer, the names it
/// is valid for, and — the reason anyone opens one — when it expires. A certificate is
/// public by construction, so decoding it reveals nothing the Secret's reveal guards; the
/// private key beside it is never read here.
/// </summary>
/// <remarks>
/// <see cref="X509CertificateLoader"/> over bytes already in hand: no new dependency, no
/// reflection, and the same platform crypto the TLS connection to the API server already
/// loads, so it costs the NativeAOT binary nothing new.
/// </remarks>
public static class TlsCertificates
{
    /// <summary>The Secret type cert-manager, ingress controllers and <c>kubectl create secret tls</c> write.</summary>
    public const string TlsSecretType = "kubernetes.io/tls";

    /// <summary>
    /// Whether a Secret key is one to read as certificates: <c>tls.crt</c> on a TLS Secret,
    /// and on any Secret a key named <c>*.crt</c> (a <c>ca.crt</c> beside a client
    /// certificate is the common one). Never a key file, whatever it is called.
    /// </summary>
    public static bool IsCertificateKey(string key, string? secretType) =>
        key.EndsWith(".crt", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(secretType, TlsSecretType, StringComparison.Ordinal) && key == "tls.crt");

    /// <summary>
    /// Every certificate in a decoded Secret value — a PEM bundle (a leaf and its chain,
    /// in the order the file has them) or a single DER certificate. Empty when there is
    /// none; a block that fails to parse is skipped rather than failing the rest.
    /// </summary>
    public static IReadOnlyList<CertificateSummary> Read(ReadOnlySpan<byte> value)
    {
        var result = new List<CertificateSummary>();
        var text = Encoding.ASCII.GetString(value);
        ReadOnlySpan<char> remaining = text;
        var sawPem = false;
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            sawPem = true;
            if (remaining[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                var der = new byte[fields.DecodedDataLength];
                if (Convert.TryFromBase64Chars(remaining[fields.Base64Data], der, out var written)
                    && TryLoad(der.AsSpan(0, written)) is { } summary)
                {
                    result.Add(summary);
                }
            }

            remaining = remaining[fields.Location.End..];
        }

        if (!sawPem && value.Length > 0 && value[0] == 0x30 && TryLoad(value) is { } single)
        {
            // DER: an ASN.1 SEQUENCE, which is what a binary certificate starts with.
            result.Add(single);
        }

        return result;
    }

    private static CertificateSummary? TryLoad(ReadOnlySpan<byte> der)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return Summarize(certificate);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static CertificateSummary Summarize(X509Certificate2 certificate)
    {
        var names = new List<string>();
        var isCa = false;
        foreach (var extension in certificate.Extensions)
        {
            switch (extension)
            {
                case X509SubjectAlternativeNameExtension san:
                    names.AddRange(san.EnumerateDnsNames());
                    names.AddRange(san.EnumerateIPAddresses().Select(ip => ip.ToString()));
                    break;
                case X509BasicConstraintsExtension constraints:
                    isCa = constraints.CertificateAuthority;
                    break;
            }
        }

        return new CertificateSummary(
            Subject: certificate.Subject,
            SubjectCommonName: certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            Issuer: certificate.Issuer,
            IssuerCommonName: certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            SubjectAlternativeNames: names,
            NotBefore: new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            NotAfter: new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            IsCertificateAuthority: isCa,
            IsSelfIssued: string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal),
            SerialNumber: certificate.SerialNumber);
    }
}

/// <summary>What one certificate says about itself — everything the card shows, nothing it does not.</summary>
public sealed record CertificateSummary(
    string Subject,
    string SubjectCommonName,
    string Issuer,
    string IssuerCommonName,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool IsCertificateAuthority,
    bool IsSelfIssued,
    string SerialNumber)
{
    /// <summary>Expired, or not yet valid, at <paramref name="now"/>.</summary>
    public bool IsValidAt(DateTimeOffset now) => now >= NotBefore && now <= NotAfter;
}
