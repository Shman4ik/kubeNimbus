namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// cert-manager on the sandbox, for the tests that need CRDs and a mutating webhook this
/// repository did not write. It is installed by hand, once, from its official release
/// manifest (see <c>scripts/README.md</c>); a test never installs it, and skips with the
/// reason when it is absent or its webhook is not serving.
/// </summary>
internal static class CertManager
{
    public static readonly ResourceDescriptor Crds = new(
        "apiextensions.k8s.io", "v1", "CustomResourceDefinition", "customresourcedefinitions",
        "customresourcedefinition", false, ["crd", "crds"], []);

    public static readonly ResourceDescriptor Certificates = new(
        "cert-manager.io", "v1", "Certificate", "certificates", "certificate", true, ["cert", "certs"], ["cert-manager"]);

    public static readonly ResourceDescriptor Issuers = new(
        "cert-manager.io", "v1", "Issuer", "issuers", "issuer", true, [], ["cert-manager"]);

    public static readonly ResourceDescriptor CertificateRequests = new(
        "cert-manager.io", "v1", "CertificateRequest", "certificaterequests", "certificaterequest", true, ["cr", "crs"], ["cert-manager"]);

    private static readonly ResourceDescriptor Deployments = new(
        "apps", "v1", "Deployment", "deployments", "deployment", true, [], []);

    /// <summary>Skips the calling test unless cert-manager's CRDs are installed and its webhook is ready.</summary>
    public static async Task RequireAsync(ClusterClient client, CancellationToken ct)
    {
        if (await client.ReadResourceAsync(Crds, null, "certificates.cert-manager.io", ct) is null)
        {
            Skip.Test("cert-manager is not installed on the sandbox (see scripts/README.md for the pinned install)");
        }

        var webhook = await client.ReadResourceAsync(Deployments, "cert-manager", "cert-manager-webhook", ct);
        var ready = webhook?.Raw.TryGetProperty("status", out var s) == true
                    && s.TryGetProperty("readyReplicas", out var r) && r.TryGetInt32(out var n) && n > 0;
        if (!ready)
        {
            Skip.Test("cert-manager's webhook has no ready replica, so its objects cannot be admitted");
        }
    }
}
