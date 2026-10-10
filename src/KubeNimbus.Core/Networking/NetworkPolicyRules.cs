using System.Globalization;
using System.Text.Json;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Networking;

/// <summary>
/// A label selector as a NetworkPolicy uses one, where empty means <em>everything</em>.
/// </summary>
/// <param name="IsEmpty">Declares no requirement. For a NetworkPolicy that is not "no
/// selector": <c>podSelector: {}</c> selects every pod in the namespace, and
/// <c>namespaceSelector: {}</c> every namespace.</param>
/// <param name="Selector">The parsed requirements; null when empty or unreadable.</param>
/// <param name="IsUnreadable">Declares something this build cannot evaluate faithfully —
/// an operator it does not know, an <c>In</c> with no values. Stated rather than guessed,
/// because dropping the requirement would widen the selector.</param>
public sealed record PolicySelector(bool IsEmpty, LabelSelector? Selector, bool IsUnreadable)
{
    /// <summary>"app=web,tier in (a,b)"; empty for an empty selector.</summary>
    public string Text => Selector?.ToQuery() ?? "";

    public bool Matches(IReadOnlyDictionary<string, string> labels) =>
        IsEmpty || (Selector is { } selector && !IsUnreadable && selector.Matches(labels));
}

/// <summary>One ingress or egress rule, in words.</summary>
/// <param name="Peers">"from"/"to" peers, each already a phrase; empty with
/// <paramref name="AllPeers"/> set when the rule names none (anyone).</param>
/// <param name="Ports">"TCP 8080", "UDP 53", "TCP 30000–32767"; empty with
/// <paramref name="AllPorts"/> set when the rule names none (every port).</param>
public sealed record NetworkPolicyRule(
    IReadOnlyList<string> Peers, bool AllPeers, IReadOnlyList<string> Ports, bool AllPorts);

/// <summary>A NetworkPolicy read into the facts its detail pane states.</summary>
/// <param name="AffectsIngress">Ingress is among the effective policy types.</param>
/// <param name="AffectsEgress">Egress is among the effective policy types.</param>
/// <param name="PolicyTypesDeclared">False when <c>spec.policyTypes</c> is absent and the
/// types were derived the way the API server defaults them.</param>
public sealed record NetworkPolicyView(
    PolicySelector PodSelector,
    bool AffectsIngress,
    bool AffectsEgress,
    bool PolicyTypesDeclared,
    IReadOnlyList<NetworkPolicyRule> Ingress,
    IReadOnlyList<NetworkPolicyRule> Egress)
{
    public string PolicyTypesText => (AffectsIngress, AffectsEgress) switch
    {
        (true, true) => "Ingress, Egress",
        (true, false) => "Ingress",
        (false, true) => "Egress",
        _ => "none",
    };
}

/// <summary>
/// NetworkPolicy as rules rather than YAML: which pods it selects, which directions it
/// restricts, and who may reach them (or be reached) on which ports.
///
/// <para>
/// <b>The empty selector is the trap, and it is the opposite of <see cref="LabelSelector"/>'s
/// rule.</b> That type refuses an empty selector on purpose — for a workload's logs, "every
/// pod" is a failure. For a NetworkPolicy, <c>podSelector: {}</c> genuinely means every
/// pod in the namespace (it is how "default deny" is written), so this reads emptiness
/// itself and never lets a null from <see cref="LabelSelector.Parse"/> stand for it.
/// </para>
///
/// <para>
/// <b>The words carry the semantics that the YAML hides.</b> A direction in
/// <c>policyTypes</c> with no rules denies everything that way; a rule with no peers
/// allows anyone; a rule with no ports allows every port. Those three are what people
/// misread, so each is said outright rather than shown as an empty list.
/// </para>
/// </summary>
public static class NetworkPolicyRules
{
    public static NetworkPolicyView Read(DynamicResource policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var spec = J.Obj(policy.Raw, "spec");
        var ns = policy.Namespace ?? "";
        var podSelector = ReadSelector(J.Obj(spec, "podSelector"));

        var ingress = J.Arr(spec, "ingress").Select(r => ReadRule(r, "from", ns)).ToList();
        var egress = J.Arr(spec, "egress").Select(r => ReadRule(r, "to", ns)).ToList();

        var declared = J.Arr(spec, "policyTypes")
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString() ?? "")
            .ToList();

