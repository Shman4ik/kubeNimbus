using System.Text.Json;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// NetworkPolicy read as rules. The traps are the empty selector (every pod, not no pod),
/// an empty rule list (deny), and an empty peer or port list (anyone / every port) — each
/// is what the YAML hides and what these words have to say.
/// </summary>
public class NetworkPolicyRulesTests
{
    private static DynamicResource Policy(string spec) => new(JsonDocument.Parse($$"""
        { "apiVersion": "networking.k8s.io/v1", "kind": "NetworkPolicy",
          "metadata": { "name": "p", "namespace": "payments" },
          "spec": {{spec}} }
        """).RootElement.Clone());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The trap the backlog row names: <c>podSelector: {}</c> is every pod in the
    /// namespace — how "default deny" is written — and must never read as "no selector",
    /// which is what <see cref="LabelSelector.Parse"/>'s null would say.
    /// </summary>
    [Test]
    [Arguments("{}")]
    [Arguments("""{ "matchLabels": {} }""")]
    [Arguments("""{ "matchLabels": {}, "matchExpressions": [] }""")]
    public async Task An_empty_pod_selector_selects_every_pod(string selector)
    {
        var view = NetworkPolicyRules.Read(Policy($$"""{ "podSelector": {{selector}}, "policyTypes": [ "Ingress" ] }"""));

        await Assert.That(view.PodSelector.IsEmpty).IsTrue();
        await Assert.That(view.PodSelector.Matches(new Dictionary<string, string>())).IsTrue();
        await Assert.That(view.PodSelector.Matches(new Dictionary<string, string> { ["app"] = "x" })).IsTrue();
        await Assert.That(NetworkPolicyRules.ListSummary(Policy("""{ "podSelector": {}, "policyTypes": [ "Ingress" ] }""")))
            .IsEqualTo("all pods · Ingress");
    }

    [Test]
    public async Task A_default_deny_policy_says_it_denies()
    {
        var view = NetworkPolicyRules.Read(Policy("""{ "podSelector": {}, "policyTypes": [ "Ingress", "Egress" ] }"""));

        await Assert.That(NetworkPolicyRules.DirectionSummary(view, ingress: true)).StartsWith("Denies all incoming traffic");
        await Assert.That(NetworkPolicyRules.DirectionSummary(view, ingress: false)).StartsWith("Denies all outgoing traffic");
    }

    /// <summary>
    /// The API server's own defaulting, for a manifest with no <c>policyTypes</c>: always
    /// Ingress, and Egress only when there are egress rules.
    /// </summary>
    [Test]
    public async Task Missing_policy_types_default_the_way_the_api_server_does()
    {
        var ingressOnly = NetworkPolicyRules.Read(Policy("""{ "podSelector": {}, "ingress": [ {} ] }"""));
        await Assert.That(ingressOnly.PolicyTypesText).IsEqualTo("Ingress");
        await Assert.That(ingressOnly.PolicyTypesDeclared).IsFalse();

        var both = NetworkPolicyRules.Read(Policy("""{ "podSelector": {}, "egress": [ { "ports": [ { "port": 53, "protocol": "UDP" } ] } ] }"""));
        await Assert.That(both.PolicyTypesText).IsEqualTo("Ingress, Egress");
    }

    [Test]
    public async Task A_rule_with_no_peers_and_no_ports_allows_everything()
    {
        var view = NetworkPolicyRules.Read(Policy("""{ "podSelector": { "matchLabels": { "app": "web" } }, "ingress": [ {} ] }"""));

        await Assert.That(view.Ingress[0].AllPeers).IsTrue();
        await Assert.That(view.Ingress[0].AllPorts).IsTrue();
        await Assert.That(NetworkPolicyRules.DirectionSummary(view, ingress: true)).StartsWith("Allows all incoming traffic");
        await Assert.That(NetworkPolicyRules.DirectionSummary(view, ingress: false)).StartsWith("Not restricted");
    }

    [Test]
    [Arguments("""{ "ipBlock": { "cidr": "10.0.0.0/8", "except": [ "10.96.0.0/12" ] } }""", "IP block 10.0.0.0/8 except 10.96.0.0/12")]
    [Arguments("""{ "podSelector": { "matchLabels": { "app": "web" } } }""", "pods with app=web in payments")]
    [Arguments("""{ "podSelector": {} }""", "every pod in payments")]
    [Arguments("""{ "namespaceSelector": {} }""", "every pod in every namespace")]
    [Arguments("""{ "namespaceSelector": { "matchLabels": { "team": "edge" } } }""", "every pod in namespaces with team=edge")]
    [Arguments("""{ "namespaceSelector": { "matchLabels": { "kubernetes.io/metadata.name": "ingress" } }, "podSelector": { "matchLabels": { "app": "traefik" } } }""", "pods with app=traefik in namespace ingress")]
    [Arguments("""{ "namespaceSelector": {}, "podSelector": { "matchLabels": { "app": "prom" } } }""", "pods with app=prom in every namespace")]
    public async Task Peers_read_as_phrases(string peer, string expected)
    {
        await Assert.That(NetworkPolicyRules.DescribePeer(Json(peer), "payments")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{ "port": 8080 }""", "TCP 8080")]
    [Arguments("""{ "port": "http", "protocol": "TCP" }""", "TCP http")]
    [Arguments("""{ "port": 32000, "endPort": 32768, "protocol": "UDP" }""", "UDP 32000–32768")]
    [Arguments("""{ "protocol": "SCTP" }""", "all SCTP ports")]
    public async Task Ports_read_as_protocol_and_port(string port, string expected)
    {
        await Assert.That(NetworkPolicyRules.DescribePort(Json(port))).IsEqualTo(expected);
    }

    /// <summary>
    /// A requirement this build cannot read must not be dropped: dropping it widens the
    /// selector, and a policy pane that lists pods the policy does not select is worse than
    /// one that says it cannot tell.
    /// </summary>
    [Test]
    public async Task An_unreadable_selector_is_stated_and_matches_nothing()
    {
        var selector = NetworkPolicyRules.ReadSelector(Json("""
            { "matchLabels": { "app": "web" },
              "matchExpressions": [ { "key": "tier", "operator": "Near", "values": [ "x" ] } ] }
            """));

        await Assert.That(selector.IsUnreadable).IsTrue();
        await Assert.That(selector.IsEmpty).IsFalse();
        await Assert.That(selector.Matches(new Dictionary<string, string> { ["app"] = "web" })).IsFalse();
    }
}
