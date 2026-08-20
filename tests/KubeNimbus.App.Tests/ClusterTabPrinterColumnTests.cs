using System.Text.Json;
using KubeNimbus.App.Demo;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The list's side of the CRD printer columns: which set the grid draws, that the cells
/// follow the objects the watch delivers, and — the invariant that matters most — that
/// nothing a built-in kind shows is changed by any of it.
///
/// <para>
/// These drive the real <c>Apply</c>/<c>ApplyFleet</c> (internal for that purpose, same
/// as <see cref="ClusterTabRowFilterTests"/>) rather than a reproduction of them, and no
/// Avalonia application is started: the column set, the cell values and the
/// advanced-view switch are all plain view-model state. What the screenshot harness
/// covers instead is the half these cannot — that the grid's slot columns actually pick
/// the headers up.
/// </para>
/// </summary>
public class ClusterTabPrinterColumnTests
{
    private static readonly ResourceDescriptor CertificateDescriptor =
        new("cert-manager.io", "v1", "Certificate", "certificates", "certificate", true, ["cert"], []);

    /// <summary>cert-manager's own set: a condition filter, a plain path, two priority-1
    /// columns and a declared Age over the creation timestamp.</summary>
    private static IReadOnlyList<PrinterColumn> CertificateColumns { get; } =
    [
        new("Ready", "string", ".status.conditions[?(@.type==\"Ready\")].status"),
        new("Secret", "string", ".spec.secretName"),
        new("Issuer", "string", ".spec.issuerRef.name", Priority: 1),
        new("Age", "date", ".metadata.creationTimestamp"),
    ];

    private static DynamicResource Certificate(string name, string? ready, string secret)
    {
        var status = ready is null
            ? ""
            : $$""", "status": { "conditions": [ { "type": "Ready", "status": "{{ready}}" } ] }""";

        var json = $$"""
            {
              "apiVersion": "cert-manager.io/v1",
              "kind": "Certificate",
              "metadata": {
                "name": "{{name}}", "namespace": "payments",
                "creationTimestamp": "2026-08-01T10:00:00Z"
              },
              "spec": { "secretName": "{{secret}}", "issuerRef": { "name": "internal-ca" } }{{status}}
            }
            """;

        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static string Cells(ResourceRowViewModel row, int count) =>
        string.Join(" | ", row.PrinterCells.Take(count).Select(c => c.Text));

    private static ClusterTabViewModel TabWithCertificates()
    {
        var tab = TestObjects.Tab();
        tab.PrinterColumns = CertificateColumns;
        tab.Apply(TestObjects.Added(Certificate("checkout-tls", "True", "checkout-tls")));
        tab.Apply(TestObjects.Added(Certificate("ledger-tls", "False", "ledger-tls")));
        tab.Apply(TestObjects.Added(Certificate("reporting-tls", ready: null, "reporting-tls")));
        return tab;
    }

    // ------------------------------------------------------------ the column set

    /// <summary>
    /// Every column the CRD declares, priority-1 included, with the declared Age folded
    /// into the list's own live Age column.
    ///
    /// <para>
    /// The priority-1 half used to be withheld until the advanced view was on — this
    /// app's <c>-o wide</c>. That switch governs the sidebar and nothing else now, so a
    /// column the CRD's author declared is always drawn: a reader who cannot see the
    /// Issuer of a Certificate has no way to know a column was withheld, and the width
    /// it costs is theirs to re-cut (FEAT-66).
    /// </para>
    /// </summary>
    [Test]
    public async Task Every_declared_column_is_shown_without_the_declared_age()
    {
        var tab = TabWithCertificates();

        await Assert.That(string.Join(",", tab.VisiblePrinterColumns.Select(c => c.Name)))
            .IsEqualTo("Ready,Secret,Issuer");
    }

    /// <summary>
    /// The advanced view no longer touches the list. This is the negative half of the
    /// rule above and the one worth pinning: it is the assertion that fails if anything
    /// re-gates a content-area column on that switch.
    /// </summary>
    [Test]
    public async Task The_advanced_view_does_not_change_the_column_set()
    {
        var tab = TabWithCertificates();

        tab.IsAdvancedView = false;
        await Assert.That(string.Join(",", tab.VisiblePrinterColumns.Select(c => c.Name)))
            .IsEqualTo("Ready,Secret,Issuer");

        tab.IsAdvancedView = true;
        await Assert.That(string.Join(",", tab.VisiblePrinterColumns.Select(c => c.Name)))
            .IsEqualTo("Ready,Secret,Issuer");
    }

    // ------------------------------------------------------------------- cells

    [Test]
    public async Task Cells_come_from_the_objects_the_watch_delivered()
    {
        var tab = TabWithCertificates();

        await Assert.That(Cells(tab.Rows[0], 2)).IsEqualTo("True | checkout-tls");
        await Assert.That(Cells(tab.Rows[1], 2)).IsEqualTo("False | ledger-tls");
    }

    /// <summary>
    /// An object the controller has not reached yet resolves every status-backed cell to
    /// nothing. That has to be an empty cell rather than an error or a placeholder that
    /// reads as data — it is the most common state on a freshly-applied custom resource.
    /// </summary>
    [Test]
    public async Task A_resource_with_no_status_gets_empty_cells_not_an_error()
    {
        var tab = TabWithCertificates();

        await Assert.That(Cells(tab.Rows[2], 2)).IsEqualTo(" | reporting-tls");
    }

    /// <summary>A Modified for a row updates its cells in place, on the same row object.</summary>
    [Test]
    public async Task A_watch_update_refreshes_the_cells_of_the_existing_row()
    {
        var tab = TabWithCertificates();
        var row = tab.Rows[1];

        tab.Apply(TestObjects.Modified(Certificate("ledger-tls", "True", "ledger-tls-v2")));

        await Assert.That(tab.Rows[1]).IsSameReferenceAs(row);
        await Assert.That(Cells(row, 2)).IsEqualTo("True | ledger-tls-v2");
    }

    /// <summary>
    /// A priority-1 cell carries its value like any other, and the advanced view leaves
    /// both the cells and the row objects alone.
    /// </summary>
    [Test]
    public async Task A_low_priority_cell_carries_its_value_whatever_the_advanced_view_says()
    {
        var tab = TabWithCertificates();
        var row = tab.Rows[0];

        await Assert.That(Cells(row, 3)).IsEqualTo("True | checkout-tls | internal-ca");

        tab.IsAdvancedView = false;

        await Assert.That(tab.Rows[0]).IsSameReferenceAs(row);
        await Assert.That(Cells(row, 3)).IsEqualTo("True | checkout-tls | internal-ca");
    }

    /// <summary>
    /// Rows created before the column set lands must pick it up. On a live cluster that
    /// is the normal order: the watch's initial sync arrives while the one GET of the
    /// CustomResourceDefinition is still in flight.
    /// </summary>
    [Test]
    public async Task Rows_added_before_the_columns_arrive_still_get_their_cells()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(Certificate("checkout-tls", "True", "checkout-tls")));

