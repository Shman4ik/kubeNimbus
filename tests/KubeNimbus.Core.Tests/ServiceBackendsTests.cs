using System.Text.Json;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The Service pane's join: the pods a selector matches against the EndpointSlices that
/// actually route. Pure JSON in, rows out — the shapes below are copied from what the
/// sandbox's k3s 1.33 serves (see <c>kubectl get endpointslice -o json</c>), trimmed.
/// </summary>
public class ServiceBackendsTests
{
    private static DynamicResource Obj(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static DynamicResource Service(string spec) => Obj($$"""
        { "apiVersion": "v1", "kind": "Service",
          "metadata": { "name": "shop-api", "namespace": "demo-shop" },
          "spec": {{spec}} }
        """);

    private static DynamicResource Pod(string name, string phase = "Running", string ip = "10.42.0.1", string node = "n1") => Obj($$"""
        { "apiVersion": "v1", "kind": "Pod",
          "metadata": { "name": "{{name}}", "namespace": "demo-shop", "uid": "uid-{{name}}", "labels": { "app": "shop-api" } },
          "spec": { "nodeName": "{{node}}", "containers": [ { "name": "app" } ] },
          "status": { "phase": "{{phase}}", "podIP": "{{ip}}" } }
        """);

    private static DynamicResource Slice(string name, string endpoints, string ports = """[ { "name": "http", "port": 8080, "protocol": "TCP" } ]""") => Obj($$"""
        { "apiVersion": "discovery.k8s.io/v1", "kind": "EndpointSlice", "addressType": "IPv4",
          "metadata": { "name": "{{name}}", "namespace": "demo-shop",
                        "labels": { "kubernetes.io/service-name": "shop-api" } },
          "endpoints": {{endpoints}},
          "ports": {{ports}} }
        """);

    private static string Endpoint(string ip, string pod, bool? ready = true, bool? serving = null, bool? terminating = null)
    {
        static string Flag(string name, bool? value) => value is { } v ? $"\"{name}\": {(v ? "true" : "false")}," : "";
        var conditions = (Flag("ready", ready) + Flag("serving", serving) + Flag("terminating", terminating)).TrimEnd(',');
        return $$"""
            { "addresses": [ "{{ip}}" ], "conditions": { {{conditions}} }, "nodeName": "n1",
              "targetRef": { "kind": "Pod", "name": "{{pod}}", "namespace": "demo-shop", "uid": "uid-{{pod}}" } }
            """;
    }

    // ------------------------------------------------------------------ shapes

    [Test]
    public async Task The_three_shapes_are_told_apart_by_type_and_selector()
    {
        await Assert.That(ServiceBackends.ShapeOf(Service("""{ "selector": { "app": "shop-api" } }""")))
            .IsEqualTo(ServiceShape.Selector);
        await Assert.That(ServiceBackends.ShapeOf(Service("""{ "ports": [ { "port": 80 } ] }""")))
            .IsEqualTo(ServiceShape.NoSelector);
        await Assert.That(ServiceBackends.ShapeOf(Service("""{ "selector": {} }""")))
            .IsEqualTo(ServiceShape.NoSelector);

        // An ExternalName service keeps its shape even if someone left a selector on it —
        // the type decides, and the selector is ignored by the controller.
        await Assert.That(ServiceBackends.ShapeOf(Service(
                """{ "type": "ExternalName", "externalName": "db.example.com", "selector": { "app": "x" } }""")))
            .IsEqualTo(ServiceShape.ExternalName);
    }

    [Test]
    public async Task Ports_default_the_target_to_the_port_and_keep_a_named_target()
    {
        var ports = ServiceBackends.Ports(Service("""
            { "ports": [ { "name": "http", "port": 80, "targetPort": 8080 },
                         { "name": "grpc", "port": 9000, "targetPort": "grpc", "protocol": "TCP", "nodePort": 30900 },
                         { "port": 53, "protocol": "UDP" } ] }
            """));

        await Assert.That(ports.Select(p => p.Display).ToArray())
            .IsEquivalentTo(new[] { "80 → 8080/TCP", "9000 → grpc/TCP (node 30900)", "53 → 53/UDP" });
    }

    // -------------------------------------------------------------- conditions

    /// <summary>
    /// The API's own defaults for an absent condition: ready is true, serving defers to
    /// ready, terminating is false. A slice written by hand often sets none of them.
    /// </summary>
    [Test]
    public async Task Absent_conditions_take_the_api_defaults()
    {
        var endpoints = ServiceBackends.Endpoints(Slice("s", $"[ {Endpoint("10.0.0.1", "p", ready: null)} ]"));

        await Assert.That(endpoints[0].Ready).IsTrue();
        await Assert.That(endpoints[0].Serving).IsTrue();
        await Assert.That(endpoints[0].Terminating).IsFalse();
        await Assert.That(endpoints[0].Ports).IsEquivalentTo(new[] { "8080/TCP" });

        var notReady = ServiceBackends.Endpoints(Slice("s", $"[ {Endpoint("10.0.0.1", "p", ready: false)} ]"));
        await Assert.That(notReady[0].Serving).IsFalse();
    }

    // -------------------------------------------------------------------- join

    /// <summary>
    /// The finding the pane exists for, in one fixture: a serving pod, a pod listed but not
    /// ready, a pod the selector matches that no slice lists yet, and a finished pod.
    /// </summary>
    [Test]
    public async Task Every_matched_pod_gets_a_row_with_its_state()
    {
        var pods = new[]
        {
            Pod("a"), Pod("b"), Pod("c", phase: "Pending", ip: "", node: ""), Pod("d", phase: "Succeeded"),
        };
        var slice = Slice("shop-api-x", $"[ {Endpoint("10.42.0.1", "a")}, {Endpoint("10.42.0.2", "b", ready: false)} ]");

        var rows = ServiceBackends.Join(pods, [slice]);

        await Assert.That(rows.Select(r => (r.Name, r.State)).ToArray()).IsEquivalentTo(new[]
        {
            ("a", BackendState.Serving),
            ("b", BackendState.NotReady),
            ("c", BackendState.NoEndpoint),
            ("d", BackendState.NoEndpoint),
        });
        await Assert.That(rows.Single(r => r.Name == "c").Reason).Contains("Not scheduled yet");
        await Assert.That(rows.Single(r => r.Name == "d").Reason).Contains("Finished (Succeeded)");
        await Assert.That(rows.Single(r => r.Name == "a").Addresses).IsEquivalentTo(new[] { "10.42.0.1" });
    }

    /// <summary>A terminating endpoint that still serves is draining, not dead — two different states.</summary>
    [Test]
    public async Task Terminating_splits_on_serving()
    {
        var slice = Slice("s", $"[ {Endpoint("10.0.0.1", "a", ready: false, serving: true, terminating: true)}, "
                               + $"{Endpoint("10.0.0.2", "b", ready: false, serving: false, terminating: true)} ]");

        var rows = ServiceBackends.Join([Pod("a"), Pod("b")], [slice]);

        await Assert.That(rows.Single(r => r.Name == "a").State).IsEqualTo(BackendState.TerminatingServing);
        await Assert.That(rows.Single(r => r.Name == "b").State).IsEqualTo(BackendState.Terminating);
    }

    /// <summary>
    /// A dual-stack service lists a pod once per address family, in two slices. That is one
    /// backend with two addresses, not two backends.
    /// </summary>
    [Test]
    public async Task A_dual_stack_pod_is_one_row()
    {
        var v4 = Slice("s4", $"[ {Endpoint("10.42.0.1", "a")} ]");
        var v6 = Slice("s6", $"[ {Endpoint("fd00::1", "a")} ]");

        var rows = ServiceBackends.Join([Pod("a")], [v4, v6]);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Addresses).IsEquivalentTo(new[] { "10.42.0.1", "fd00::1" });
    }

