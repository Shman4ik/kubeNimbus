using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #308's slice: a ResourceQuota row says whether its namespace is out of room. The full
/// quota is the sandbox's own (k3s 1.33, <c>bundle-t</c>), read after a third pod was refused
/// with "exceeded quota"; its Details is what <c>kubectl get quota</c> printed in its REQUEST
/// and LIMIT columns, joined.
/// </summary>
public class ResourceQuotaSummaryTests
{
    private static ResourceSummary Quota(string hard, string? used)
    {
        var status = used is null ? $$"""{ "hard": {{hard}} }""" : $$"""{ "hard": {{hard}}, "used": {{used}} }""";
        using var document = JsonDocument.Parse($$"""
            { "apiVersion": "v1", "kind": "ResourceQuota", "metadata": { "name": "q", "namespace": "n" },
              "spec": { "hard": {{hard}} }, "status": {{status}} }
            """);
        return ResourceStatusSummary.Summarize(new DynamicResource(document.RootElement.Clone()));
    }

    [Test]
    public async Task A_quota_at_its_limit_is_full_and_names_what_is_full()
    {
        var summary = Quota(
            """{ "limits.memory": "1Gi", "pods": "2", "requests.cpu": "1", "services.loadbalancers": "0" }""",
            """{ "limits.memory": "1Gi", "pods": "2", "requests.cpu": "1", "services.loadbalancers": "0" }""");

        await Assert.That(summary.Status).IsEqualTo("Full: limits.memory, pods, requests.cpu");
        await Assert.That(summary.Health).IsEqualTo(ResourceHealth.Error);
        await Assert.That(summary.Details)
            .IsEqualTo("limits.memory: 1Gi/1Gi, pods: 2/2, requests.cpu: 1/1, services.loadbalancers: 0/0");
    }

    /// <summary>A hard limit of 0 forbids a resource; it does not make the namespace full.</summary>
    [Test]
    public async Task A_resource_forbidden_with_a_hard_limit_of_zero_is_not_full()
    {
        var summary = Quota(
            """{ "pods": "20", "services.loadbalancers": "0" }""",
            """{ "pods": "3", "services.loadbalancers": "0" }""");

        await Assert.That(summary.Status).IsEqualTo("Within quota");
        await Assert.That(summary.Health).IsEqualTo(ResourceHealth.Ok);
    }

    [Test]
    public async Task A_quota_from_ninety_percent_is_almost_full_with_its_fullest_resource()
    {
        var summary = Quota(
            """{ "limits.memory": "4Gi", "pods": "20", "requests.cpu": "4" }""",
            """{ "limits.memory": "3840Mi", "pods": "14", "requests.cpu": "2350m" }""");

        await Assert.That(summary.Status).IsEqualTo("Almost full: limits.memory 93%");
        await Assert.That(summary.Health).IsEqualTo(ResourceHealth.Warn);
        await Assert.That(ResourceHealth.IsUnhealthy(summary.Health)).IsTrue();

        var roomy = Quota("""{ "pods": "20" }""", """{ "pods": "17" }""");
        await Assert.That(roomy.Status).IsEqualTo("Within quota");
    }

    /// <summary>Before the quota controller has counted, the row claims nothing and still lists the limits.</summary>
    [Test]
    public async Task A_quota_not_yet_counted_claims_nothing()
    {
        var summary = Quota("""{ "pods": "2" }""", used: null);

        await Assert.That(summary.Status).IsEqualTo("");
        await Assert.That(summary.Health).IsEqualTo(ResourceHealth.Idle);
        await Assert.That(summary.Details).IsEqualTo("pods: —/2");
    }

    /// <summary>
    /// "Within quota" is a claim about every limit: an empty <c>used</c>, or one missing a limit,
    /// leaves that limit uncounted (<c>—</c>) and the row claiming nothing. A limit that was
    /// counted can still be full on its own evidence.
    /// </summary>
    [Test]
    public async Task A_limit_with_no_used_figure_is_never_counted_as_within_quota()
    {
        var empty = Quota("""{ "pods": "20" }""", "{}");
        await Assert.That(empty.Status).IsEqualTo("");
        await Assert.That(empty.Health).IsEqualTo(ResourceHealth.Idle);
        await Assert.That(empty.Details).IsEqualTo("pods: —/20");

        var partial = Quota("""{ "pods": "20", "requests.cpu": "4" }""", """{ "pods": "3" }""");
        await Assert.That(partial.Status).IsEqualTo("");
        await Assert.That(partial.Health).IsEqualTo(ResourceHealth.Idle);
        await Assert.That(partial.Details).IsEqualTo("pods: 3/20, requests.cpu: —/4");

        var fullAnyway = Quota("""{ "pods": "3", "requests.cpu": "4" }""", """{ "pods": "3" }""");
        await Assert.That(fullAnyway.Status).IsEqualTo("Full: pods");
    }

    /// <summary>
    /// UI rule 20 for the demo: its quotas' counts are the demo's own objects — the non-terminal
    /// pods and the claims in each quota's namespace — so the demo never states a figure it does
    /// not have.
    /// </summary>
    [Test]
    public async Task The_demo_quotas_count_the_demos_own_objects()
    {
        var quotas = KubeNimbus.App.Demo.DemoData.OfKind("ResourceQuota");
        await Assert.That(quotas.Count).IsEqualTo(3);
        foreach (var quota in quotas)
        {
            var used = quota.Raw.GetProperty("status").GetProperty("used");
            var ns = quota.Namespace;
            if (used.TryGetProperty("pods", out var pods))
            {
                var live = KubeNimbus.App.Demo.DemoData.Pods.Count(p => p.Namespace == ns
                    && !(p.Raw.TryGetProperty("status", out var s) && s.TryGetProperty("phase", out var phase)
                         && phase.GetString() is "Succeeded" or "Failed"));
                await Assert.That(int.Parse(pods.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                    .IsEqualTo(live).Because($"{quota.Name}: pods");
            }

            if (used.TryGetProperty("persistentvolumeclaims", out var claims))
            {
                var bound = KubeNimbus.App.Demo.DemoData.OfKind("PersistentVolumeClaim").Count(c => c.Namespace == ns);
                await Assert.That(int.Parse(claims.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                    .IsEqualTo(bound).Because($"{quota.Name}: persistentvolumeclaims");
            }
        }

        var statuses = quotas.Select(q => ResourceStatusSummary.Summarize(q).Health).ToList();
        await Assert.That(statuses).Contains(ResourceHealth.Error);
        await Assert.That(statuses).Contains(ResourceHealth.Warn);
    }

    [Test]
    public async Task The_quota_list_has_a_Details_column()
    {
        var descriptor = new ResourceDescriptor("", "v1", "ResourceQuota", "resourcequotas", "resourcequota",
            Namespaced: true, ShortNames: ["quota"], Categories: []);
        await Assert.That(ResourceStatusSummary.ShowsDetails(descriptor)).IsTrue();
        await Assert.That(ResourceStatusSummary.ShowsStatus(descriptor)).IsTrue();
    }
}
