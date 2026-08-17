using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The CRD printer-column path: reading a CustomResourceDefinition's declared columns,
/// selecting which of them the list draws, and evaluating one object's cell for each.
///
/// <para>
/// No cluster is needed and none should be: the whole behaviour is decided by two JSON
/// documents. What makes these worth having is that every failure here is quiet — a
/// JSONPath subset that silently misses <c>[?(@.type=="Ready")]</c> does not throw, it
/// renders a blank READY column on every cert-manager, Flux, KEDA and Argo list in the
/// app, which looks exactly like a cluster whose controllers have not reconciled yet.
/// </para>
/// </summary>
public class PrinterColumnTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>cert-manager's own Certificate columns, verbatim — the canonical real-world set.</summary>
    private const string CertificateCrd = """
        {
          "apiVersion": "apiextensions.k8s.io/v1",
          "kind": "CustomResourceDefinition",
          "metadata": { "name": "certificates.cert-manager.io" },
          "spec": {
            "group": "cert-manager.io",
            "names": { "plural": "certificates", "kind": "Certificate" },
            "versions": [
              {
                "name": "v1alpha2",
                "additionalPrinterColumns": [
                  { "name": "Legacy", "type": "string", "jsonPath": ".spec.legacy" }
                ]
              },
              {
                "name": "v1",
                "additionalPrinterColumns": [
                  { "name": "Ready", "type": "string", "jsonPath": ".status.conditions[?(@.type==\"Ready\")].status" },
                  { "name": "Secret", "type": "string", "jsonPath": ".spec.secretName" },
                  { "name": "Issuer", "type": "string", "priority": 1, "description": "The issuer", "jsonPath": ".spec.issuerRef.name" },
                  { "name": "Status", "type": "string", "priority": 1, "jsonPath": ".status.conditions[?(@.type==\"Ready\")].message" },
                  { "name": "Age", "type": "date", "jsonPath": ".metadata.creationTimestamp" }
                ]
              }
            ]
          }
        }
        """;

    private const string Certificate = """
        {
          "apiVersion": "cert-manager.io/v1",
          "kind": "Certificate",
          "metadata": { "name": "checkout-tls", "namespace": "payments", "creationTimestamp": "2026-07-01T00:00:00Z" },
          "spec": { "secretName": "checkout-tls", "issuerRef": { "name": "internal-ca" } },
          "status": {
            "conditions": [
              { "type": "Issuing", "status": "True", "message": "Issuing" },
              { "type": "Ready", "status": "True", "message": "Certificate is up to date and has not expired" }
            ]
          }
        }
        """;

    // ------------------------------------------------------------------- parsing

    /// <summary>
    /// The columns belong to a <em>version</em>, not to the CRD. Reading them off the
    /// first version in the array would show v1alpha2's columns on a v1 list — plausible
    /// headers over cells that never resolve, which is worse than no columns at all.
    /// </summary>
    [Test]
    public async Task Parse_reads_the_requested_versions_columns()
    {
        var columns = PrinterColumns.Parse(Json(CertificateCrd), "v1");

        await Assert.That(string.Join(",", columns.Select(c => c.Name)))
            .IsEqualTo("Ready,Secret,Issuer,Status,Age");
        await Assert.That(PrinterColumns.Parse(Json(CertificateCrd), "v1alpha2").Single().Name).IsEqualTo("Legacy");
    }

    [Test]
    public async Task Parse_reads_type_priority_and_description()
    {
        var issuer = PrinterColumns.Parse(Json(CertificateCrd), "v1").Single(c => c.Name == "Issuer");

        await Assert.That(issuer.Type).IsEqualTo("string");
        await Assert.That(issuer.Priority).IsEqualTo(1);
        await Assert.That(issuer.Description).IsEqualTo("The issuer");
        await Assert.That(issuer.JsonPath).IsEqualTo(".spec.issuerRef.name");
    }

    /// <summary>An unset priority is 0 — kubectl's default table, not <c>-o wide</c>.</summary>
    [Test]
    public async Task Parse_defaults_priority_to_zero()
    {
        await Assert.That(PrinterColumns.Parse(Json(CertificateCrd), "v1").Single(c => c.Name == "Ready").Priority)
            .IsEqualTo(0);
    }

    /// <summary>
    /// A CRD that declares nothing, a version that isn't served, and a document that
    /// isn't a CRD at all must all come back empty rather than throwing. The list then
    /// renders exactly as it did before printer columns existed, which is the required
    /// degradation — an exception here would land on a watch tick.
    /// </summary>
    [Test]
    [Arguments("""{ "spec": { "versions": [ { "name": "v1" } ] } }""", "v1")]
    [Arguments("""{ "spec": { "versions": [ { "name": "v1", "additionalPrinterColumns": [] } ] } }""", "v1")]
    [Arguments(CertificateCrd, "v2")]
    [Arguments("""{ "kind": "Status", "message": "forbidden" }""", "v1")]
    [Arguments("[]", "v1")]
    public async Task Parse_returns_nothing_rather_than_failing(string json, string version)
    {
        await Assert.That(PrinterColumns.Parse(Json(json), version)).IsEmpty();
    }

    /// <summary>A column missing its name or its path is skipped, not thrown on.</summary>
    [Test]
    public async Task Parse_skips_malformed_columns()
    {
        var columns = PrinterColumns.Parse(Json("""
            {
              "spec": { "versions": [ { "name": "v1", "additionalPrinterColumns": [
                { "type": "string", "jsonPath": ".spec.a" },
                { "name": "NoPath", "type": "string" },
                { "name": "Good", "type": "string", "jsonPath": ".spec.b" }
              ] } ] }
            }
            """), "v1");

        await Assert.That(columns.Single().Name).IsEqualTo("Good");
    }

    // ----------------------------------------------------------------- selection

    /// <summary>
    /// Priority is kubectl's <c>-o wide</c> lever, and it is wired to the advanced view.
    /// The default list carries what the CRD author marked as always worth seeing.
    /// </summary>
    [Test]
    public async Task Visible_hides_low_priority_columns_outside_the_wide_view()
    {
        var all = PrinterColumns.Parse(Json(CertificateCrd), "v1");

        await Assert.That(string.Join(",", PrinterColumns.Visible(all, includeLowPriority: false, 10).Select(c => c.Name)))
            .IsEqualTo("Ready,Secret");
        await Assert.That(string.Join(",", PrinterColumns.Visible(all, includeLowPriority: true, 10).Select(c => c.Name)))
            .IsEqualTo("Ready,Secret,Issuer,Status");
    }

    /// <summary>
    /// A declared Age over the object's own creation timestamp is the column the list
    /// already draws — live, off its shared clock, with the exact timestamp as a tooltip.
    /// Two Age columns side by side would be the visible symptom; the invisible one is
    /// that the CRD's copy would only re-render when a watch event happened to arrive.
    /// </summary>
    [Test]
    public async Task Visible_drops_a_declared_age_over_the_creation_timestamp()
    {
        var all = PrinterColumns.Parse(Json(CertificateCrd), "v1");

        await Assert.That(PrinterColumns.Visible(all, includeLowPriority: true, 10).Any(c => c.Name == "Age"))
            .IsFalse();
    }

    /// <summary>…but an "Age" pointing somewhere else is the CRD's own column and is kept.</summary>
    [Test]
    public async Task Visible_keeps_an_age_column_that_is_not_the_creation_timestamp()
    {
        IReadOnlyList<PrinterColumn> all = [new("Age", "date", ".status.startTime")];

        await Assert.That(PrinterColumns.Visible(all, includeLowPriority: false, 10).Single().Name).IsEqualTo("Age");
    }

    /// <summary>The grid has a fixed number of printer slots; the surplus is dropped in declaration order.</summary>
    [Test]
    public async Task Visible_caps_at_the_slot_count()
    {
        IReadOnlyList<PrinterColumn> all =
            [.. Enumerable.Range(0, 14).Select(i => new PrinterColumn($"C{i}", "string", $".spec.f{i}"))];

        var visible = PrinterColumns.Visible(all, includeLowPriority: false, 10);

        await Assert.That(visible.Count).IsEqualTo(10);
        await Assert.That(visible[9].Name).IsEqualTo("C9");
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>
    /// The condition filter. This is not an exotic corner of JSONPath: it is how
    /// cert-manager, Flux, KEDA and Argo all spell their Ready column, so a subset
    /// without it would blank the single most-wanted column on the most-installed CRDs.
    /// Note it must pick the <em>matching</em> condition and not merely the first one —
    /// the fixture puts Issuing ahead of Ready for exactly that reason.
    /// </summary>
    [Test]
    public async Task Evaluate_resolves_a_condition_filter()
    {
        var columns = PrinterColumns.Parse(Json(CertificateCrd), "v1");
        var resource = Json(Certificate);

        await Assert.That(PrinterColumns.Evaluate(columns.Single(c => c.Name == "Ready"), resource)).IsEqualTo("True");
        await Assert.That(PrinterColumns.Evaluate(columns.Single(c => c.Name == "Status"), resource))
            .IsEqualTo("Certificate is up to date and has not expired");
    }

    [Test]
    public async Task Evaluate_resolves_a_nested_field()
    {
        var columns = PrinterColumns.Parse(Json(CertificateCrd), "v1");

        await Assert.That(PrinterColumns.Evaluate(columns.Single(c => c.Name == "Issuer"), Json(Certificate)))
            .IsEqualTo("internal-ca");
    }

    /// <summary>
    /// An absent field, an unresolvable path and a non-scalar value are all one outcome:
    /// an empty cell. The API server emits a null cell for each and kubectl prints
    /// nothing for it, and the object-that-has-no-status-yet case is common enough that
    /// it must never read as an error.
    /// </summary>
    [Test]
    [Arguments(".status.conditions[?(@.type==\"Ready\")].status")]
    [Arguments(".spec.missing")]
    [Arguments(".spec")]
    [Arguments(".status.conditions")]
    [Arguments("..spec.name")]
    [Arguments("")]
    public async Task Evaluate_renders_nothing_it_cannot_resolve_as_an_empty_cell(string path)
    {
        var resource = Json("""
            { "kind": "Certificate", "metadata": { "name": "x" }, "spec": { "secretName": "s" } }
            """);

        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", "string", path), resource)).IsEqualTo("");
    }

    /// <summary>
    /// JSON already distinguishes an integer from a string, so a number renders as
    /// itself. Re-formatting one would only create a way for the app to disagree with
    /// the object it is displaying.
    /// </summary>
    [Test]
    [Arguments("integer", ".spec.replicas", "3")]
    [Arguments("number", ".spec.ratio", "0.75")]
    [Arguments("boolean", ".spec.enabled", "true")]
    [Arguments("string", ".spec.name", "checkout")]
    public async Task Evaluate_renders_scalars_as_their_own_text(string type, string path, string expected)
    {
        var resource = Json("""
            { "spec": { "replicas": 3, "ratio": 0.75, "enabled": true, "name": "checkout" } }
            """);

        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", type, path), resource)).IsEqualTo(expected);
    }

    /// <summary>
    /// A <c>date</c> column is an age, because that is what the API server itself does
    /// to one before kubectl ever sees the cell
    /// (<c>tableconvertor.cellForJSONValue</c> → <c>ConvertToHumanReadableDateString</c>).
    /// Its two sentinels are kept for the same reason.
    /// </summary>
    [Test]
    public async Task Evaluate_renders_a_date_column_as_an_age()
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var resource = Json("""
            {
              "status": {
                "lastRun": "2026-07-28T12:00:00Z",
                "never": "0001-01-01T00:00:00Z",
                "broken": "not a timestamp"
              }
            }
            """);

        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("Last", "date", ".status.lastRun"), resource, now))
            .IsEqualTo("2d");
        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("Last", "date", ".status.never"), resource, now))
            .IsEqualTo("<unknown>");
        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("Last", "date", ".status.broken"), resource, now))
            .IsEqualTo("<invalid>");
    }

    /// <summary>
    /// A date column's timestamp is handed back so the list can re-render that cell off
    /// its shared age timer. Without it an "Expires" column would freeze at whatever it
    /// said when the last watch event arrived — the exact bug the list's own Age column
    /// has a timer to avoid.
    /// </summary>
    [Test]
    public async Task DateValue_reports_the_timestamp_for_date_columns_only()
    {
        var resource = Json("""{ "status": { "lastRun": "2026-07-28T12:00:00Z" } }""");

        await Assert.That(PrinterColumns.DateValue(new PrinterColumn("L", "date", ".status.lastRun"), resource))
            .IsEqualTo(new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(PrinterColumns.DateValue(new PrinterColumn("L", "string", ".status.lastRun"), resource))
            .IsNull();
        await Assert.That(PrinterColumns.DateValue(new PrinterColumn("L", "date", ".status.missing"), resource))
            .IsNull();
    }

    // ------------------------------------------------------ the JSONPath subset

    [Test]
    [Arguments(".spec.name", "checkout")]
    [Arguments("spec.name", "checkout")]
    [Arguments(".spec.ports[0].port", "8080")]
    [Arguments(".spec.ports[1].name", "metrics")]
    [Arguments(".spec.ports[*].name", "http")]
    [Arguments(".metadata.labels['app.kubernetes.io/name']", "checkout")]
    [Arguments(".metadata.labels[\"app.kubernetes.io/name\"]", "checkout")]
    [Arguments(".status.conditions[?(@.type=='Ready')].status", "True")]
    [Arguments(".status.conditions[?(@.type!=\"Ready\")].type", "Issuing")]
    [Arguments(".spec.ports[9].port", "")]
    [Arguments(".status.conditions[?(@.type==\"Nope\")].status", "")]
    [Arguments(".spec.name.deeper", "")]
    public async Task SimpleJsonPath_covers_the_shapes_crds_actually_use(string path, string expected)
    {
        var resource = Json("""
            {
              "metadata": { "labels": { "app.kubernetes.io/name": "checkout" } },
              "spec": {
                "name": "checkout",
                "ports": [ { "name": "http", "port": 8080 }, { "name": "metrics", "port": 9090 } ]
              },
              "status": {
                "conditions": [
                  { "type": "Issuing", "status": "False" },
                  { "type": "Ready", "status": "True" }
                ]
              }
            }
            """);

        await Assert.That(PrinterColumns.Evaluate(new PrinterColumn("C", "string", path), resource)).IsEqualTo(expected);
    }
}
