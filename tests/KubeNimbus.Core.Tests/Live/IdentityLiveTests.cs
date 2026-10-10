using System.Security.Cryptography.X509Certificates;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// B3-3 against a real API server: the SelfSubjectReview names the identity this connection
/// authenticates as — the client certificate's common name for the sandbox's admin, the
/// ServiceAccount's username for a narrow token — and an Argo sync records it as the initiator.
/// The sandbox's Application CRD is a stand-in with no controller, so the operation the sync
/// writes stays on the object to be read back.
/// </summary>
public class IdentityLiveTests
{
    private static readonly ResourceDescriptor Applications =
        new("argoproj.io", "v1alpha1", "Application", "applications", "application", Namespaced: true, ShortNames: [], Categories: []);

    [Test]
    [Timeout(60_000)]
    public async Task The_review_names_the_admin_by_its_client_certificate(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);

        var sandbox = (await KubeconfigReader.LoadAsync(client.Context.KubeconfigPath, ct)).Configuration;
        var userName = sandbox.Contexts.First(c => c.Name == client.Context.Name).ContextDetails.User;
        var certificateData = sandbox.Users.First(u => u.Name == userName).UserCredentials.ClientCertificateData;
        if (string.IsNullOrEmpty(certificateData))
        {
            Skip.Test("The sandbox kubeconfig authenticates without a client certificate; nothing to compare against.");
        }

        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateData!));
        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

        await Assert.That(await client.GetCurrentUsernameAsync(ct)).IsEqualTo(commonName);
    }

    [Test]
    [Timeout(120_000)]
    public async Task The_review_names_a_narrow_service_account_token(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        using var narrow = await LiveCluster.CreateNarrowUserAsync(client, LiveCluster.Named("whoami"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [get]
            """, ct);

        // A SelfSubjectReview needs no grant of its own; the Role above is only what
        // CreateNarrowUserAsync requires.
        await Assert.That(await narrow.Client.GetCurrentUsernameAsync(ct)).IsEqualTo(narrow.UserName);
    }

    /// <summary>
    /// FEAT-56: the access review's "Signed in as" line reads the groups too, and for a
    /// ServiceAccount the API server's own are the ones RBAC bindings name it by.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task The_access_review_reads_a_service_accounts_groups(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        using var narrow = await LiveCluster.CreateNarrowUserAsync(client, LiveCluster.Named("whoami-groups"), """
            - apiGroups: [""]
              resources: [pods]
              verbs: [get]
            """, ct);

        var identity = await narrow.Client.ReviewSelfSubjectAsync(ct);

        await Assert.That(identity.Outcome).IsEqualTo(SelfSubjectReviewOutcome.Answered);
        await Assert.That(identity.Username).IsEqualTo(narrow.UserName);
        await Assert.That(identity.Groups).Contains("system:serviceaccounts");
        await Assert.That(identity.Groups).Contains($"system:serviceaccounts:{LiveCluster.Namespace}");
        await Assert.That(identity.Groups).Contains("system:authenticated");
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_sync_records_the_user_as_its_initiator(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var catalog = await client.GetResourceCatalogAsync(ct);
        if (ArgoCd.ApplicationDescriptor(catalog) is null)
        {
            Skip.Test("The sandbox has no Application kind (scripts/manifests/70-argocd-crds.yaml not applied).");
        }

        var name = LiveCluster.Named("whoami-app");
        await LiveCluster.ApplyAsync(client, Applications, name, $"""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: {name}
              namespace: {LiveCluster.Namespace}
            spec:
              project: default
              source:
                repoURL: https://example.invalid/repo.git
                path: app
                targetRevision: main
              destination:
                server: https://kubernetes.default.svc
                namespace: {LiveCluster.Namespace}
            """, ct);

        await client.SyncArgoApplicationAsync(Applications, LiveCluster.Namespace, name, cancellationToken: ct);

        var expected = ArgoCd.InitiatorFor(await client.GetCurrentUsernameAsync(ct));
        var synced = await client.ReadResourceAsync(Applications, LiveCluster.Namespace, name, ct);
        var initiator = synced!.Raw.GetProperty("operation").GetProperty("initiatedBy").GetProperty("username").GetString();

        await Assert.That(expected).EndsWith(" (kubeNimbus)");
        await Assert.That(initiator).IsEqualTo(expected);
    }
}
