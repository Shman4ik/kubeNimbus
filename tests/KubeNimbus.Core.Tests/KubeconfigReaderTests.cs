using k8s;
using k8s.Exceptions;
using KubeNimbus.Core;
using YamlDotNet.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="KubeconfigReader"/> replaced the client library's kubeconfig loader, whose
/// precompiled YAML code only runs against the YamlDotNet it was built with. These pin that
/// it reads everything the library's model holds, the way the library read it, so the swap
/// is invisible to a connect.
/// </summary>
public class KubeconfigReaderTests
{
    private const string Full = """
        apiVersion: v1
        kind: Config
        preferences: {}
        current-context: prod
        clusters:
        - name: prod-cluster
          cluster:
            server: https://prod.example:6443
            certificate-authority-data: Q0EtREFUQQ==
            tls-server-name: api.prod.example
            proxy-url: socks5://localhost:1080
            extensions:
            - name: client.authentication.k8s.io/exec
              extension: {audience: prod}
        - name: lab-cluster
          cluster:
            server: https://lab.example:6443
            certificate-authority: certs/ca.crt
            insecure-skip-tls-verify: true
        users:
        - name: eks
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: aws
              args: [eks, get-token, --cluster-name, prod]
              env:
              - name: AWS_PROFILE
                value: prod
              installHint: Install the AWS CLI.
              provideClusterInfo: true
              interactiveMode: IfAvailable
        - name: certs
          user:
            client-certificate: certs/client.crt
            client-key: certs/client.key
            client-certificate-data: Q0VSVA==
            client-key-data: S0VZ
        - name: legacy
          user:
            token: abc
            username: admin
            password: hunter2
            as: someone
            as-groups: [system:masters, devs]
            as-user-extra:
              scopes: [view, edit]
              team: platform
            auth-provider:
              name: oidc
              config:
                id-token: xyz
                idp-issuer-url: https://issuer.example
        contexts:
        - name: prod
          context:
            cluster: prod-cluster
            user: eks
            namespace: payments
        - name: lab
          context:
            cluster: lab-cluster
            user: certs
        """;

    [Test]
    public async Task Reads_every_field_the_library_model_holds()
    {
        var config = KubeconfigReader.Parse(Full).Configuration;

        await Assert.That(config.ApiVersion).IsEqualTo("v1");
        await Assert.That(config.Kind).IsEqualTo("Config");
        await Assert.That(config.CurrentContext).IsEqualTo("prod");

        var prod = config.Clusters.Single(c => c.Name == "prod-cluster").ClusterEndpoint;
        await Assert.That(prod.Server).IsEqualTo("https://prod.example:6443");
        await Assert.That(prod.CertificateAuthorityData).IsEqualTo("Q0EtREFUQQ==");
        await Assert.That(prod.TlsServerName).IsEqualTo("api.prod.example");
        await Assert.That(prod.SkipTlsVerify).IsFalse();

        var lab = config.Clusters.Single(c => c.Name == "lab-cluster").ClusterEndpoint;
        await Assert.That(lab.CertificateAuthority).IsEqualTo("certs/ca.crt");
        await Assert.That(lab.SkipTlsVerify).IsTrue();

        var exec = config.Users.Single(u => u.Name == "eks").UserCredentials.ExternalExecution;
        await Assert.That(exec.ApiVersion).IsEqualTo("client.authentication.k8s.io/v1beta1");
        await Assert.That(exec.Command).IsEqualTo("aws");
        await Assert.That(exec.Arguments).IsEquivalentTo(["eks", "get-token", "--cluster-name", "prod"]);
        await Assert.That(exec.EnvironmentVariables.Single()["name"]).IsEqualTo("AWS_PROFILE");
        await Assert.That(exec.EnvironmentVariables.Single()["value"]).IsEqualTo("prod");
        await Assert.That(exec.InstallHint).IsEqualTo("Install the AWS CLI.");
        await Assert.That(exec.ProvideClusterInfo).IsTrue();

        var certs = config.Users.Single(u => u.Name == "certs").UserCredentials;
        await Assert.That(certs.ClientCertificate).IsEqualTo("certs/client.crt");
        await Assert.That(certs.ClientKey).IsEqualTo("certs/client.key");
        await Assert.That(certs.ClientCertificateData).IsEqualTo("Q0VSVA==");
        await Assert.That(certs.ClientKeyData).IsEqualTo("S0VZ");
        await Assert.That(certs.ExternalExecution).IsNull();

        var legacy = config.Users.Single(u => u.Name == "legacy").UserCredentials;
        await Assert.That(legacy.Token).IsEqualTo("abc");
        await Assert.That(legacy.UserName).IsEqualTo("admin");
        await Assert.That(legacy.Password).IsEqualTo("hunter2");
        await Assert.That(legacy.Impersonate).IsEqualTo("someone");
        await Assert.That(legacy.ImpersonateGroups).IsEquivalentTo(["system:masters", "devs"]);
        await Assert.That(legacy.AuthProvider.Name).IsEqualTo("oidc");
        await Assert.That(legacy.AuthProvider.Config["id-token"]).IsEqualTo("xyz");
        await Assert.That(legacy.AuthProvider.Config["idp-issuer-url"]).IsEqualTo("https://issuer.example");

        // client-go's shape for this is a list per key, which the library's model cannot
        // hold. The list is skipped; the single value is kept; the file still loads.
        await Assert.That(legacy.ImpersonateUserExtra.ContainsKey("scopes")).IsFalse();
        await Assert.That(legacy.ImpersonateUserExtra["team"]).IsEqualTo("platform");

        var context = config.Contexts.Single(c => c.Name == "prod").ContextDetails;
        await Assert.That(context.Cluster).IsEqualTo("prod-cluster");
        await Assert.That(context.User).IsEqualTo("eks");
        await Assert.That(context.Namespace).IsEqualTo("payments");
        await Assert.That(config.Contexts.Single(c => c.Name == "lab").ContextDetails.Namespace).IsNull();
    }

