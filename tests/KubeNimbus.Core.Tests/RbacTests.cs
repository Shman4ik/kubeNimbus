using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Rule parsing and display formatting for the access-review panel. Pure unit
/// tests — the live half (SelfSubjectRulesReview, binding lookup) is covered by
/// <see cref="RbacIntegrationTests"/> against a sandbox cluster.
/// </summary>
public class RbacTests
{
    private const string RulesJson = """
        {
          "rules": [
            { "verbs": ["get", "list", "watch"], "apiGroups": [""], "resources": ["pods", "pods/log"] },
            { "verbs": ["create"], "apiGroups": ["apps"], "resources": ["deployments"], "resourceNames": ["checkout"] },
            { "verbs": ["*"], "apiGroups": ["*"], "resources": ["*"] },
            { "verbs": ["get"], "nonResourceURLs": ["/healthz"] }
          ]
        }
        """;

    private static IReadOnlyList<PolicyRule> Rules()
    {
        using var doc = JsonDocument.Parse(RulesJson);
        return ClusterClient.ReadRules(doc.RootElement, "rules");
    }

    [Test]
    public async Task Reads_every_rule_shape()
    {
        var rules = Rules();

        await Assert.That(rules.Count).IsEqualTo(4);
        await Assert.That(rules[0].VerbsText).IsEqualTo("get, list, watch");
        await Assert.That(rules[0].ResourcesText).IsEqualTo("pods, pods/log");
        await Assert.That(rules[1].ResourcesText).IsEqualTo("deployments.apps");
        await Assert.That(rules[1].ResourceNamesText).IsEqualTo("checkout");
        await Assert.That(rules[3].ResourcesText).IsEqualTo("/healthz");
    }

    [Test]
    public async Task Flags_only_the_grant_everything_rule()
    {
        var rules = Rules();

        await Assert.That(rules[0].IsWildcard).IsFalse();
        await Assert.That(rules[2].IsWildcard).IsTrue();
    }

    [Test]
    public async Task Returns_no_rules_for_a_role_without_any()
    {
        using var doc = JsonDocument.Parse("""{ "kind": "Role" }""");

        await Assert.That(ClusterClient.ReadRules(doc.RootElement, "rules")).IsEmpty();
    }

    [Test]
    public async Task Binding_scope_reads_as_the_namespace_or_cluster_wide()
    {
        var namespaced = new SubjectBinding("RoleBinding", "reader", "payments", "Role", "pod-reader", []);
        var cluster = new SubjectBinding("ClusterRoleBinding", "admins", null, "ClusterRole", "cluster-admin", []);

        await Assert.That(namespaced.Scope).IsEqualTo("payments");
        await Assert.That(cluster.Scope).IsEqualTo("cluster-wide");
        await Assert.That(cluster.RoleText).IsEqualTo("ClusterRole/cluster-admin");
        await Assert.That(namespaced.HasRules).IsFalse();
    }
}

/// <summary>
/// The rule-matching half of "who can do X?". These pin the API server's own RBAC
/// semantics, which is the whole reason the scan is trustworthy at all — get one of
/// these wrong and the view confidently lists the wrong subjects.
/// </summary>
public class WhoCanMatchingTests
{
    private static PolicyRule Rule(
        string[] verbs, string[] groups, string[] resources, string[]? names = null) =>
        new(verbs, groups, resources, names ?? [], []);

    private static AccessQuery Query(
        string verb = "delete", string resource = "pods", string group = "", string? name = null) =>
        new(verb, resource, group, name);

    [Test]
    public async Task Matches_an_exact_verb_group_and_resource()
    {
        var rule = Rule(["get", "delete"], [""], ["pods"]);

        await Assert.That(ClusterClient.Matches(rule, Query())).IsTrue();
        await Assert.That(ClusterClient.Matches(rule, Query(verb: "create"))).IsFalse();
        await Assert.That(ClusterClient.Matches(rule, Query(resource: "secrets"))).IsFalse();
        await Assert.That(ClusterClient.Matches(rule, Query(group: "apps"))).IsFalse();
    }

    [Test]
    public async Task Wildcards_match_every_verb_group_and_resource()
    {
        var rule = Rule(["*"], ["*"], ["*"]);

        await Assert.That(ClusterClient.Matches(rule, Query())).IsTrue();
        await Assert.That(ClusterClient.Matches(rule, Query(resource: "widgets", group: "example.com"))).IsTrue();
    }

