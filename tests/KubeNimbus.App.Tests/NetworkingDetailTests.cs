using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The Service, Ingress and NetworkPolicy panes, driven through the real demo tab: each is
/// opened the way a double-click opens it, so the routing is pinned along with the pane.
/// Nothing here starts a watch — the demo tab has no client, and the pane's own event entry
/// points (<c>ApplyPodEvent</c> / <c>ApplySliceEvent</c>) are what the watch would call.
/// </summary>
public class NetworkingDetailTests
{
    private static ClusterTabViewModel DemoTab(string group, string kind)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedNamespace = "payments";
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind));
        return tab;
    }

    private static T Open<T>(string group, string kind, string name) where T : InspectorTabViewModelBase
    {
        var tab = DemoTab(group, kind);
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);
        return (T)tab.SelectedInspectorTab!;
    }

    private static DynamicResource Obj(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    // ------------------------------------------------------------------- Service

    /// <summary>
    /// The headline case: two pods match, one serving and one crash-looping behind a
    /// not-ready endpoint. The join, the verdict and the row states all come from the
    /// shipped dataset through the production code.
    /// </summary>
    [Test]
    public async Task A_service_opens_on_its_backends_with_a_verdict()
    {
        var detail = Open<ServiceDetailTabViewModel>("", "Service", "checkout");

        await Assert.That(detail.IsLoading).IsFalse();
        await Assert.That(detail.VerdictHeadline).IsEqualTo("1 of 2 matching pods serving");
        await Assert.That(detail.VerdictLevel).IsEqualTo("warn");
        await Assert.That(detail.Backends.Select(b => (b.Name, b.StateText)).ToArray()).IsEquivalentTo(new[]
        {
            ("checkout-worker-5d8f7b9c4-qz9pl", "Not ready"),
            ("checkout-worker-7b4c6d8f5-h2x7d", "Serving"),
        });
        await Assert.That(detail.Backends.Single(b => b.StateText == "Serving").AddressText).IsEqualTo("10.42.2.31:8080");
    }

    [Test]
    public async Task The_three_degenerate_shapes_are_three_different_statements()
    {
        var noMatch = Open<ServiceDetailTabViewModel>("", "Service", "checkout-canary");
        await Assert.That(noMatch.VerdictHeadline).IsEqualTo("The selector matches no pod");
        await Assert.That(noMatch.VerdictLevel).IsEqualTo("error");
        await Assert.That(noMatch.HasBackends).IsFalse();

        var noSelector = Open<ServiceDetailTabViewModel>("", "Service", "legacy-billing");
        await Assert.That(noSelector.VerdictHeadline).IsEqualTo("No selector — 2 endpoints, 1 serving");
        await Assert.That(noSelector.Backends.Count).IsEqualTo(2);

        var external = Open<ServiceDetailTabViewModel>("", "Service", "payments-db");
        await Assert.That(external.VerdictHeadline).StartsWith("ExternalName → payments-db.");
        await Assert.That(external.VerdictLevel).IsEqualTo("");
        await Assert.That(external.HasBackends).IsFalse();
    }

    /// <summary>
    /// UI rule 18 on this pane: a (re)list that has started is not one that has finished. A
    /// Reset of the pod watch puts the pane back to "reading", and "matches no pod" is said
    /// only after Synced — never on the empty store between the two.
    /// </summary>
    [Test]
    public async Task No_verdict_is_given_between_a_reset_and_its_sync()
    {
        var detail = Open<ServiceDetailTabViewModel>("", "Service", "checkout");

        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Reset);

        await Assert.That(detail.IsLoading).IsTrue();
        await Assert.That(detail.VerdictHeadline).DoesNotContain("matches no pod");
        await Assert.That(detail.VerdictLevel).IsEqualTo("");

        detail.ApplyPodEvent(ResourceEvent<DynamicResource>.Synced);

        await Assert.That(detail.IsLoading).IsFalse();
        await Assert.That(detail.VerdictHeadline).IsEqualTo("The selector matches no pod");
    }

    /// <summary>
    /// A watch frame that changes one backend updates that row in place, and the selected row
    /// stays selected — a live list that dropped the selection on every probe flip could not
    /// be read.
    /// </summary>
    [Test]
    public async Task A_watch_tick_keeps_the_rows_and_the_selection()
    {
        var detail = Open<ServiceDetailTabViewModel>("", "Service", "checkout");
        detail.SelectedBackend = detail.Backends.Single(b => b.StateText == "Serving");
        var first = detail.Backends[0];

        detail.ApplySliceEvent(new ResourceEvent<DynamicResource>(ResourceEventType.Modified, Obj("""
            { "apiVersion": "discovery.k8s.io/v1", "kind": "EndpointSlice", "addressType": "IPv4",
              "metadata": { "name": "checkout-7kx2p", "namespace": "payments", "labels": { "kubernetes.io/service-name": "checkout" } },
              "endpoints": [
                { "addresses": [ "10.42.1.18" ], "conditions": { "ready": false },
                  "targetRef": { "kind": "Pod", "name": "checkout-worker-5d8f7b9c4-qz9pl", "namespace": "payments" } },
                { "addresses": [ "10.42.2.31" ], "conditions": { "ready": false, "serving": true, "terminating": true },
                  "targetRef": { "kind": "Pod", "name": "checkout-worker-7b4c6d8f5-h2x7d", "namespace": "payments" } } ],
              "ports": [ { "name": "http", "port": 8080, "protocol": "TCP" } ] }
            """)));

        await Assert.That(detail.Backends.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(detail.Backends[0], first)).IsTrue();
        await Assert.That(detail.SelectedBackend?.Name).IsEqualTo("checkout-worker-7b4c6d8f5-h2x7d");
        await Assert.That(detail.SelectedBackend?.StateText).IsEqualTo("Draining");
        await Assert.That(detail.VerdictHeadline).IsEqualTo("2 pods match, none is serving");
    }

    /// <summary>An edit to the selector re-reads the pods with the new one.</summary>
    [Test]
    public async Task Editing_the_selector_rereads_the_pods()
    {
        var tab = DemoTab("", "Service");
        var row = tab.Rows.First(r => r.Name == "checkout");
        tab.SelectedRow = row;
        tab.OpenSelectedCommand.Execute(null);
        var detail = (ServiceDetailTabViewModel)tab.SelectedInspectorTab!;

        row.Update(Obj("""
            { "apiVersion": "v1", "kind": "Service",
              "metadata": { "name": "checkout", "namespace": "payments" },
              "spec": { "type": "ClusterIP", "clusterIP": "10.43.12.40", "selector": { "app": "ledger-api" },
                        "ports": [ { "port": 80, "targetPort": 8080 } ] } }
            """));

        await Assert.That(detail.Backends.Where(b => b.Backend.Pod is not null).Select(b => b.Name).ToArray())
            .IsEquivalentTo(new[] { "ledger-api-6d5c8b7f9-m2v7q", "ledger-api-6d5c8b7f9-x4k9p" });
    }

    /// <summary>
    /// Double-click routes by group and kind: a core Service opens the pane, and so do Ingress
    /// and NetworkPolicy; reopening the same object selects the pane already open.
    /// </summary>
    [Test]
    public async Task Double_click_opens_the_networking_panes_and_reuses_them()
    {
        var tab = DemoTab("", "Service");
        tab.SelectedRow = tab.Rows.First(r => r.Name == "checkout");
        tab.OpenSelectedCommand.Execute(null);
        var first = tab.SelectedInspectorTab;
        tab.OpenSelectedCommand.Execute(null);

        await Assert.That(first).IsTypeOf<ServiceDetailTabViewModel>();
        await Assert.That(tab.InspectorTabs.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(tab.SelectedInspectorTab, first)).IsTrue();

        await Assert.That(Open<InspectorTabViewModelBase>("networking.k8s.io", "Ingress", "payments-public"))
            .IsTypeOf<IngressDetailTabViewModel>();
        await Assert.That(Open<InspectorTabViewModelBase>("networking.k8s.io", "NetworkPolicy", "checkout-worker"))
            .IsTypeOf<NetworkPolicyDetailTabViewModel>();
    }

    // ------------------------------------------------------------------- Ingress

    [Test]
    public async Task An_ingress_lists_its_routes_with_tls_and_urls()
    {
        var detail = Open<IngressDetailTabViewModel>("networking.k8s.io", "Ingress", "payments-public");

        await Assert.That(detail.Routes.Count).IsEqualTo(4);
        var ledger = detail.Routes.Single(r => r.PathText == "/ledger");
        await Assert.That(ledger.Url?.AbsoluteUri).IsEqualTo("https://api.payments.example.com/ledger");
        await Assert.That(ledger.TlsText).IsEqualTo("TLS · payments-public-tls");
        await Assert.That(detail.OpenUrlCommand.CanExecute(ledger)).IsTrue();

        // A wildcard host is text, and its Open / Copy are disabled rather than a link to nowhere.
        var wildcard = detail.Routes.Single(r => r.HostText.StartsWith("*.", StringComparison.Ordinal));
        await Assert.That(wildcard.Url).IsNull();
        await Assert.That(detail.OpenUrlCommand.CanExecute(wildcard)).IsFalse();
        await Assert.That(detail.CopyUrlCommand.CanExecute(wildcard)).IsFalse();
        await Assert.That(detail.SummaryText).IsEqualTo("class nginx · 203.0.113.10");
    }

    // ------------------------------------------------------------- NetworkPolicy

    /// <summary>
    /// The trap: <c>podSelector: {}</c> selects every pod in the namespace, and the Pods tab
    /// must list them — not "no selector", not nothing.
    /// </summary>
    [Test]
    public async Task A_default_deny_selects_every_pod_in_the_namespace()
    {
        var detail = Open<NetworkPolicyDetailTabViewModel>("networking.k8s.io", "NetworkPolicy", "default-deny-ingress");

        var expected = Demo.DemoData.Pods.Count(p => p.Namespace == "payments");
        await Assert.That(detail.Pods.Count).IsEqualTo(expected);
        await Assert.That(detail.SelectsText).IsEqualTo("Selects all pods in payments");
        await Assert.That(detail.IngressSummary).StartsWith("Denies all incoming traffic");
    }

    [Test]
    public async Task A_policy_lists_the_pods_its_selector_matches_and_its_rules()
    {
        var detail = Open<NetworkPolicyDetailTabViewModel>("networking.k8s.io", "NetworkPolicy", "checkout-worker");

        await Assert.That(detail.Pods.Select(p => p.Name).ToArray()).IsEquivalentTo(new[]
        {
            "checkout-worker-5d8f7b9c4-qz9pl", "checkout-worker-7b4c6d8f5-h2x7d",
        });
        await Assert.That(detail.IngressRules[0].PeersText)
            .IsEqualTo("pods with app.kubernetes.io/name=ingress-nginx in namespace ingress-nginx");
        await Assert.That(detail.EgressRules[2].PeersText).IsEqualTo("IP block 10.0.0.0/8 except 10.43.0.0/16");
        await Assert.That(detail.EgressRules[1].PortsText).IsEqualTo("UDP 53, TCP 53");
    }
}
