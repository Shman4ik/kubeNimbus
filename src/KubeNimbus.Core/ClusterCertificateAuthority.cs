using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s.KubeConfigModels;

namespace KubeNimbus.Core;

/// <summary>
/// Reads a cluster entry's <c>certificate-authority-data</c> or <c>certificate-authority</c>
/// whole, so a bundle of several roots trusts every one of them (ENG-57).
/// </summary>
/// <remarks>
/// <para>
/// The client library loads only the first certificate of the PEM it is given. A bundle is
/// what a cluster whose CA is being rotated carries (old and new root side by side), and what
/// several managed offerings and proxies hand out; with only the first root trusted, a server
/// that already presents the new one was refused. kubectl trusts every certificate in the
/// bundle (client-go's <c>certutil.NewPoolFromBytes</c>), and so does this, now that the check
/// is kubeNimbus's own (<see cref="ApiServerCertificateValidator"/>).
/// </para>
/// <para>
/// <b>Only PEM is read here.</b> A value that is not PEM (a single DER certificate, which the
/// library also accepts) leaves the library's own result in place. A PEM bundle with a block
/// that does not parse fails the connect, as it fails kubectl, rather than trusting whatever
/// part of it could be read.
/// </para>
/// </remarks>
internal static class ClusterCertificateAuthority
{
    private const string PemCertificateBegin = "-----BEGIN CERTIFICATE-----";

    /// <summary>
    /// Every certificate in <paramref name="endpoint"/>'s certificate authority, or null when
    /// the entry names none or it is not PEM (the library's own result then stands).
    /// </summary>
    /// <exception cref="KubeconfigSetupException">The PEM holds a certificate that does not parse, or the file cannot be read.</exception>
    internal static X509Certificate2Collection? Read(ClusterEndpoint endpoint, string? kubeconfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        string field;
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(endpoint.CertificateAuthorityData))
        {
            field = "certificate-authority-data";
            try
            {
                bytes = Convert.FromBase64String(endpoint.CertificateAuthorityData.Trim());
            }
            catch (FormatException)
            {
                // The library has already refused or accepted this value its own way; nothing to add.
                return null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.CertificateAuthority))
        {
            var path = Kubeconfig.ResolveAgainstFile(endpoint.CertificateAuthority.Trim(), kubeconfigDirectory);
            field = $"certificate-authority {path}";
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new KubeconfigSetupException($"The cluster entry's {field} could not be read: {e.Message}");
            }
        }
        else
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains(PemCertificateBegin, StringComparison.Ordinal))
        {
            return null;
        }

        // Block by block rather than X509Certificate2Collection.ImportFromPem, which was seen to
        // pass over a CERTIFICATE block that does not decode; a bundle that is partly unreadable
        // must fail, as it fails kubectl, not trust whatever part of it could be read.
        var authorities = new X509Certificate2Collection();
        var rest = text.AsMemory();
        var index = 0;
        while (PemEncoding.TryFind(rest.Span, out var fields))
        {
            index++;
            var label = rest.Span[fields.Label];
            if (label.SequenceEqual("CERTIFICATE"))
            {
                try
                {
                    var der = Convert.FromBase64String(rest.Span[fields.Base64Data].ToString());
                    authorities.Add(X509CertificateLoader.LoadCertificate(der));
                }
                catch (Exception e) when (e is CryptographicException or FormatException)
                {
                    throw new KubeconfigSetupException(
                        $"The cluster entry's {field} holds a certificate (PEM block {index}) that could not be read: {e.Message}");
                }
            }

            rest = rest[fields.Location.End..];
        }

        return authorities.Count > 0 ? authorities : null;
    }
}
