using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The selector that decides which pods an aggregated log pane tails. Every failure
/// here is quiet: too wide and the pane streams pods the workload does not own (and
/// opens a connection per one of them), too narrow and a rolling deployment silently
/// loses half of itself — the old ReplicaSet's pods — which is precisely the case the
/// feature exists for. Neither shows up as an error.
/// </summary>
public class LabelSelectorTests
{
    private static DynamicResource Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static DynamicResource Workload(string selectorJson) => Object($$"""
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "api", "namespace": "payments" },
          "spec": { "selector": {{selectorJson}} }
        }
        """);

    // -------------------------------------------------------------------- parsing

    [Test]
    public async Task MatchLabels_becomes_an_equality_query()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""{ "matchLabels": { "app": "api" } }"""));

        await Assert.That(selector).IsNotNull();
        await Assert.That(selector!.ToQuery()).IsEqualTo("app=api");
    }

    [Test]
    public async Task Several_match_labels_are_comma_joined()
    {
        var selector = LabelSelector.ForPodsOf(
            Workload("""{ "matchLabels": { "app": "api", "tier": "backend" } }"""));

        await Assert.That(selector!.ToQuery()).IsEqualTo("app=api,tier=backend");
    }

    /// <summary>
    /// A Deployment's selector names the app, never the pod-template hash — which is
    /// exactly why a rollout's old and new ReplicaSets are both in scope. Pinned because
    /// "select the pods of this workload" is easy to implement as "select the pods of its
    /// current ReplicaSet", and that version reads a rollout as half a story.
    /// </summary>
    [Test]
    public async Task A_selector_that_omits_the_template_hash_matches_both_replica_sets()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""{ "matchLabels": { "app": "api" } }"""))!;

        var old = new Dictionary<string, string> { ["app"] = "api", ["pod-template-hash"] = "7f9c8d6bcd" };
        var @new = new Dictionary<string, string> { ["app"] = "api", ["pod-template-hash"] = "8c1a4f2e91" };

        await Assert.That(selector.Matches(old)).IsTrue();
        await Assert.That(selector.Matches(@new)).IsTrue();
    }

    [Test]
    public async Task MatchExpressions_render_in_the_api_servers_own_syntax()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""
            {
              "matchExpressions": [
                { "key": "tier", "operator": "In", "values": ["backend", "worker"] },
                { "key": "canary", "operator": "NotIn", "values": ["true"] },
                { "key": "app", "operator": "Exists" },
                { "key": "retired", "operator": "DoesNotExist" }
              ]
            }
            """));

        await Assert.That(selector!.ToQuery())
            .IsEqualTo("tier in (backend,worker),canary notin (true),app,!retired");
    }

    [Test]
    public async Task MatchLabels_and_matchExpressions_are_anded()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""
            {
              "matchLabels": { "app": "api" },
              "matchExpressions": [ { "key": "canary", "operator": "DoesNotExist" } ]
            }
            """));

        await Assert.That(selector!.ToQuery()).IsEqualTo("app=api,!canary");
    }

    /// <summary>A Service (and a ReplicationController) spells its selector as a plain map.</summary>
    [Test]
    public async Task A_plain_string_map_selector_is_read_too()
    {
        var service = Object("""
            {
              "apiVersion": "v1",
              "kind": "Service",
              "metadata": { "name": "api", "namespace": "payments" },
              "spec": { "selector": { "app": "api" }, "ports": [ { "port": 80 } ] }
            }
            """);

        await Assert.That(LabelSelector.ForPodsOf(service)!.ToQuery()).IsEqualTo("app=api");
    }

    // ------------------------------------------------------------------- refusals

    /// <summary>
    /// Kubernetes reads an empty selector as "everything". Honouring that here would open
    /// a log stream against every pod in the namespace because an object declared
    /// <c>selector: {}</c>; refusing means the action is simply not offered.
    /// </summary>
    [Test]
    public async Task An_empty_selector_is_refused_rather_than_read_as_everything()
    {
        await Assert.That(LabelSelector.ForPodsOf(Workload("{}"))).IsNull();
        await Assert.That(LabelSelector.ForPodsOf(Workload("""{ "matchLabels": {} }"""))).IsNull();
        await Assert.That(LabelSelector.ForPodsOf(Workload("""{ "matchExpressions": [] }"""))).IsNull();
    }

    [Test]
    public async Task An_object_with_no_selector_at_all_is_null()
    {
        var pod = Object("""
            {
              "apiVersion": "v1",
              "kind": "Pod",
              "metadata": { "name": "api-7f9", "namespace": "payments" },
              "spec": { "containers": [ { "name": "app" } ] }
            }
            """);

        await Assert.That(LabelSelector.ForPodsOf(pod)).IsNull();
    }

    /// <summary>
    /// An unknown operator drops that requirement, and dropping a requirement *widens*
    /// the selector. So a selector whose only requirement is unreadable comes back null
    /// instead of matching everything.
    /// </summary>
    [Test]
    public async Task An_unreadable_operator_does_not_widen_the_selector()
    {
        await Assert.That(LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "tier", "operator": "Gt", "values": ["3"] } ] }
            """))).IsNull();

        // In/NotIn with no values is invalid to the API server too.
        await Assert.That(LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "tier", "operator": "In", "values": [] } ] }
            """))).IsNull();
    }

    /// <summary>
    /// A selector object whose values are not all strings is not the plain-map shape; it
    /// is a LabelSelector with fields this build does not understand, and guessing would
    /// produce a query for the wrong pods.
    /// </summary>
    [Test]
    public async Task A_selector_that_is_neither_shape_is_refused()
    {
        await Assert.That(LabelSelector.ForPodsOf(Workload("""{ "somethingElse": { "a": "b" } }"""))).IsNull();
    }

    // ------------------------------------------------------------------- matching

    [Test]
    public async Task In_requires_the_label_to_be_present_with_one_of_the_values()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "tier", "operator": "In", "values": ["backend", "worker"] } ] }
            """))!;

        await Assert.That(selector.Matches(new Dictionary<string, string> { ["tier"] = "worker" })).IsTrue();
        await Assert.That(selector.Matches(new Dictionary<string, string> { ["tier"] = "frontend" })).IsFalse();
        await Assert.That(selector.Matches(new Dictionary<string, string>())).IsFalse();
    }

    /// <summary>
    /// The one that surprises people, and the API server's own behaviour: <c>notin</c>
    /// matches an object that carries no such label at all.
    /// </summary>
    [Test]
    public async Task NotIn_matches_an_object_with_no_such_label()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "canary", "operator": "NotIn", "values": ["true"] } ] }
            """))!;

        await Assert.That(selector.Matches(new Dictionary<string, string>())).IsTrue();
        await Assert.That(selector.Matches(new Dictionary<string, string> { ["canary"] = "false" })).IsTrue();
        await Assert.That(selector.Matches(new Dictionary<string, string> { ["canary"] = "true" })).IsFalse();
    }

    [Test]
    public async Task Exists_and_DoesNotExist_ignore_the_value()
    {
        var exists = LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "app", "operator": "Exists" } ] }
            """))!;
        var doesNot = LabelSelector.ForPodsOf(Workload("""
            { "matchExpressions": [ { "key": "app", "operator": "DoesNotExist" } ] }
            """))!;

        await Assert.That(exists.Matches(new Dictionary<string, string> { ["app"] = "" })).IsTrue();
        await Assert.That(exists.Matches(new Dictionary<string, string>())).IsFalse();
        await Assert.That(doesNot.Matches(new Dictionary<string, string>())).IsTrue();
        await Assert.That(doesNot.Matches(new Dictionary<string, string> { ["app"] = "api" })).IsFalse();
    }

    [Test]
    public async Task Every_requirement_must_hold()
    {
        var selector = LabelSelector.ForPodsOf(
            Workload("""{ "matchLabels": { "app": "api", "tier": "backend" } }"""))!;

        await Assert.That(selector.Matches(
            new Dictionary<string, string> { ["app"] = "api", ["tier"] = "backend" })).IsTrue();
        await Assert.That(selector.Matches(new Dictionary<string, string> { ["app"] = "api" })).IsFalse();
    }

    /// <summary>
    /// The query and the local match are two renderings of one selector, and the demo
    /// cluster uses the second where a live cluster uses the first. They must not be able
    /// to disagree about the same object.
    /// </summary>
    [Test]
    public async Task The_query_and_the_local_match_come_from_the_same_requirements()
    {
        var selector = LabelSelector.ForPodsOf(Workload("""
            {
              "matchLabels": { "app": "api" },
              "matchExpressions": [ { "key": "canary", "operator": "NotIn", "values": ["true"] } ]
            }
            """))!;

        await Assert.That(selector.ToQuery()).IsEqualTo("app=api,canary notin (true)");
        await Assert.That(selector.Requirements.Count).IsEqualTo(2);
        await Assert.That(selector.Matches(
            new Dictionary<string, string> { ["app"] = "api", ["canary"] = "true" })).IsFalse();
    }
}