    [Test]
    public async Task Reads_the_proxy_url_the_library_model_drops()
    {
        var document = KubeconfigReader.Parse(Full);

        await Assert.That(document.ProxyUrl("prod-cluster")).IsEqualTo("socks5://localhost:1080");
        await Assert.That(document.ProxyUrl("lab-cluster")).IsNull();
    }

    [Test]
    public async Task A_plain_null_is_null_and_a_quoted_one_is_text()
    {
        var config = KubeconfigReader.Parse("""
            contexts:
            - name: a
              context:
                cluster: ~
                user: "null"
                namespace:
            """).Configuration;

        var details = config.Contexts.Single().ContextDetails;
        await Assert.That(details.Cluster).IsNull();
        await Assert.That(details.User).IsEqualTo("null");
        await Assert.That(details.Namespace).IsNull();
    }

    [Test]
    public async Task Scalars_are_read_as_text_whatever_they_look_like()
    {
        // A namespace or a token that happens to look like a number must stay the exact
        // characters written; the library's model is strings throughout.
        var config = KubeconfigReader.Parse("""
            users:
            - name: u
              user:
                token: 0755
                username: 1e3
            """).Configuration;

        await Assert.That(config.Users.Single().UserCredentials.Token).IsEqualTo("0755");
        await Assert.That(config.Users.Single().UserCredentials.UserName).IsEqualTo("1e3");
    }

    [Test]
    [Arguments("")]
    [Arguments("# nothing but a comment\n")]
    [Arguments("---\n")]
    public async Task An_empty_file_is_an_empty_configuration(string text)
    {
        // The library's loader threw a NullReferenceException here; kubectl reads an empty
        // kubeconfig as one with nothing in it.
        var config = KubeconfigReader.Parse(text).Configuration;

        await Assert.That(config.Contexts).IsEmpty();
        await Assert.That(config.Clusters).IsEmpty();
        await Assert.That(config.Users).IsEmpty();
        await Assert.That(config.CurrentContext).IsNull();
    }

    [Test]
    public async Task Kubectls_own_null_lists_read_as_empty()
    {
        // What `kubectl config view` writes for a kubeconfig with nothing in it yet, and what
        // a fresh ~/.kube/config therefore often holds.
        var config = KubeconfigReader.Parse("""
            apiVersion: v1
            clusters: null
            contexts:
            -
            - name: only
              context:
                cluster: c
                user: null
            current-context: ""
            kind: Config
            preferences: {}
            users: null
            """).Configuration;

        await Assert.That(config.Clusters).IsEmpty();
        await Assert.That(config.Users).IsEmpty();
        await Assert.That(config.Contexts.Select(c => c.Name)).IsEquivalentTo(["only"]);
        await Assert.That(config.Contexts.Single().ContextDetails.User).IsNull();
        await Assert.That(config.CurrentContext).IsEqualTo("");
    }

    [Test]
    public async Task A_key_given_twice_keeps_its_last_value()
    {
        var config = KubeconfigReader.Parse("""
            current-context: first
            current-context: second
            """).Configuration;

        await Assert.That(config.CurrentContext).IsEqualTo("second");
    }

    [Test]
    public async Task Anchors_and_aliases_resolve()
    {
        var config = KubeconfigReader.Parse("""
            users:
            - name: a
              user:
                exec: &plugin
                  command: kubelogin
                  args: [get-token]
            - name: b
              user:
                exec: *plugin
            """).Configuration;

        await Assert.That(config.Users.Select(u => u.UserCredentials.ExternalExecution.Command))
            .IsEquivalentTo(["kubelogin", "kubelogin"]);
    }

    [Test]
    public async Task Only_the_first_document_is_read()
    {
        var config = KubeconfigReader.Parse("""
            current-context: first
            ---
            current-context: second
            """).Configuration;

        await Assert.That(config.CurrentContext).IsEqualTo("first");
    }

