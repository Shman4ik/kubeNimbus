using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KubeNimbus.Core;

/// <summary>
/// Verifies the API server's certificate the way kubectl (Go's <c>crypto/tls</c>) does, and
/// replaces the client library's own check on every transport — the HTTP handler and the
/// WebSocket exec and port-forward open. Installed by <c>ClusterClient.Create</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the library's check is replaced, not trusted.</b> <c>KubernetesClient.Aot</c>
/// (every version up to 19.0.2) validates a kubeconfig with a <c>certificate-authority</c>
/// in <c>Kubernetes.CertificateValidationCallBack</c>, which returns "the chain builds to that
/// CA" from inside its chain-errors branch and never looks at
/// <see cref="SslPolicyErrors.RemoteCertificateNameMismatch"/>. A cluster CA is never in the
/// system store, so the chain-errors branch is the one every such kubeconfig takes, and any
/// server certificate that CA signed was accepted for any host name — a kubelet's serving
/// certificate on EKS, AKS, k3s or kubeadm with <c>serverTLSBootstrap</c> among them. The
/// bearer token went to whoever presented one.
/// </para>
/// <para>
/// <b>The rules, kubectl's.</b> The expected name is the cluster's <c>tls-server-name</c>
/// when set, otherwise the server URL's host. With the kubeconfig's CA: the chain must build
/// to exactly those certificates (never the system store), for server authentication, with
/// no revocation check (kubectl does none). Without one: the system's trust decides the
/// chain. In both cases the name check is ours, against the certificate's subject
/// alternative names only — IP entries count, the common name does not, as in Go since 1.15.
/// <c>insecure-skip-tls-verify</c> is the one case this is not installed, and the app says so
/// while such a tab is connected.
/// </para>
/// <para>
/// <b>A refusal throws</b> <see cref="ApiServerCertificateException"/> rather than returning
/// false. The TLS stack carries the exception out as the inner cause of the request's own
/// failure, so the connection report can say which name was expected and why the
/// certificate did not match it — for exactly the connection that failed, which a value
/// recorded on the side could not promise once connections are pooled.
/// </para>
/// </remarks>
internal sealed class ApiServerCertificateValidator
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private readonly X509Certificate2Collection? _authorities;

    internal ApiServerCertificateValidator(string expectedName, X509Certificate2Collection? authorities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ExpectedName = expectedName;
        _authorities = authorities is { Count: > 0 } ? authorities : null;
    }

    /// <summary>The name the certificate must be valid for.</summary>
    public string ExpectedName { get; }

    /// <summary>
    /// The validator for a client built from <paramref name="configuration"/> that talks to
    /// <paramref name="server"/>, or null when there is nothing to validate: a plain-http
    /// server, or a cluster entry that sets <c>insecure-skip-tls-verify</c>.
    /// </summary>
    internal static ApiServerCertificateValidator? For(k8s.KubernetesClientConfiguration configuration, Uri server)
    {
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || configuration.SkipTlsVerify)
        {
            return null;
        }

        return new ApiServerCertificateValidator(ExpectedNameFor(server, configuration.TlsServerName), configuration.SslCaCerts);
    }

    /// <summary><c>tls-server-name</c> when set, otherwise the URL's host, without IPv6 brackets.</summary>
    internal static string ExpectedNameFor(Uri server, string? tlsServerName) =>
        !string.IsNullOrWhiteSpace(tlsServerName)
            ? tlsServerName.Trim()
            : server.IdnHost.Trim('[', ']');

    /// <summary>The <see cref="RemoteCertificateValidationCallback"/>. Throws on a refusal; see the remarks above.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        X509Certificate2? leaf = certificate switch
        {
            null => null,
            X509Certificate2 typed => typed,
            _ => X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()),
        };

        var refusal = Check(leaf, chain, errors, ExpectedName, _authorities);
        return refusal is null ? true : throw refusal;
    }

    /// <summary>
    /// The decision, pure apart from building a chain: null when <paramref name="leaf"/> is
    /// acceptable for <paramref name="expectedName"/>, otherwise the refusal to throw.
    /// </summary>
    /// <param name="presented">The chain the TLS stack built, whose extra store holds the intermediates the server sent.</param>
    /// <param name="errors">What the TLS stack found; only its chain verdict is used, and only without <paramref name="authorities"/>.</param>
    /// <param name="authorities">The kubeconfig's <c>certificate-authority</c>, or null to use the system's trust.</param>
    internal static ApiServerCertificateException? Check(
        X509Certificate2? leaf,
        X509Chain? presented,
        SslPolicyErrors errors,
        string expectedName,
        X509Certificate2Collection? authorities)
    {
        if (leaf is null)
        {
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.NoCertificate, expectedName, "The API server presented no certificate.");
        }

        // The chain first, then the name: the order Go checks them in, so a certificate that
        // is wrong on both counts reads the way kubectl would describe it.
        if (authorities is not null)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
            if (presented is not null)
            {
                chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
                foreach (var element in presented.ChainElements.Skip(1))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            if (!chain.Build(leaf))
            {
                var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
                return ChainRefusal(flags, expectedName, chain.ChainStatus);
            }
        }
        else if ((errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None)
        {
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.Untrusted,
                expectedName,
                "The API server's certificate is not trusted by this machine, and the kubeconfig names no certificate-authority for this cluster.");
        }

        if (!leaf.MatchesHostname(expectedName, allowWildcards: true, allowCommonName: false))
        {
            var names = NamesIn(leaf);
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.NameMismatch,
                expectedName,
                names.Count == 0
                    ? $"The API server's certificate is not valid for \"{expectedName}\": it lists no names at all."
                    : $"The API server's certificate is not valid for \"{expectedName}\"; it is for {string.Join(", ", names)}.");
        }

        return null;
    }

    private static ApiServerCertificateException ChainRefusal(
        X509ChainStatusFlags flags, string expectedName, X509ChainStatus[] statuses)
    {
        if ((flags & X509ChainStatusFlags.NotValidForUsage) != 0)
        {
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.WrongUsage,
                expectedName,
                "The API server's certificate is not issued for server authentication (its extended key usage does not allow it).");
        }

        if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
        {
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.Untrusted,
                expectedName,
                "The API server's certificate is not signed by the certificate-authority in the kubeconfig.");
        }

        if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            return new ApiServerCertificateException(
                ApiServerCertificateProblem.Expired,
                expectedName,
                "The API server's certificate has expired or is not valid yet.");
        }

        var detail = string.Join(" ", statuses.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
        return new ApiServerCertificateException(
            ApiServerCertificateProblem.Untrusted,
            expectedName,
            detail.Length == 0
                ? "The API server's certificate does not verify against the kubeconfig's certificate-authority."
                : $"The API server's certificate does not verify against the kubeconfig's certificate-authority: {detail}");
    }

    /// <summary>The DNS and IP entries of the certificate's subject alternative names.</summary>
    internal static IReadOnlyList<string> NamesIn(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(a => a.ToString()));
        }

        return names;
    }
}

