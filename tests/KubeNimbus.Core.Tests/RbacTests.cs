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
}
