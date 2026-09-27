using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-60 and FEAT-62: the four networking kinds' list cells carry kubectl's own columns,
/// and Gateway API is filed under Network. The objects are the sandbox's own (k3s 1.33),
/// trimmed, and each expected string is what <c>kubectl get</c> printed for that object —
/// the Details cell joins kubectl's columns with " · ", so the parts between the dots are
/// the thing compared.
/// </summary>
public class NetworkingListColumnsTests
{
    private static DynamicResource Obj(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static ResourceDescriptor Kind(string group, string kind) =>
        new(group, "v1", kind, kind.ToLowerInvariant() + "s", kind.ToLowerInvariant(), Namespaced: true, ShortNames: [], Categories: []);

    /// <summary><c>kubectl get endpoints -n kube-system kube-dns</c> → <c>10.42.0.90:53,10.42.0.90:53,10.42.0.90:9153</c>.</summary>
    [Test]
    public async Task Endpoints_print_kubectls_endpoint_list()
    {
        var endpoints = Obj("""
            { "apiVersion": "v1", "kind": "Endpoints", "metadata": { "name": "kube-dns", "namespace": "kube-system" },
              "subsets": [ { "addresses": [ { "ip": "10.42.0.90" } ],
                             "ports": [ { "name": "dns-tcp", "port": 53, "protocol": "TCP" },
                                        { "name": "dns", "port": 53, "protocol": "UDP" },
                                        { "name": "metrics", "port": 9153, "protocol": "TCP" } ] } ] }
            """);

        await Assert.That(ResourceStatusSummary.Summarize(endpoints).Details)
            .IsEqualTo("10.42.0.90:53,10.42.0.90:53,10.42.0.90:9153");
    }

    /// <summary>Only ready addresses count, three are shown, the rest are counted — and none at all is "&lt;none&gt;".</summary>
    [Test]
    public async Task Endpoints_show_three_ready_addresses_and_count_the_rest()
    {
        var many = Obj("""
            { "apiVersion": "v1", "kind": "Endpoints", "metadata": { "name": "x", "namespace": "n" },
              "subsets": [ { "addresses": [ { "ip": "10.0.0.1" }, { "ip": "10.0.0.2" }, { "ip": "10.0.0.3" }, { "ip": "10.0.0.4" }, { "ip": "10.0.0.5" } ],
                             "notReadyAddresses": [ { "ip": "10.0.0.9" } ],
                             "ports": [ { "port": 8080 } ] } ] }
            """);
        var empty = Obj("""{ "apiVersion": "v1", "kind": "Endpoints", "metadata": { "name": "x", "namespace": "n" } }""");

        await Assert.That(ResourceStatusSummary.Summarize(many).Details)
            .IsEqualTo("10.0.0.1:8080,10.0.0.2:8080,10.0.0.3:8080 + 2 more...");
        await Assert.That(ResourceStatusSummary.Summarize(empty).Details).IsEqualTo("<none>");
    }

    /// <summary><c>kubectl get endpointslice kube-dns-gn99q</c> → <c>IPv4   9153,53,53   10.42.0.90</c>.</summary>
    [Test]
    public async Task EndpointSlices_print_address_type_ports_and_endpoints()
    {
        var slice = Obj("""
            { "apiVersion": "discovery.k8s.io/v1", "kind": "EndpointSlice", "addressType": "IPv4",
              "metadata": { "name": "kube-dns-gn99q", "namespace": "kube-system" },
              "endpoints": [ { "addresses": [ "10.42.0.90" ], "conditions": { "ready": true } } ],
              "ports": [ { "name": "metrics", "port": 9153 }, { "name": "dns-tcp", "port": 53 }, { "name": "dns", "port": 53 } ] }
            """);
        var empty = Obj("""
            { "apiVersion": "discovery.k8s.io/v1", "kind": "EndpointSlice", "addressType": "IPv4",
              "metadata": { "name": "e", "namespace": "n" }, "endpoints": [], "ports": [ { "name": "http" } ] }
            """);

        await Assert.That(ResourceStatusSummary.Summarize(slice).Details).IsEqualTo("IPv4 · 9153,53,53 · 10.42.0.90");
        await Assert.That(ResourceStatusSummary.Summarize(empty).Details).IsEqualTo("IPv4 · http · <unset>");
    }

    /// <summary>
    /// <c>kubectl get ingress -n demo-shop shop</c> → CLASS <c>&lt;none&gt;</c>, HOSTS
    /// <c>shop.sandbox.local</c>, ADDRESS <c>172.17.0.2</c>, PORTS <c>80</c>. An absent class
    /// is left out of the cell rather than spelled &lt;none&gt;; a TLS entry adds 443.
    /// </summary>
    [Test]
    public async Task Ingresses_print_class_hosts_address_and_ports()
    {
        var shop = Obj("""
            { "apiVersion": "networking.k8s.io/v1", "kind": "Ingress", "metadata": { "name": "shop", "namespace": "demo-shop" },
              "spec": { "rules": [ { "host": "shop.sandbox.local" } ] },
              "status": { "loadBalancer": { "ingress": [ { "ip": "172.17.0.2" } ] } } }
            """);
        var tls = Obj("""
            { "apiVersion": "networking.k8s.io/v1", "kind": "Ingress", "metadata": { "name": "t", "namespace": "n" },
              "spec": { "ingressClassName": "nginx", "tls": [ { "hosts": [ "a.example.com" ] } ],
                        "rules": [ { "host": "a.example.com" }, { "host": "b.example.com" } ] } }
            """);

        await Assert.That(ResourceStatusSummary.Summarize(shop).Details).IsEqualTo("shop.sandbox.local · 172.17.0.2 · 80");
        await Assert.That(ResourceStatusSummary.Summarize(tls).Details).IsEqualTo("nginx · a.example.com,b.example.com · 80, 443");
    }

    /// <summary>
    /// kubectl's POD-SELECTOR, with the one deliberate difference: kubectl prints
    /// &lt;none&gt; for an empty selector, which reads as "selects nothing" and means every pod.
    /// </summary>
    [Test]
    public async Task NetworkPolicies_print_their_pod_selector_and_say_all_pods_for_an_empty_one()
    {
        var web = Obj("""
            { "apiVersion": "networking.k8s.io/v1", "kind": "NetworkPolicy", "metadata": { "name": "w", "namespace": "n" },
              "spec": { "podSelector": { "matchLabels": { "app": "web" } }, "policyTypes": [ "Ingress", "Egress" ] } }
            """);
        var deny = Obj("""
            { "apiVersion": "networking.k8s.io/v1", "kind": "NetworkPolicy", "metadata": { "name": "d", "namespace": "n" },
              "spec": { "podSelector": {}, "policyTypes": [ "Ingress" ] } }
            """);

        await Assert.That(ResourceStatusSummary.Summarize(web).Details).IsEqualTo("app=web · Ingress, Egress");
        await Assert.That(ResourceStatusSummary.Summarize(deny).Details).IsEqualTo("all pods · Ingress");
    }

    /// <summary>The Details column is shown for all four — before FEAT-60 a NetworkPolicy list was name and age.</summary>
    [Test]
    public async Task The_details_column_is_shown_for_the_networking_kinds()
    {
        await Assert.That(ResourceStatusSummary.ShowsDetails(Kind("", "Endpoints"))).IsTrue();
        await Assert.That(ResourceStatusSummary.ShowsDetails(Kind("discovery.k8s.io", "EndpointSlice"))).IsTrue();
        await Assert.That(ResourceStatusSummary.ShowsDetails(Kind("networking.k8s.io", "NetworkPolicy"))).IsTrue();
        await Assert.That(ResourceStatusSummary.ShowsDetails(Kind("networking.k8s.io", "Ingress"))).IsTrue();
    }

    // ------------------------------------------------------------------ FEAT-62

    /// <summary>
    /// Gateway API goes to Network by its group — every route kind, including ones this code
    /// has never heard of — while a CRD called Gateway in another group (Istio's) stays in
    /// CRDs, and so does the experimental x-k8s.io channel.
    /// </summary>
    [Test]
    [Arguments("gateway.networking.k8s.io", "Gateway", "Network")]
    [Arguments("gateway.networking.k8s.io", "GatewayClass", "Network")]
    [Arguments("gateway.networking.k8s.io", "HTTPRoute", "Network")]
    [Arguments("gateway.networking.k8s.io", "GRPCRoute", "Network")]
    [Arguments("gateway.networking.k8s.io", "SomeFutureRoute", "Network")]
    [Arguments("networking.istio.io", "Gateway", "CRDs")]
    [Arguments("gateway.networking.x-k8s.io", "XListenerSet", "CRDs")]
    public async Task Gateway_api_is_filed_under_network_by_group(string group, string kind, string section)
    {
        await Assert.That(SidebarGrouping.SectionFor(Kind(group, kind))).IsEqualTo(section);
    }
}