/// <summary>What was wrong with the API server's certificate.</summary>
public enum ApiServerCertificateProblem
{
    /// <summary>Valid, but for another name than the one the kubeconfig connects to.</summary>
    NameMismatch,

    /// <summary>Not signed by the kubeconfig's certificate-authority (or, without one, by anything this machine trusts).</summary>
    Untrusted,

    /// <summary>Its extended key usage does not include server authentication.</summary>
    WrongUsage,

    /// <summary>Outside its validity period.</summary>
    Expired,

    /// <summary>The server sent none.</summary>
    NoCertificate,
}

/// <summary>
/// The API server's certificate was refused by kubeNimbus's own check. An
/// <see cref="AuthenticationException"/>, so everything that already reads a TLS failure as
/// one keeps doing so; the connection report reads <see cref="ExpectedName"/> and
/// <see cref="Problem"/> to say more.
/// </summary>
public sealed class ApiServerCertificateException : AuthenticationException
{
    internal ApiServerCertificateException(ApiServerCertificateProblem problem, string expectedName, string message)
        : base(message)
    {
        Problem = problem;
        ExpectedName = expectedName;
    }

    public ApiServerCertificateProblem Problem { get; }

    /// <summary>The name the certificate had to be valid for: <c>tls-server-name</c>, or the server URL's host.</summary>
    public string ExpectedName { get; }

    /// <summary>The refusal somewhere in <paramref name="exception"/>'s chain of causes, or null.</summary>
    public static ApiServerCertificateException? Find(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is ApiServerCertificateException refusal)
            {
                return refusal;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