    /// <summary>
    /// An endpoint no matched pod accounts for still routes traffic, so it gets its own row
    /// after the pods — whether it names a pod the selector no longer matches or names none.
    /// </summary>
    [Test]
    public async Task Endpoints_no_pod_accounts_for_are_kept_after_the_pods()
    {
        var slice = Slice("s", $$"""
            [ {{Endpoint("10.42.0.1", "a")}}, {{Endpoint("10.42.0.9", "stale")}},
              { "addresses": [ "192.168.40.12" ], "conditions": { "ready": true } } ]
            """);

        var rows = ServiceBackends.Join([Pod("a")], [slice]);

        await Assert.That(rows.Select(r => r.Name).ToArray()).IsEquivalentTo(new[] { "a", "192.168.40.12", "stale" });
        await Assert.That(rows[1].Pod).IsNull();
        await Assert.That(rows.Single(r => r.Name == "stale").Reason).Contains("not one the selector matches");
        await Assert.That(rows.Single(r => r.Name == "192.168.40.12").Reason).Contains("Names no pod");
    }

    // ---------------------------------------------------------------- verdicts

    [Test]
    public async Task A_selector_that_matches_nothing_is_its_own_verdict()
    {
        var service = Service("""{ "selector": { "app": "shop-apii" } }""");

        var verdict = ServiceBackends.Verdict(service, ServiceBackends.Join([], []));

        await Assert.That(verdict.Level).IsEqualTo(VerdictLevel.Problem);
        await Assert.That(verdict.Headline).IsEqualTo("The selector matches no pod");
        await Assert.That(verdict.Detail).Contains("app=shop-apii");
    }

