using System.Net;
using System.Net.Security;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The API server's certificate is checked the way kubectl checks it — the kubeconfig's CA
/// only, and the host name (or <c>tls-server-name</c>) — on both transports, through the real
/// connect path: a temp kubeconfig, <see cref="ClusterClient.ConnectAsync"/>, a request, and a
/// loopback TLS server standing in for the cluster.
/// </summary>
/// <remarks>
/// The client library accepts any certificate the kubeconfig's CA signed, for any name
/// (<see cref="ApiServerCertificateValidator"/> says why). The first test is the one that
/// matters: a certificate for another host, signed by the cluster's own CA — what a kubelet
/// serving certificate is — must not receive the bearer token. Remove the replacement in
/// <c>ClusterClient.Create</c> and it goes red.
/// </remarks>
public class ApiServerTlsTests
{
    private const string Token = "kubenimbus-test-secret-token";

    [Test]
    public async Task A_certificate_for_another_name_signed_by_the_cluster_ca_is_refused_before_the_token_is_sent()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["wrong.example"]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(server.Requests).IsEmpty();
        var refusal = ApiServerCertificateException.Find(failure);
        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!.Problem).IsEqualTo(ApiServerCertificateProblem.NameMismatch);
        await Assert.That(refusal.ExpectedName).IsEqualTo("127.0.0.1");

        // And the failure view says which name was expected and what the certificate is for.
        var report = await ConnectionReport.CreateAsync(client.Context, failure!);
        await Assert.That(report.Step).IsEqualTo(ConnectionReport.SettingUpTls);
        await Assert.That(report.Headline).Contains("\"127.0.0.1\"");
        await Assert.That(report.Headline).Contains("wrong.example");
        await Assert.That(report.Advice!).Contains("tls-server-name");
    }

    [Test]
    public async Task A_certificate_whose_ip_entry_matches_the_server_address_is_accepted()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(server.Requests.Single().Authorization).IsEqualTo($"Bearer {Token}");
    }

    [Test]
    public async Task A_dns_name_matching_the_server_url_is_accepted()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["localhost"]), host: "localhost");
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
    }

    [Test]
    public async Task Tls_server_name_is_the_name_checked_and_the_one_sent()
    {
        // The URL is an address the certificate does not list; tls-server-name is the name it
        // does — the shape of a cluster reached through a tunnel or a load balancer's IP.
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["api.cluster.example"]));
        using var client = await ClusterClient.ConnectAsync(
            Context(Kubeconfig(server.Url, pki.AuthorityData, clusterExtra: "tls-server-name: api.cluster.example")));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(server.Requests.Single().Headers["Host"]).IsEqualTo("api.cluster.example");
    }

    [Test]
    public async Task A_certificate_from_another_ca_is_refused()
    {
        using var clusterCa = new TestPki("cluster-ca");
        using var otherCa = new TestPki("other-ca");
        using var server = new ScriptedApiServer(Version, otherCa.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, clusterCa.AuthorityData)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(ApiServerCertificateException.Find(failure)!.Problem).IsEqualTo(ApiServerCertificateProblem.Untrusted);
    }

    [Test]
    public async Task A_certificate_only_for_client_authentication_is_refused()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(
            Version, pki.Issue(addresses: [IPAddress.Loopback], usage: TestPki.ClientAuthentication));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(ApiServerCertificateException.Find(failure)!.Problem).IsEqualTo(ApiServerCertificateProblem.WrongUsage);
    }

    [Test]
    public async Task Without_a_ca_in_the_kubeconfig_a_certificate_this_machine_does_not_trust_is_refused()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, authorityData: null)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(ApiServerCertificateException.Find(failure)!.Problem).IsEqualTo(ApiServerCertificateProblem.Untrusted);
    }

    [Test]
    public async Task Insecure_skip_tls_verify_still_accepts_anything_and_says_so()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["wrong.example"]));
        var kubeconfig = Kubeconfig(server.Url, authorityData: null, clusterExtra: "insecure-skip-tls-verify: true");
        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(client.SkipsTlsVerification).IsTrue();
        var report = await ConnectionReport.CreateAsync(client.Context, new TimeoutException());
        await Assert.That(report.Facts.Any(f => f.Label == "TLS" && f.Value == "not verified (insecure-skip-tls-verify)")).IsTrue();
    }

    [Test]
    public async Task A_verified_connection_does_not_claim_to_skip_verification()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        await Assert.That(client.SkipsTlsVerification).IsFalse();
    }

    [Test]
    public async Task Exec_against_a_server_with_the_wrong_name_fails_at_tls()
    {
        // The WebSocket transport (exec, port-forward) had the same flawed check, installed by
        // a non-virtual method of the library's builder; ours replaces it just before connecting.
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["wrong.example"]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Exception? failure = null;
        try
        {
            using var session = await client.ExecAsync("default", "pod", "app", ["sh"], cancellationToken: timeout.Token);
        }
        catch (Exception e)
        {
            failure = e;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(ApiServerCertificateException.Find(failure)?.Problem).IsEqualTo(ApiServerCertificateProblem.NameMismatch);
        await Assert.That(server.Requests).IsEmpty();
    }

    [Test]
    public async Task Port_forward_against_a_server_with_the_wrong_name_fails_at_tls()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(dnsNames: ["wrong.example"]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<Exception>(
            () => client.OpenPortForwardWebSocketAsync("default", "pod", 80, timeout.Token));

        await Assert.That(ApiServerCertificateException.Find(failure)?.Problem).IsEqualTo(ApiServerCertificateProblem.NameMismatch);
        await Assert.That(server.Requests).IsEmpty();
    }

    // ------------------------------------------------- a certificate-authority bundle (ENG-57)

    /// <summary>
    /// A bundle of several roots — what a cluster whose CA is being rotated carries — trusts
    /// every one of them, whichever comes first. The library kept only the first, so a server
    /// already presenting the second root was refused.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Every_root_in_a_certificate_authority_data_bundle_is_trusted(bool signerFirst)
    {
        using var signer = new TestPki("signing-ca");
        using var other = new TestPki("other-ca");
        using var server = new ScriptedApiServer(Version, signer.Issue(addresses: [IPAddress.Loopback]));
        var bundle = signerFirst ? Bundle(signer, other) : Bundle(other, signer);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, Base64(bundle))));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
    }

    /// <summary>The same through <c>certificate-authority</c>, a file path relative to the kubeconfig.</summary>
    [Test]
    public async Task Every_root_in_a_certificate_authority_file_is_trusted()
    {
        using var signer = new TestPki("signing-ca");
        using var other = new TestPki("other-ca");
        using var server = new ScriptedApiServer(Version, signer.Issue(addresses: [IPAddress.Loopback]));
        var kubeconfig = Kubeconfig(server.Url, authorityData: null, clusterExtra: "certificate-authority: ca-bundle.crt");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(kubeconfig)!, "ca-bundle.crt"), Bundle(other, signer));
        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));

        await Assert.That((await client.GetServerVersionAsync()).GitVersion).IsEqualTo("v1.31.0");
    }

    /// <summary>A bundle still trusts nothing outside it.</summary>
    [Test]
    public async Task A_certificate_from_a_ca_outside_the_bundle_is_still_refused()
    {
        using var a = new TestPki("a-ca");
        using var b = new TestPki("b-ca");
        using var stranger = new TestPki("stranger-ca");
        using var server = new ScriptedApiServer(Version, stranger.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, Base64(Bundle(a, b)))));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerVersionAsync());

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(ApiServerCertificateException.Find(failure)!.Problem).IsEqualTo(ApiServerCertificateProblem.Untrusted);
    }

    /// <summary>kubectl refuses a bundle with a block that does not parse; so does this, rather than trusting part of it.</summary>
    [Test]
    public async Task A_bundle_with_a_broken_certificate_fails_at_reading_the_kubeconfig()
    {
        using var a = new TestPki("a-ca");
        var broken = a.Authority.ExportCertificatePem() + "\n-----BEGIN CERTIFICATE-----\nAAAAAAAA\n-----END CERTIFICATE-----\n";
        var kubeconfig = Kubeconfig("https://127.0.0.1:1", Base64(broken));

        var failure = await Assert.ThrowsAsync<KubeconfigSetupException>(() => ClusterClient.ConnectAsync(Context(kubeconfig)));

        await Assert.That(failure!.Message).Contains("certificate-authority-data");
    }

    private static string Bundle(params TestPki[] authorities) =>
        string.Concat(authorities.Select(a => a.Authority.ExportCertificatePem() + "\n"));

    private static string Base64(string pem) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem));

    // ------------------------------------------------------------ plain http (ENG-59)

    [Test]
    public async Task A_plain_http_server_is_stated_by_the_client_and_in_the_report()
    {
        using var server = new ScriptedApiServer(Version);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, authorityData: null)));

        await Assert.That(client.UsesPlainHttp).IsTrue();
        var report = await ConnectionReport.CreateAsync(client.Context, new TimeoutException());
        await Assert.That(report.Facts.Single(f => f.Label == "TLS").Value).IsEqualTo(ConnectionReport.PlainHttpFact);
    }

    /// <summary>With no TLS there is no certificate to skip checking; the http fact is the one stated.</summary>
    [Test]
    public async Task A_plain_http_server_with_skip_verify_is_stated_as_plain_http()
    {
        using var server = new ScriptedApiServer(Version);
        var kubeconfig = Kubeconfig(server.Url, authorityData: null, clusterExtra: "insecure-skip-tls-verify: true");

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), new TimeoutException());

        await Assert.That(report.Facts.Single(f => f.Label == "TLS").Value).IsEqualTo(ConnectionReport.PlainHttpFact);
    }

    [Test]
    public async Task An_https_server_is_not_plain_http()
    {
        using var pki = new TestPki();
        using var server = new ScriptedApiServer(Version, pki.Issue(addresses: [IPAddress.Loopback]));
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, pki.AuthorityData)));

        await Assert.That(client.UsesPlainHttp).IsFalse();
        var report = await ConnectionReport.CreateAsync(client.Context, new TimeoutException());
        await Assert.That(report.Facts.Any(f => f.Label == "TLS")).IsFalse();
    }

    // ------------------------------------------------------------ the decision itself

    [Test]
    public async Task The_expected_name_is_tls_server_name_or_the_url_host_without_brackets()
    {
        await Assert.That(ApiServerCertificateValidator.ExpectedNameFor(new Uri("https://10.0.0.1:6443"), null)).IsEqualTo("10.0.0.1");
        await Assert.That(ApiServerCertificateValidator.ExpectedNameFor(new Uri("https://[::1]:6443"), null)).IsEqualTo("::1");
        await Assert.That(ApiServerCertificateValidator.ExpectedNameFor(new Uri("https://10.0.0.1:6443"), "api.example")).IsEqualTo("api.example");
    }

    [Test]
    public async Task The_common_name_alone_never_matches()
    {
        // Go stopped reading the common name in 1.15; a certificate that names its host only
        // there is refused by kubectl, and so here.
        using var pki = new TestPki();
        using var leaf = pki.Issue(dnsNames: ["other.example"]);
        var refusal = ApiServerCertificateValidator.Check(
            leaf, presented: null, SslPolicyErrors.None, "kubenimbus-test-leaf", pki.Authority.Collection());

        await Assert.That(refusal?.Problem).IsEqualTo(ApiServerCertificateProblem.NameMismatch);
    }

    [Test]
    public async Task An_expired_certificate_is_refused()
    {
        using var pki = new TestPki();
        using var leaf = pki.Issue(addresses: [IPAddress.Loopback], notAfter: DateTimeOffset.UtcNow.AddHours(-1));
        var refusal = ApiServerCertificateValidator.Check(
            leaf, presented: null, SslPolicyErrors.None, "127.0.0.1", pki.Authority.Collection());

        await Assert.That(refusal?.Problem).IsEqualTo(ApiServerCertificateProblem.Expired);
    }

    [Test]
    public async Task A_wildcard_matches_one_label()
    {
        using var pki = new TestPki();
        using var leaf = pki.Issue(dnsNames: ["*.cluster.example"]);

        await Assert.That(ApiServerCertificateValidator.Check(
            leaf, null, SslPolicyErrors.None, "api.cluster.example", pki.Authority.Collection())).IsNull();
        await Assert.That(ApiServerCertificateValidator.Check(
            leaf, null, SslPolicyErrors.None, "a.b.cluster.example", pki.Authority.Collection())?.Problem)
            .IsEqualTo(ApiServerCertificateProblem.NameMismatch);
    }

    // --------------------------------------------------------------------- helpers

    private static ScriptedResponse Version(ScriptedRequest request) => new(200, ScriptedApiServer.VersionBody);

    internal static ClusterContext Context(string kubeconfig) => new("tls", "tls", null, "tls", kubeconfig);

    /// <summary>A kubeconfig for one cluster and a bearer-token user; extra lines are indented into each block.</summary>
    internal static string Kubeconfig(string server, string? authorityData, string? clusterExtra = null, string? userExtra = null)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-tls").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        var lines = new List<string>
        {
            "apiVersion: v1",
            "kind: Config",
            "clusters:",
            "- name: tls",
            "  cluster:",
            $"    server: {server}",
        };
        if (authorityData is not null) lines.Add($"    certificate-authority-data: {authorityData}");
        if (clusterExtra is not null) lines.AddRange(clusterExtra.Split('\n').Select(l => "    " + l.TrimEnd('\r')));
        lines.AddRange([
            "contexts:",
            "- name: tls",
            "  context:",
            "    cluster: tls",
            "    user: tls",
            "current-context: tls",
            "users:",
            "- name: tls",
            "  user:",
            $"    token: {Token}",
        ]);
        if (userExtra is not null) lines.AddRange(userExtra.Split('\n').Select(l => "    " + l.TrimEnd('\r')));
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }
}

internal static class CertificateCollections
{
    public static System.Security.Cryptography.X509Certificates.X509Certificate2Collection Collection(
        this System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => new(certificate);
}
