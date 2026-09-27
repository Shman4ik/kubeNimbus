using System.Text.Json;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Ingress as routes with a URL each. The URL half is the security-relevant one: it is the
/// first place a string from the cluster reaches the operating system's "open this", so the
/// tests pin that it is built from a validated host and never passed through.
/// </summary>
public class IngressRulesTests
{
    private static DynamicResource Ingress(string spec, string annotations = "{}", string status = "{}") => new(JsonDocument.Parse($$"""
        { "apiVersion": "networking.k8s.io/v1", "kind": "Ingress",
          "metadata": { "name": "i", "namespace": "payments", "annotations": {{annotations}} },
          "spec": {{spec}}, "status": {{status}} }
        """).RootElement.Clone());

    private const string Rules = """
        {
          "ingressClassName": "nginx",
          "tls": [ { "hosts": [ "api.example.com" ], "secretName": "api-tls" }, { "hosts": [ "old.example.com" ] } ],
          "rules": [
            { "host": "api.example.com", "http": { "paths": [
                { "path": "/ledger", "pathType": "Prefix", "backend": { "service": { "name": "ledger-api", "port": { "name": "http" } } } },
                { "path": "/api(/|$)(.*)", "pathType": "ImplementationSpecific", "backend": { "service": { "name": "checkout", "port": { "number": 80 } } } } ] } },
            { "host": "plain.example.com", "http": { "paths": [
                { "path": "/", "pathType": "Prefix", "backend": { "service": { "name": "web", "port": { "number": 8080 } } } } ] } },
            { "host": "*.internal.example.com", "http": { "paths": [
                { "path": "/", "pathType": "Prefix", "backend": { "resource": { "apiGroup": "k8s.example.com", "kind": "StorageBucket", "name": "assets" } } } ] } },
            { "http": { "paths": [ { "path": "/", "pathType": "Prefix", "backend": { "service": { "name": "fallback", "port": { "number": 80 } } } } ] } }
          ]
        }
        """;

    [Test]
    public async Task Each_path_is_a_row_with_its_backend_and_tls_state()
    {
        var view = IngressRules.Read(Ingress(Rules));

        await Assert.That(view.ClassName).IsEqualTo("nginx");
        await Assert.That(view.Paths.Select(p => (p.Host, p.Path, p.Backend.Display, p.IsTls)).ToArray()).IsEquivalentTo(new[]
        {
            ("api.example.com", "/ledger", "ledger-api:http", true),
            ("api.example.com", "/api(/|$)(.*)", "checkout:80", true),
            ("plain.example.com", "/", "web:8080", false),
            ("*.internal.example.com", "/", "StorageBucket/assets", false),
            ("", "/", "fallback:80", false),
        });
        await Assert.That(view.Paths[0].TlsSecret).IsEqualTo("api-tls");
        await Assert.That(view.TlsHostsWithoutRule).IsEquivalentTo(new[] { "old.example.com" });
    }

    /// <summary>https exactly when the host is in <c>spec.tls[].hosts</c>; a plain path goes into the URL.</summary>
    [Test]
    public async Task The_url_scheme_follows_tls_and_a_plain_path_is_kept()
    {
        var view = IngressRules.Read(Ingress(Rules));

        await Assert.That(view.Paths[0].Url?.AbsoluteUri).IsEqualTo("https://api.example.com/ledger");
        await Assert.That(view.Paths[2].Url?.AbsoluteUri).IsEqualTo("http://plain.example.com/");
    }

    /// <summary>A regex path would 404 as a URL, so the link goes to the host's root instead.</summary>
    [Test]
    public async Task A_regex_path_links_to_the_host_root()
    {
        var view = IngressRules.Read(Ingress(Rules));

        await Assert.That(view.Paths[1].Url?.AbsoluteUri).IsEqualTo("https://api.example.com/");
    }

    /// <summary>A wildcard or an empty host is not a place a browser can go: plain text, no link.</summary>
    [Test]
    public async Task A_wildcard_or_empty_host_has_no_url()
    {
        var view = IngressRules.Read(Ingress(Rules));

        await Assert.That(view.Paths[3].Url).IsNull();
        await Assert.That(view.Paths[4].Url).IsNull();
    }

    /// <summary>
    /// Anything that is not a DNS-1123 name is refused, including the strings that would
    /// turn "open this URL" into something else: another scheme, credentials, a port, a
    /// path smuggled into the host, a command line, upper case the API server rejects.
    /// </summary>
    [Test]
    [Arguments("evil.com@good.com")]
    [Arguments("good.com/../x")]
    [Arguments("good.com:8443")]
    [Arguments("file:///etc/passwd")]
    [Arguments("calc.exe & del")]
    [Arguments("Good.Example.com")]
    [Arguments("-bad.example.com")]
    [Arguments("a..b")]
    [Arguments("")]
    public async Task A_host_that_is_not_a_hostname_gets_no_url(string host)
    {
        await Assert.That(IngressRules.IsValidHostname(host)).IsFalse();
        await Assert.That(IngressRules.BuildUrl(host, "/", https: true)).IsNull();
    }

    [Test]
    [Arguments("/a b")]
    [Arguments("/x?y=1")]
    [Arguments("/x#frag")]
    [Arguments("relative")]
    [Arguments("/%zz")]
    public async Task A_path_that_is_not_plain_is_not_put_in_a_url(string path)
    {
        await Assert.That(IngressRules.IsPlainPath(path)).IsFalse();
        await Assert.That(IngressRules.BuildUrl("ok.example.com", path, https: false)).IsNull();
    }

    [Test]
    public async Task The_legacy_class_annotation_is_used_when_no_class_name_is_set()
    {
        var view = IngressRules.Read(Ingress("""{ "rules": [] }""", annotations: """{ "kubernetes.io/ingress.class": "traefik" }"""));

        await Assert.That(view.ClassName).IsEqualTo("traefik");
        await Assert.That(view.ClassFromAnnotation).IsTrue();
    }

    /// <summary>kubectl's Hosts column: three hosts, "+ N more...", and "*" when no rule names one.</summary>
    [Test]
    public async Task The_hosts_column_matches_kubectl()
    {
        static JsonElement Spec(string json) => JsonDocument.Parse(json).RootElement.Clone();

        await Assert.That(IngressRules.HostsColumn(Spec("""{ "rules": [ {} ] }"""))).IsEqualTo("*");
        await Assert.That(IngressRules.HostsColumn(Spec("""{ "rules": [ { "host": "a" }, { "host": "b" } ] }""")))
            .IsEqualTo("a,b");
        await Assert.That(IngressRules.HostsColumn(Spec("""{ "rules": [ { "host": "a" }, { "host": "b" }, { "host": "c" }, { "host": "d" }, { "host": "e" } ] }""")))
            .IsEqualTo("a,b,c + 2 more...");
    }
}