    [Test]
    public async Task Wildcard_is_whole_value_never_a_prefix()
    {
        // RBAC has no partial wildcards: "pods/*" is a literal string the API server
        // matches against nothing. A glob implementation here would invent access.
        var rule = Rule(["*"], [""], ["pods/*"]);

        await Assert.That(ClusterClient.Matches(rule, Query(resource: "pods/log"))).IsFalse();
        await Assert.That(ClusterClient.Matches(rule, Query(resource: "pods/*"))).IsTrue();
    }

    [Test]
    public async Task A_resource_rule_does_not_cover_its_subresources()
    {
        var rule = Rule(["get"], [""], ["pods"]);

        await Assert.That(ClusterClient.Matches(rule, Query(verb: "get", resource: "pods/log"))).IsFalse();
        await Assert.That(ClusterClient.Matches(Rule(["get"], [""], ["pods/log"]), Query(verb: "get", resource: "pods/log"))).IsTrue();
    }

    [Test]
    public async Task The_core_group_is_the_empty_string_on_both_sides()
    {
        await Assert.That(ClusterClient.Matches(Rule(["get"], [""], ["pods"]), Query(verb: "get"))).IsTrue();
        await Assert.That(ClusterClient.Matches(Rule(["get"], ["core"], ["pods"]), Query(verb: "get"))).IsFalse();
    }

    [Test]
    public async Task A_non_resource_rule_never_grants_a_resource_verb()
    {
        var rule = new PolicyRule(["get"], [], [], [], ["/healthz"]);

        await Assert.That(ClusterClient.Matches(rule, Query(verb: "get"))).IsFalse();
    }

    [Test]
    public async Task ResourceNames_narrow_a_named_query_but_still_answer_an_unnamed_one()
    {
        var rule = Rule(["delete"], [""], ["pods"], ["checkout-0"]);

        // Unnamed question: the subject genuinely can delete *a* pod, so it is listed
        // (the UI shows which names) rather than silently dropped.
        await Assert.That(ClusterClient.Matches(rule, Query())).IsTrue();
        await Assert.That(ClusterClient.Matches(rule, Query(name: "checkout-0"))).IsTrue();
        await Assert.That(ClusterClient.Matches(rule, Query(name: "checkout-1"))).IsFalse();

        // An unrestricted rule answers a named question too.
        await Assert.That(ClusterClient.Matches(Rule(["delete"], [""], ["pods"]), Query(name: "checkout-1"))).IsTrue();
    }

    [Test]
    public async Task Query_splits_a_subresource_for_SubjectAccessReview()
    {
        var plain = new AccessQuery("get", "pods");
        var sub = new AccessQuery("get", "pods/log", Namespace: "payments");

        await Assert.That(plain.BaseResource).IsEqualTo("pods");
        await Assert.That(plain.Subresource).IsNull();
        await Assert.That(sub.BaseResource).IsEqualTo("pods");
        await Assert.That(sub.Subresource).IsEqualTo("log");
    }

    [Test]
    public async Task Query_text_states_the_scope_it_actually_asked_about()
    {
        await Assert.That(new AccessQuery("delete", "deployments", "apps", Namespace: "payments").Text)
            .IsEqualTo("delete deployments.apps in payments");
        await Assert.That(new AccessQuery("delete", "pods", ResourceName: "checkout-0").Text)
            .IsEqualTo("delete pods/checkout-0 in any namespace");
    }

    [Test]
    public async Task Subject_access_reports_cluster_wide_ahead_of_its_namespaces()
    {
        var rules = new[] { new PolicyRule(["*"], ["*"], ["*"], [], []) };
        var namespaced = new SubjectAccess(
            new SubjectRef("ServiceAccount", "deploy-bot", "payments"),
            [new SubjectBinding("RoleBinding", "deployers", "payments", "Role", "deployer", rules)]);
        var clusterWide = new SubjectAccess(
            new SubjectRef("Group", "system:masters", null),
            [new SubjectBinding("ClusterRoleBinding", "admins", null, "ClusterRole", "cluster-admin", rules)]);

        await Assert.That(namespaced.DisplayName).IsEqualTo("ServiceAccount payments:deploy-bot");
        await Assert.That(namespaced.IsClusterWide).IsFalse();
        await Assert.That(namespaced.ScopeText).IsEqualTo("payments");
        await Assert.That(clusterWide.DisplayName).IsEqualTo("Group system:masters");
        await Assert.That(clusterWide.IsClusterWide).IsTrue();
        await Assert.That(clusterWide.ScopeText).IsEqualTo("cluster-wide");
        await Assert.That(clusterWide.ViaWildcard).IsTrue();
    }