        await Assert.That(Cells(tab.Rows[0], 2)).IsEqualTo(" | ");

        tab.PrinterColumns = CertificateColumns;

        await Assert.That(Cells(tab.Rows[0], 2)).IsEqualTo("True | checkout-tls");
    }

    /// <summary>
    /// Fleet mode has one set of headers because a table can only have one, so it uses
    /// the tab's own cluster's columns and evaluates every row against them whatever
    /// cluster served it. A member serving a shape those paths don't fit gets blank
    /// cells — the same outcome an absent field already has, and never a wrong value.
    /// </summary>
    [Test]
    public async Task Fleet_rows_are_evaluated_against_the_tabs_own_column_set()
    {
        var tab = TestObjects.Tab();
        tab.PrinterColumns = CertificateColumns;

        tab.ApplyFleet(new FleetResourceEvent(
            "eu-prod", TestObjects.Added(Certificate("checkout-tls", "True", "checkout-tls"))));
        tab.ApplyFleet(new FleetResourceEvent(
            "us-prod", TestObjects.Added(Certificate("checkout-tls", ready: null, "us-checkout-tls"))));

        await Assert.That(tab.Rows.Count).IsEqualTo(2);
        await Assert.That(Cells(tab.Rows[0], 2)).IsEqualTo("True | checkout-tls");
        await Assert.That(Cells(tab.Rows[1], 2)).IsEqualTo(" | us-checkout-tls");
    }

    // ------------------------------------------------- the built-ins are untouched

    /// <summary>
    /// The half of the acceptance criterion that is a negative: a built-in kind is not a
    /// CustomResourceDefinition, so it has no printer columns and nothing about its list
    /// changes. Pinned on the view model because the mechanism is exactly that
    /// <c>VisiblePrinterColumns</c> stays empty — and because a future change that made
    /// printer columns apply to built-ins would take Pods away from
    /// <c>ResourceStatusSummary</c> silently.
    /// </summary>
    [Test]
    public async Task A_built_in_kind_has_no_printer_columns_and_keeps_its_summary_columns()
    {
        var tab = TestObjects.Tab();
        tab.Apply(TestObjects.Added(TestObjects.Pod("payments", "checkout-abc")));

        await Assert.That(tab.PrinterColumns).IsEmpty();
        await Assert.That(tab.VisiblePrinterColumns).IsEmpty();
        await Assert.That(Cells(tab.Rows[0], ResourceRowViewModel.PrinterCellCount).Trim(' ', '|')).IsEqualTo("");

        // ResourceStatusSummary still owns every column a pod list shows.
        await Assert.That(tab.Rows[0].Status).IsEqualTo("Running");
        await Assert.That(tab.Rows[0].ReadyText).IsEqualTo("1/1");
        await Assert.That(ResourceStatusSummary.ShowsReady(TestObjects.PodDescriptor)).IsTrue();
        await Assert.That(ResourceStatusSummary.ShowsStatus(TestObjects.PodDescriptor)).IsTrue();
    }

    /// <summary>
    /// The demo cluster answers for its own CRD, through the same parse a live cluster's
    /// GET goes through — so "Explore demo cluster" shows a CRD list with its own columns
    /// rather than the generic Status column every CRD used to get.
    /// </summary>
    [Test]
    public async Task The_demo_dataset_supplies_printer_columns_for_its_own_crd()
    {
        var columns = DemoData.PrinterColumnsFor(CertificateDescriptor);

        await Assert.That(string.Join(",", columns.Select(c => c.Name))).IsEqualTo("Ready,Secret,Issuer,Status,Age");
        await Assert.That(DemoData.PrinterColumnsFor(TestObjects.PodDescriptor)).IsEmpty();
    }
}