    [Test]
    public async Task An_external_name_service_says_where_it_points_and_claims_nothing_about_pods()
    {
        var service = Service("""{ "type": "ExternalName", "externalName": "db.example.com" }""");

        var verdict = ServiceBackends.Verdict(service, []);

        await Assert.That(verdict.Level).IsEqualTo(VerdictLevel.Neutral);
        await Assert.That(verdict.Headline).IsEqualTo("ExternalName → db.example.com");
    }

    [Test]
    public async Task A_selectorless_service_is_judged_on_its_own_endpoints()
    {
        var service = Service("""{ "ports": [ { "port": 443 } ] }""");

        var none = ServiceBackends.Verdict(service, []);
        await Assert.That(none.Level).IsEqualTo(VerdictLevel.Problem);
        await Assert.That(none.Headline).IsEqualTo("No selector, and no endpoints");

        var slice = Slice("manual", """[ { "addresses": [ "192.168.40.12" ] } ]""");
        var some = ServiceBackends.Verdict(service, ServiceBackends.Join([], [slice], hasSelector: false));
        await Assert.That(some.Level).IsEqualTo(VerdictLevel.Good);
        await Assert.That(some.Headline).IsEqualTo("No selector — 1 endpoint, 1 serving");
    }

    [Test]
    public async Task Partial_and_total_failure_are_different_levels()
    {
        var service = Service("""{ "selector": { "app": "shop-api" } }""");
        var slice = Slice("s", $"[ {Endpoint("10.42.0.1", "a")}, {Endpoint("10.42.0.2", "b", ready: false)} ]");

        var partial = ServiceBackends.Verdict(service, ServiceBackends.Join([Pod("a"), Pod("b")], [slice]));
        await Assert.That(partial.Level).IsEqualTo(VerdictLevel.Warning);
        await Assert.That(partial.Headline).IsEqualTo("1 of 2 matching pods serving");
        await Assert.That(partial.Detail).Contains("1 not ready");

        var none = ServiceBackends.Verdict(service, ServiceBackends.Join([Pod("b")], [slice]));
        await Assert.That(none.Level).IsEqualTo(VerdictLevel.Problem);

        var all = ServiceBackends.Verdict(service, ServiceBackends.Join([Pod("a")], [Slice("s", $"[ {Endpoint("10.42.0.1", "a")} ]")]));
        await Assert.That(all.Level).IsEqualTo(VerdictLevel.Good);
        await Assert.That(all.Headline).IsEqualTo("The one matching pod is serving");
    }

    // ------------------------------------------------------------- the sandbox

    /// <summary>
    /// Against a real API server: the slices found by the <c>kubernetes.io/service-name</c>
    /// label are the service's own, and every running pod the selector matches is backed by
    /// one of their endpoints. Read-only — it uses the sandbox's demo-shop, which every
    /// sandbox has.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task Sandbox_service_backends_join_on_a_real_cluster(CancellationToken ct)
    {
        if (await SandboxCluster.TryGetContextAsync() is not { } context)
        {
            return;
        }

        using var client = await ClusterClient.ConnectAsync(context, ct);
        var serviceDescriptor = new ResourceDescriptor("", "v1", "Service", "services", "service", true, [], []);
        var service = await client.ReadResourceAsync(serviceDescriptor, "demo-shop", "shop-api", ct);
        if (service is null)
        {
            Skip.Test("The sandbox has no demo-shop/shop-api service; run scripts/sandbox-up to re-apply the manifests.");
            return;
        }

        var pods = await client.ListResourceOnceAsync(
            ResourceDescriptor.Pods, "demo-shop", cancellationToken: ct, labelSelector: LabelSelector.ForPodsOf(service));
        var slices = await client.ListResourceOnceAsync(
            ResourceDescriptor.EndpointSlices, "demo-shop", cancellationToken: ct,
            labelSelector: ServiceBackends.SlicesOf("shop-api"));

        await Assert.That(slices.Count).IsGreaterThan(0);
        await Assert.That(slices.All(s => s.Labels[ServiceBackends.ServiceNameLabel] == "shop-api")).IsTrue();

        var rows = ServiceBackends.Join(pods, slices);
        var running = rows.Where(r => r.Pod is { } pod
            && pod.Raw.GetProperty("status").GetProperty("phase").GetString() == "Running").ToList();
        await Assert.That(running.Count).IsGreaterThan(0);
        await Assert.That(running.All(r => r.Endpoints.Count > 0)).IsTrue();
    }
}
