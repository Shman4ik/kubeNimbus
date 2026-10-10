using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-12: which text identifies an object to the list's search box
/// (<see cref="ResourceRowViewModel.Matches"/>), per kind. The filter tests cover it only
/// through the filter; this pins the decision itself — above all the deliberate
/// <em>non</em>-matches, which read like omissions and are exactly what someone would
/// helpfully "fix": the status ("Running" would match most of a healthy list), a CRD's
/// printer cells ("True" would), and an Event's Type ("Normal" would).
/// </summary>
public class ResourceRowMatchesTests
{
    private static DynamicResource Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    private static ResourceRowViewModel PodRow(string clusterName = "") =>
        new(TestObjects.Pod("payments", "checkout-worker-7f9c"), clusterName);

    [Test]
    public async Task A_pod_matches_its_name_and_namespace_in_any_case()
    {
        var row = PodRow();

        await Assert.That(row.Matches("checkout")).IsTrue();
        await Assert.That(row.Matches("WORKER-7F")).IsTrue();
        await Assert.That(row.Matches("paym")).IsTrue();
        await Assert.That(row.Matches("ledger")).IsFalse();
    }

    [Test]
    public async Task A_pod_does_not_match_its_status()
    {
        var row = PodRow();
        await Assert.That(row.Status).IsEqualTo("Running");

        await Assert.That(row.Matches("Running")).IsFalse();
        await Assert.That(row.Matches("unn")).IsFalse();
    }

    /// <summary>The cluster identifies a row only where it is a column: in the fleet list.</summary>
    [Test]
    public async Task The_cluster_matches_in_fleet_mode_only()
    {
        await Assert.That(PodRow("prod-eu").Matches("prod-eu")).IsTrue();
        await Assert.That(PodRow().Matches("prod-eu")).IsFalse();
    }

    [Test]
    public async Task A_crds_printer_cells_are_not_matched()
    {
        var row = new ResourceRowViewModel(Parse("""
            {
              "apiVersion": "cert-manager.io/v1",
              "kind": "Certificate",
              "metadata": { "name": "checkout-tls", "namespace": "payments", "uid": "c1" },
              "spec": { "secretName": "vault-issued" },
              "status": { "conditions": [ { "type": "Ready", "status": "True" } ] }
            }
            """));
        row.SetPrinterColumns(
        [
            new PrinterColumn("Ready", "string", ".status.conditions[?(@.type==\"Ready\")].status"),
            new PrinterColumn("Secret", "string", ".spec.secretName"),
        ]);
        await Assert.That(row.PrinterCells[1].Text).IsEqualTo("vault-issued");

        await Assert.That(row.Matches("checkout-tls")).IsTrue();
        await Assert.That(row.Matches("vault")).IsFalse();
        await Assert.That(row.Matches("True")).IsFalse();
    }

    /// <summary>
    /// An Event's name is a generated "&lt;object&gt;.&lt;hex&gt;" nobody types, so what
    /// happened (Reason), to what (Object) and the sentence it logged (Message) identify it
    /// — the same rule, applied to what identifies an event. Type stays out.
    /// </summary>
    [Test]
    public async Task An_event_matches_its_reason_object_and_message_but_not_its_type()
    {
        var row = new ResourceRowViewModel(Parse("""
            {
              "apiVersion": "v1",
              "kind": "Event",
              "metadata": { "name": "checkout-worker-0.17e4a1b2c3d4", "namespace": "payments", "uid": "e1" },
              "involvedObject": { "apiVersion": "v1", "kind": "Pod", "name": "checkout-worker-0", "namespace": "payments" },
              "type": "Normal",
              "reason": "BackOff",
              "message": "Back-off restarting failed container app",
              "count": 3
            }
            """));

        await Assert.That(row.Matches("backoff")).IsTrue();
        await Assert.That(row.Matches("checkout-worker-0")).IsTrue();
        await Assert.That(row.Matches("restarting failed")).IsTrue();
        await Assert.That(row.Matches("Normal")).IsFalse();
    }

    /// <summary>The Event exception is the Event's alone: a pod has no Reason or Message to match.</summary>
    [Test]
    public async Task Only_an_event_matches_on_event_fields()
    {
        var pod = PodRow();

        await Assert.That(pod.Matches("BackOff")).IsFalse();
        await Assert.That(pod.EventReason).IsEqualTo("");
    }

    /// <summary>
    /// FEAT-65: an Ingress is known by its hostname, so every <c>spec.rules[].host</c>
    /// identifies it — "which Ingress serves shop.example.com?" is the question the search
    /// answers. Its status (the load balancer's address) and its class stay out.
    /// </summary>
    [Test]
    public async Task An_ingress_matches_every_host_its_rules_name_and_not_its_address_or_class()
    {
        var row = new ResourceRowViewModel(Parse("""
            {
              "apiVersion": "networking.k8s.io/v1",
              "kind": "Ingress",
              "metadata": { "name": "storefront", "namespace": "payments", "uid": "i1" },
              "spec": {
                "ingressClassName": "traefik",
                "rules": [
                  { "host": "shop.example.com", "http": { "paths": [] } },
                  { "host": "api.example.com" },
                  { "http": { "paths": [] } }
                ],
                "tls": [ { "hosts": [ "tls-only.example.com" ] } ]
              },
              "status": { "loadBalancer": { "ingress": [ { "ip": "10.43.0.7" } ] } }
            }
            """));

        await Assert.That(row.Matches("SHOP.example")).IsTrue();
        await Assert.That(row.Matches("api.example.com")).IsTrue();
        await Assert.That(row.Matches("10.43.0.7")).IsFalse();
        await Assert.That(row.Matches("traefik")).IsFalse();
        await Assert.That(row.Matches("tls-only")).IsFalse();
    }

    /// <summary>The table is per kind: another kind with a <c>spec.rules[].host</c> does not borrow the Ingress's row.</summary>
    [Test]
    public async Task Only_an_ingress_matches_on_rule_hosts()
    {
        var row = new ResourceRowViewModel(Parse("""
            {
              "apiVersion": "example.com/v1",
              "kind": "Ingress",
              "metadata": { "name": "lookalike", "namespace": "payments", "uid": "c1" },
              "spec": { "rules": [ { "host": "shop.example.com" } ] }
            }
            """));

        await Assert.That(row.Matches("shop.example.com")).IsFalse();
    }
}