        // The API server's own defaulting (pkg/apis/networking/v1/defaults.go): with no
        // policyTypes, every policy affects ingress and one with egress rules affects
        // egress too. Objects read back from a server always carry the defaulted list;
        // this matters for manifests and the demo dataset.
        var affectsIngress = declared.Count > 0 ? declared.Contains("Ingress", StringComparer.Ordinal) : true;
        var affectsEgress = declared.Count > 0 ? declared.Contains("Egress", StringComparer.Ordinal) : egress.Count > 0;

        return new NetworkPolicyView(podSelector, affectsIngress, affectsEgress, declared.Count > 0, ingress, egress);
    }

    /// <summary>
    /// The list's one-cell summary: what the policy selects and which ways it restricts.
    /// kubectl prints <c>&lt;none&gt;</c> for an empty pod selector, which reads as "selects
    /// nothing" and means the opposite; this says "all pods".
    /// </summary>
    public static string ListSummary(DynamicResource policy)
    {
        var view = Read(policy);
        var selector = view.PodSelector switch
        {
            { IsEmpty: true } => "all pods",
            { IsUnreadable: true } => "unreadable selector",
            var s => s.Text,
        };
        return $"{selector} · {view.PolicyTypesText}";
    }

    /// <summary>
    /// The sentence for one direction: whether the policy restricts it at all, denies it
    /// outright, allows everything, or allows only what its rules list.
    /// </summary>
    public static string DirectionSummary(NetworkPolicyView view, bool ingress)
    {
        ArgumentNullException.ThrowIfNull(view);

        var affects = ingress ? view.AffectsIngress : view.AffectsEgress;
        var rules = ingress ? view.Ingress : view.Egress;
        var way = ingress ? "incoming" : "outgoing";

        if (!affects)
        {
            return rules.Count > 0
                ? $"Not restricted: policyTypes leaves {(ingress ? "Ingress" : "Egress")} out, so the {way} rules below are ignored."
                : $"Not restricted by this policy — {way} traffic is decided by the other policies that select these pods, if any.";
        }

        if (rules.Count == 0)
        {
            return $"Denies all {way} traffic to the selected pods, unless another policy that selects them allows it.";
        }

        if (rules.Any(r => r.AllPeers && r.AllPorts))
        {
            return $"Allows all {way} traffic: a rule names no peers and no ports.";
        }

        return $"Allows only the {way} traffic below; everything else {(ingress ? "to" : "from")} the selected pods is denied "
               + "unless another policy that selects them allows it.";
    }

    /// <summary>
    /// Reads a selector with NetworkPolicy's meaning of empty: a selector that declares
    /// nothing is every pod, and one that declares something <see cref="LabelSelector.Parse"/>
    /// refuses is unreadable. Parse refuses a selector whole rather than skipping the part it
    /// cannot read (ENG-50), so its null is trusted here once emptiness has been ruled out.
    /// </summary>
    public static PolicySelector ReadSelector(JsonElement selector)
    {
        if (selector.ValueKind != JsonValueKind.Object)
        {
            return new PolicySelector(IsEmpty: true, null, IsUnreadable: false);
        }

        var declaresSomething = false;
        foreach (var property in selector.EnumerateObject())
        {
            if (property.Name is not ("matchLabels" or "matchExpressions"))
            {
                // A field a LabelSelector does not have — including the plain label map a
                // Service uses, which Parse accepts and a NetworkPolicy does not: not
                // something to evaluate.
                return new PolicySelector(IsEmpty: false, null, IsUnreadable: true);
            }

            declaresSomething |= property.Value.ValueKind switch
            {
                JsonValueKind.Null => false,
                JsonValueKind.Object => property.Value.EnumerateObject().Any(),
                JsonValueKind.Array => property.Value.GetArrayLength() > 0,
                _ => true,
            };
        }

        if (!declaresSomething)
        {
            return new PolicySelector(IsEmpty: true, null, IsUnreadable: false);
        }

        return LabelSelector.Parse(selector) is { } parsed
            ? new PolicySelector(IsEmpty: false, parsed, IsUnreadable: false)
            : new PolicySelector(IsEmpty: false, null, IsUnreadable: true);
    }

    private static NetworkPolicyRule ReadRule(JsonElement rule, string peersField, string policyNamespace)
    {
        var peers = J.Arr(rule, peersField).Select(p => DescribePeer(p, policyNamespace)).ToList();
        var ports = J.Arr(rule, "ports").Select(DescribePort).ToList();
        return new NetworkPolicyRule(peers, peers.Count == 0, ports, ports.Count == 0);
    }

    /// <summary>One peer as a phrase. The combinations are the API's own: a peer with both
    /// selectors means pods matching one <em>in</em> namespaces matching the other.</summary>
    internal static string DescribePeer(JsonElement peer, string policyNamespace)
    {
        var ipBlock = J.Obj(peer, "ipBlock");
        if (ipBlock.ValueKind == JsonValueKind.Object)
        {
            var cidr = J.Str(ipBlock, "cidr");
            var except = J.Arr(ipBlock, "except")
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .ToList();
            return except.Count > 0 ? $"IP block {cidr} except {string.Join(", ", except)}" : $"IP block {cidr}";
        }

        var hasPods = peer.ValueKind == JsonValueKind.Object && peer.TryGetProperty("podSelector", out _);
        var hasNamespaces = peer.ValueKind == JsonValueKind.Object && peer.TryGetProperty("namespaceSelector", out _);
        var pods = ReadSelector(J.Obj(peer, "podSelector"));
        var namespaces = ReadSelector(J.Obj(peer, "namespaceSelector"));

        var podText = pods switch
        {
            { IsUnreadable: true } => "pods matching a selector this app cannot read",
            { IsEmpty: true } => "every pod",
            var s => $"pods with {s.Text}",
        };

        if (!hasNamespaces)
        {
            return hasPods ? $"{podText} in {policyNamespace}" : "no one (an empty peer)";
        }

        var namespaceText = namespaces switch
        {
            { IsUnreadable: true } => "namespaces matching a selector this app cannot read",
            { IsEmpty: true } => "every namespace",
            var s when NamedNamespaces(s) is { } names => names.Count == 1 ? $"namespace {names[0]}" : $"namespaces {string.Join(", ", names)}",
            var s => $"namespaces with {s.Text}",
        };

        return hasPods && !pods.IsEmpty ? $"{podText} in {namespaceText}" : $"every pod in {namespaceText}";
    }

    /// <summary>
    /// <c>kubernetes.io/metadata.name in (a, b)</c> is how a namespace is named in a peer
    /// (the API server sets that label on every namespace), and "namespace ingress" reads
    /// better than the selector that spells it.
    /// </summary>
    private static IReadOnlyList<string>? NamedNamespaces(PolicySelector selector) =>
        selector.Selector is { Requirements: [{ Key: "kubernetes.io/metadata.name", Operator: LabelOperator.In } only] }
            ? only.Values
            : null;

    internal static string DescribePort(JsonElement port)
    {
        var protocol = J.Str(port, "protocol") is { Length: > 0 } p ? p : "TCP";
        if (!port.TryGetProperty("port", out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return $"all {protocol} ports";
        }

        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString() ?? "";
        return J.Int(port, "endPort") is { } end
            ? $"{protocol} {text}–{end.ToString(CultureInfo.InvariantCulture)}"
            : $"{protocol} {text}";
    }
}
