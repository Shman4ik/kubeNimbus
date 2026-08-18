using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The apply preview's diff engine. No cluster is needed and none should be: the whole
/// behaviour is decided by two JSON documents — the object as the server holds it and
/// the object the server's <c>dryRun=All</c> response says it would hold.
///
/// <para>
/// Every failure mode here is quiet in the same way the printer columns' was. A diff
/// that pairs list elements wrongly does not throw, it reports six containers as changed
/// when one was inserted; a diff that leaks <c>managedFields</c> does not throw, it
/// buries the one line someone needed under the apply's own bookkeeping. Both look like
/// a working feature until you read the output.
/// </para>
/// </summary>
public class ResourceDiffTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ResourceDiff Diff(string before, string after) => ResourceDiff.Between(Json(before), Json(after));

    private static string Describe(ResourceDiff diff) =>
        string.Join(" | ", diff.Changes.Select(c => c.Kind switch
        {
            ResourceChangeKind.Added => $"+{c.Path}={c.After}",
            ResourceChangeKind.Removed => $"-{c.Path}={c.Before}",
            _ => $"~{c.Path}:{c.Before}→{c.After}",
        }));

    [Test]
    public async Task An_identical_object_produces_no_changes()
    {
        var diff = Diff("""{"spec":{"replicas":3}}""", """{"spec":{"replicas":3}}""");

        await Assert.That(diff.IsEmpty).IsTrue();
        await Assert.That(diff.TotalChanges).IsEqualTo(0);
        await Assert.That(diff.IsCreate).IsFalse();
    }

    [Test]
    public async Task A_changed_scalar_reports_both_sides()
    {
        var diff = Diff("""{"spec":{"replicas":3}}""", """{"spec":{"replicas":5}}""");

        await Assert.That(Describe(diff)).IsEqualTo("~spec.replicas:3→5");
    }

    [Test]
    public async Task An_added_and_a_removed_field_are_distinguished()
    {
        var diff = Diff(
            """{"metadata":{"labels":{"app":"web","legacy":"yes"}}}""",
            """{"metadata":{"labels":{"app":"web","tier":"front"}}}""");

        await Assert.That(Describe(diff))
            .IsEqualTo("+metadata.labels.tier=front | -metadata.labels.legacy=yes");
    }

    /// <summary>
    /// A type change is a change, not a pair of add/remove. <c>replicas: "3"</c> against
    /// <c>replicas: 3</c> is exactly the mistake a hand-edited manifest makes, and the
    /// server's answer to it is the reason to preview.
    /// </summary>
    [Test]
    public async Task A_value_that_changes_type_is_one_change()
    {
        var diff = Diff("""{"spec":{"replicas":3}}""", """{"spec":{"replicas":"3"}}""");

        await Assert.That(Describe(diff)).IsEqualTo("~spec.replicas:3→3");
        await Assert.That(diff.Changes.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The three server counters are excluded and counted. Without this, every preview
    /// of every apply opens with <c>managedFields</c> — which the apply itself rewrites,
    /// so it changes even when nothing else does.
    /// </summary>
    [Test]
    public async Task Server_bookkeeping_is_hidden_and_counted()
    {
        var diff = Diff(
            """
            {"metadata":{"resourceVersion":"41","generation":1,
             "managedFields":[{"manager":"kubectl","operation":"Apply"}],
             "labels":{"app":"web"}}}
            """,
            """
            {"metadata":{"resourceVersion":"42","generation":2,
             "managedFields":[{"manager":"kubenimbus","operation":"Apply"}],
             "labels":{"app":"api"}}}
            """);

        await Assert.That(Describe(diff)).IsEqualTo("~metadata.labels.app:web→api");
        await Assert.That(diff.HiddenBookkeepingCount).IsEqualTo(3);
    }

    [Test]
    public async Task Bookkeeping_that_did_not_change_is_not_counted()
    {
        var diff = Diff(
            """{"metadata":{"resourceVersion":"41","name":"web"}}""",
            """{"metadata":{"resourceVersion":"41","name":"api"}}""");

        await Assert.That(diff.HiddenBookkeepingCount).IsEqualTo(0);
        await Assert.That(Describe(diff)).IsEqualTo("~metadata.name:web→api");
    }

    /// <summary>
    /// An apply that only adds a field the server did not have before still counts a
    /// hidden entry if that field is bookkeeping — the count is about what was withheld,
    /// not about what was compared.
    /// </summary>
    [Test]
    public async Task Bookkeeping_added_by_the_apply_is_counted_not_shown()
    {
        var diff = Diff("""{"metadata":{"name":"web"}}""", """{"metadata":{"name":"web","generation":1}}""");

        await Assert.That(diff.IsEmpty).IsTrue();
        await Assert.That(diff.HiddenBookkeepingCount).IsEqualTo(1);
    }

    /// <summary>
    /// The point of the merge key. Inserting one container at the front must report one
    /// addition, not "every container changed" — which is what index pairing gives and
    /// is the single loudest source of noise in a real deployment diff.
    /// </summary>
    [Test]
    public async Task A_container_inserted_at_the_front_reports_one_addition()
    {
        var diff = Diff(
            """{"spec":{"containers":[{"name":"web","image":"nginx:1.27"},{"name":"log","image":"busybox"}]}}""",
            """
            {"spec":{"containers":[
              {"name":"init-proxy","image":"envoy:1.31"},
              {"name":"web","image":"nginx:1.27"},
              {"name":"log","image":"busybox"}]}}
            """);

        await Assert.That(Describe(diff))
            .IsEqualTo("""+spec.containers[init-proxy]={"name":"init-proxy","image":"envoy:1.31"}""");
    }

    [Test]
    public async Task A_field_inside_a_named_element_is_pathed_by_name()
    {
        var diff = Diff(
            """{"spec":{"containers":[{"name":"web","image":"nginx:1.27"}]}}""",
            """{"spec":{"containers":[{"name":"web","image":"nginx:1.29"}]}}""");

        await Assert.That(Describe(diff)).IsEqualTo("~spec.containers[web].image:nginx:1.27→nginx:1.29");
    }

    [Test]
    public async Task A_removed_named_element_is_reported_by_name()
    {
        var diff = Diff(
            """{"spec":{"containers":[{"name":"web"},{"name":"log"}]}}""",
            """{"spec":{"containers":[{"name":"web"}]}}""");

        await Assert.That(Describe(diff)).IsEqualTo("""-spec.containers[log]={"name":"log"}""");
    }

    /// <summary>
    /// Reordering changes no element, so without this line it is invisible — and for
    /// <c>env</c> it is semantic, since a later entry can expand an earlier one.
    /// </summary>
    [Test]
    public async Task Reordering_named_elements_is_reported_as_one_line()
    {
        var diff = Diff(
            """{"env":[{"name":"A","value":"1"},{"name":"B","value":"2"}]}""",
            """{"env":[{"name":"B","value":"2"},{"name":"A","value":"1"}]}""");

        await Assert.That(Describe(diff)).IsEqualTo("~env (order):A, B→B, A");
    }

    /// <summary>
    /// Two entries sharing a name cannot be paired by it — the pairing would be a guess,
    /// and a wrong guess invents changes. Index comparison is the honest fallback.
    /// </summary>
    [Test]
    public async Task A_list_with_duplicate_names_falls_back_to_index_pairing()
    {
        var diff = Diff(
            """{"ports":[{"name":"http","port":80},{"name":"http","port":8080}]}""",
            """{"ports":[{"name":"http","port":80},{"name":"http","port":9090}]}""");

        await Assert.That(Describe(diff)).IsEqualTo("~ports[1].port:8080→9090");
    }

    [Test]
    public async Task A_list_of_scalars_is_compared_by_index()
    {
        var diff = Diff("""{"args":["--v=2","--leader-elect"]}""", """{"args":["--v=4","--leader-elect","--extra"]}""");

        await Assert.That(Describe(diff)).IsEqualTo("~args[0]:--v=2→--v=4 | +args[2]=--extra");
    }

    [Test]
    public async Task A_list_of_unnamed_objects_is_compared_by_index()
    {
        var diff = Diff(
            """{"rules":[{"verbs":["get"]}]}""",
            """{"rules":[{"verbs":["get","list"]}]}""");

        await Assert.That(Describe(diff)).IsEqualTo("+rules[0].verbs[1]=list");
    }

    /// <summary>
    /// Every <c>app.kubernetes.io/*</c> label contains a dot, so an unquoted path would
    /// be ambiguous about where the key ends and a nested field begins.
    /// </summary>
    [Test]
    public async Task A_key_containing_a_dot_is_quoted_in_the_path()
    {
        var diff = Diff(
            """{"metadata":{"labels":{"app.kubernetes.io/name":"web"}}}""",
            """{"metadata":{"labels":{"app.kubernetes.io/name":"api"}}}""");

        await Assert.That(Describe(diff)).IsEqualTo("~metadata.labels['app.kubernetes.io/name']:web→api");
    }

    /// <summary>
    /// A missing object is not an error and not one giant entry: the apply would create
    /// it, and what is worth reading is its top-level shape.
    /// </summary>
    [Test]
    public async Task A_creation_diffs_against_an_empty_object()
    {
        var diff = ResourceDiff.Between(null, Json("""{"apiVersion":"v1","kind":"ConfigMap","data":{"a":"1"}}"""));

        await Assert.That(diff.IsCreate).IsTrue();
        await Assert.That(Describe(diff))
            .IsEqualTo("""+apiVersion=v1 | +kind=ConfigMap | +data={"a":"1"}""");
    }

    /// <summary>
    /// A whole subtree that appears at once renders as compact JSON, capped — the row is
    /// one line in a ~300px dock, and an added container spec would otherwise push every
    /// other row off it.
    /// </summary>
    [Test]
    public async Task A_long_added_value_is_elided()
    {
        var big = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"k{i}\":\"v{i}\""));
        var diff = Diff("""{"metadata":{}}""", "{\"metadata\":{\"annotations\":{" + big + "}}}");

        var value = diff.Changes.Single().After!;
        await Assert.That(value.Length).IsLessThanOrEqualTo(241);
        await Assert.That(value.EndsWith('…')).IsTrue();
        await Assert.That(value.StartsWith("""{"k0":"v0",""")).IsTrue();
    }

    /// <summary>
    /// Pretty-printed JSON must render on one line — the API server's own responses are
    /// compact, but nothing in the type system says so.
    /// </summary>
    [Test]
    public async Task Whitespace_inside_a_rendered_value_is_collapsed()
    {
        var diff = Diff("""{"metadata":{}}""", "{\"metadata\":{\"annotations\":{\n   \"a\":   \"1 2\"\n }}}");

        await Assert.That(diff.Changes.Single().After).IsEqualTo("""{ "a": "1 2" }""");
    }

    /// <summary>
    /// A diff long enough to stop being read line by line is capped, and says so rather
    /// than silently showing a prefix.
    /// </summary>
    [Test]
    public async Task A_very_large_diff_is_capped_and_reports_the_total()
    {
        var before = "{" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"k{i}\":\"a\"")) + "}";
        var after = "{" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"k{i}\":\"b\"")) + "}";

        var diff = Diff(before, after);

        await Assert.That(diff.Changes.Count).IsEqualTo(ResourceDiff.MaxChanges);
        await Assert.That(diff.TotalChanges).IsEqualTo(300);
        await Assert.That(diff.IsTruncated).IsTrue();
    }

    /// <summary>
    /// The diff reads in the order the document is written, so a change under
    /// <c>metadata</c> comes before one under <c>spec</c> — a diff sorted by anything
    /// else makes the reader hunt.
    /// </summary>
    [Test]
    public async Task Changes_follow_the_new_documents_own_order()
    {
        var diff = Diff(
            """{"metadata":{"name":"web"},"spec":{"replicas":1},"status":{"phase":"Running"}}""",
            """{"metadata":{"name":"api"},"spec":{"replicas":2},"status":{"phase":"Pending"}}""");

        await Assert.That(string.Join(",", diff.Changes.Select(c => c.Path)))
            .IsEqualTo("metadata.name,spec.replicas,status.phase");
    }

    /// <summary>
    /// Null is a value, not an absence: <c>x: null</c> becoming <c>x: 5</c> is a change,
    /// and it must not read as an addition.
    /// </summary>
    [Test]
    public async Task Null_is_compared_as_a_value()
    {
        var diff = Diff("""{"spec":{"replicas":null}}""", """{"spec":{"replicas":5}}""");

        await Assert.That(Describe(diff)).IsEqualTo("~spec.replicas:null→5");
    }
}