    [Test]
    public async Task A_field_of_the_wrong_shape_names_itself_and_its_line()
    {
        var exception = await Assert.That(() => KubeconfigReader.Parse("""
            apiVersion: v1
            kind: Config
            clusters: not-a-list
            """)).Throws<YamlException>();

        await Assert.That(exception!.Message).StartsWith("Line 3, column ");
        await Assert.That(exception.Message).Contains("clusters should be a list");
        await Assert.That(exception.Start.Line).IsEqualTo(3);
    }

    [Test]
    public async Task A_boolean_that_is_not_one_is_refused()
    {
        var exception = await Assert.That(() => KubeconfigReader.Parse("""
            clusters:
            - name: c
              cluster:
                insecure-skip-tls-verify: sometimes
            """)).Throws<YamlException>();

        await Assert.That(exception!.Message).Contains("insecure-skip-tls-verify");
    }

    [Test]
    [Arguments("yes", true)]
    [Arguments("True", true)]
    [Arguments("off", false)]
    [Arguments("\"false\"", false)]
    public async Task Booleans_take_the_spellings_the_library_took(string written, bool expected)
    {
        var config = KubeconfigReader.Parse($"""
            clusters:
            - name: c
              cluster:
                insecure-skip-tls-verify: {written}
            """).Configuration;

        await Assert.That(config.Clusters.Single().ClusterEndpoint.SkipTlsVerify).IsEqualTo(expected);
    }

    [Test]
    public async Task Text_that_is_not_yaml_is_refused_with_a_position()
    {
        var exception = await Assert.That(() => KubeconfigReader.Parse("contexts: [ this is: not: yaml\n"))
            .Throws<YamlException>();

        await Assert.That(exception!.Message).StartsWith("Line 1, column ");
    }

    [Test]
    public async Task A_missing_file_is_reported_in_the_librarys_own_words()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"kubenimbus-gone-{Guid.NewGuid():N}.yaml");

        var exception = await Assert.That(async () => await KubeconfigReader.LoadAsync(gone)).Throws<KubeConfigException>();

        await Assert.That(exception!.Message).IsEqualTo($"kubeconfig file not found at {Path.GetFullPath(gone)}");
    }

    [Test]
    public async Task Relative_certificate_paths_resolve_against_the_file_as_before()
    {
        // FileName is what the library's BuildConfigFromConfigObject resolves relative paths
        // against; the old loader set it, so this one has to.
        var directory = Directory.CreateTempSubdirectory("kubenimbus-reader-");
        try
        {
            var path = Path.Combine(directory.FullName, "config");
            await File.WriteAllTextAsync(path, """
                apiVersion: v1
                kind: Config
                current-context: lab
                clusters:
                - name: lab-cluster
                  cluster:
                    server: https://lab.example:6443
                    insecure-skip-tls-verify: true
                users:
                - name: certs
                  user:
                    client-certificate: certs/client.crt
                    client-key: certs/client.key
                contexts:
                - name: lab
                  context:
                    cluster: lab-cluster
                    user: certs
                """);

            var document = await KubeconfigReader.LoadAsync(path);
            var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(document.Configuration, "lab");

            await Assert.That(document.Configuration.FileName).IsEqualTo(path);
            await Assert.That(configuration.Host).IsEqualTo("https://lab.example:6443");
            await Assert.That(configuration.SkipTlsVerify).IsTrue();
            await Assert.That(Path.GetFullPath(configuration.ClientCertificateFilePath))
                .IsEqualTo(Path.Combine(directory.FullName, "certs", "client.crt"));
            await Assert.That(Path.GetFullPath(configuration.ClientKeyFilePath))
                .IsEqualTo(Path.Combine(directory.FullName, "certs", "client.key"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task What_it_reads_builds_a_client_configuration()
    {
        // The end of the path the app takes on every connect, run with whatever YamlDotNet
        // this build carries. Through the library's own loader this threw
        // TypeLoadException as soon as YamlDotNet moved past 16.3.0.
        var config = KubeconfigReader.Parse("""
            apiVersion: v1
            kind: Config
            clusters:
            - name: c
              cluster:
                server: https://c.example:6443
                tls-server-name: api.c.example
                insecure-skip-tls-verify: true
            users:
            - name: u
              user:
                token: not-a-real-token
            contexts:
            - name: ctx
              context:
                cluster: c
                user: u
                namespace: payments
            """).Configuration;

        var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(config, "ctx");

        await Assert.That(configuration.Host).IsEqualTo("https://c.example:6443");
        await Assert.That(configuration.Namespace).IsEqualTo("payments");
        await Assert.That(configuration.TlsServerName).IsEqualTo("api.c.example");
        await Assert.That(configuration.AccessToken).IsEqualTo("not-a-real-token");
    }
}