    [Test]
    public async Task A_result_says_out_loud_when_it_could_not_read_everything()
    {
        var query = new AccessQuery("get", "pods");

        await Assert.That(new WhoCanResult(query, [], []).IsPartial).IsFalse();
        await Assert.That(new WhoCanResult(query, [], ["Could not list roles: 403"]).IsPartial).IsTrue();
    }

    [Test]
    public async Task A_decision_distinguishes_denied_from_merely_not_allowed()
    {
        await Assert.That(new AccessDecision(true, false, null, null).Text).IsEqualTo("allowed");
        await Assert.That(new AccessDecision(false, true, "webhook", null).Text).IsEqualTo("denied");
        await Assert.That(new AccessDecision(false, false, null, null).Text).IsEqualTo("not allowed");
    }
}

/// <summary>
/// Live half of the RBAC surface. Skips cleanly without a sandbox cluster, same
/// convention as the other integration suites.
/// </summary>
public class RbacIntegrationTests
{
    [Test]
    [Timeout(30_000)]
    public async Task SelfSubjectRules_answers_for_the_current_user(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = ClusterClient.Connect(context);
        var rules = await client.GetSelfSubjectRulesAsync("default", ct);

        // The sandbox kubeconfig is cluster-admin, so it must come back with the
        // wildcard rule; more importantly, this proves the review round-trips.
        await Assert.That(rules.ResourceRules).IsNotEmpty();
        await Assert.That(rules.ResourceRules.Any(r => r.IsWildcard)).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task Bindings_for_a_built_in_service_account_resolve_their_roles(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = ClusterClient.Connect(context);

        // Every cluster binds system:kube-controller-manager (a User subject) or
        // at minimum has bindings for its own system ServiceAccounts; an empty
        // result is still a valid answer, so this asserts shape, not count.
        var bindings = await client.GetBindingsForSubjectAsync(
            new SubjectRef("ServiceAccount", "default", "kube-system"), ct);

        foreach (var binding in bindings)
        {
            await Assert.That(binding.BindingName).IsNotEmpty();
            await Assert.That(binding.RoleName).IsNotEmpty();
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task WhoCan_finds_the_cluster_admin_subjects(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = ClusterClient.Connect(context);
        var result = await client.WhoCanAsync(new AccessQuery("delete", "pods"), ct);

        // Every conformant cluster binds cluster-admin to the system:masters group, so
        // "who can delete pods anywhere" can never legitimately come back empty.
        await Assert.That(result.Subjects).IsNotEmpty();
        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(result.Subjects.Any(s => s.IsClusterWide && s.ViaWildcard)).IsTrue();

        foreach (var subject in result.Subjects)
        {
            await Assert.That(subject.Bindings).IsNotEmpty();
            await Assert.That(subject.Bindings.All(b => b.Rules.Count > 0)).IsTrue();
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task WhoCan_agrees_with_the_API_servers_own_verdict(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = ClusterClient.Connect(context);
        var query = new AccessQuery("delete", "pods", Namespace: "default");
        var result = await client.WhoCanAsync(query, ct);

        // The scan is a local read of RBAC; SubjectAccessReview is the server's own
        // answer. They can legitimately differ (webhook authorizers), but on a plain
        // RBAC-only cluster a subject the scan found must come back allowed — that
        // round-trip is what this asserts.
        var scanned = result.Subjects.FirstOrDefault(s => s.Subject.Kind == "ServiceAccount");
        if (scanned is null)
        {
            return;
        }

        var decision = await client.CheckAccessAsync(scanned.Subject, query, ct);
        await Assert.That(decision.Allowed).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task A_cluster_scoped_query_only_consults_cluster_role_bindings(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();
        if (context is null)
        {
            return;
        }

        using var client = ClusterClient.Connect(context);
        var result = await client.WhoCanAsync(
            new AccessQuery("list", "nodes", ClusterScopedResource: true), ct);

        // A RoleBinding confines even a ClusterRole to its namespace, where a Node does
        // not exist — so no namespaced binding may ever appear in this answer.
        await Assert.That(result.Subjects.SelectMany(s => s.Bindings).All(b => b.BindingNamespace is null)).IsTrue();
    }
}
