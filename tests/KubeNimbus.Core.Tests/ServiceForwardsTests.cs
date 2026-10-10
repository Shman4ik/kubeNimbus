using System.Text.Json;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-29: which pod and which pod port a forward of one Service port reaches. Pure
/// functions over the Service and its EndpointSlices, so every shape is pinned without a
/// cluster; <c>Live/ServicePortForwardLiveTests</c> checks the same against a real one.
/// </summary>
public class ServiceForwardsTests
{
    private static DynamicResource Obj(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static DynamicResource Service(string ports, string type = "ClusterIP") => Obj(
        """{"apiVersion":"v1","kind":"Service","metadata":{"name":"shop-api","namespace":"shop"},"spec":{"type":""" + $"\"{type}\""
        + ""","selector":{"app":"shop-api"},"ports":[""" + ports + "]}}");

    private static DynamicResource Slice(string name, string ports, string endpoints) => Obj(
        """{"apiVersion":"discovery.k8s.io/v1","kind":"EndpointSlice","metadata":{"name":""" + $"\"{name}\""
        + ""","namespace":"shop","labels":{"kubernetes.io/service-name":"shop-api"}},"addressType":"IPv4","ports":["""
        + ports + "],\"endpoints\":[" + endpoints + "]}");

    private static string Endpoint(string pod, string ip, bool? ready = true, bool terminating = false)
    {
        var conditions = (ready is { } r ? $"\"ready\":{(r ? "true" : "false")}," : "") + $"\"terminating\":{(terminating ? "true" : "false")}";
        return $"{{\"addresses\":[\"{ip}\"],\"conditions\":{{{conditions}}},"
            + $"\"targetRef\":{{\"kind\":\"Pod\",\"name\":\"{pod}\",\"namespace\":\"shop\",\"uid\":\"uid-{pod}\"}}}}";
    }

    private const string HttpPort = """{"name":"http","port":80,"targetPort":"web","protocol":"TCP"}""";
    private const string SliceHttp = """{"name":"http","port":8080,"protocol":"TCP"}""";

    [Test]
    public async Task A_named_target_port_is_read_from_the_slice_and_the_first_ready_pod_by_name_is_picked()
    {
        var slices = new[]
        {
            Slice("a", SliceHttp, string.Join(",", Endpoint("shop-api-zzz", "10.0.0.3"), Endpoint("shop-api-bbb", "10.0.0.2"))),
            Slice("b", SliceHttp, Endpoint("shop-api-aaa", "10.0.0.1", ready: false)),
        };

        var resolution = ServiceForwards.Resolve(Service(HttpPort), slices, 80);

        await Assert.That(resolution.Problem).IsNull();
        await Assert.That(resolution.Target).IsEqualTo(new ServiceForwardTarget("shop", "shop-api-bbb", "uid-shop-api-bbb", 8080, "80 (http)"));
    }

    [Test]
    public async Task The_previous_pod_is_kept_while_it_is_still_ready()
    {
        var slices = new[] { Slice("a", SliceHttp, string.Join(",", Endpoint("shop-api-aaa", "10.0.0.1"), Endpoint("shop-api-bbb", "10.0.0.2"))) };

        var kept = ServiceForwards.Resolve(Service(HttpPort), slices, 80, preferPod: "shop-api-bbb");
        var gone = ServiceForwards.Resolve(Service(HttpPort), slices, 80, preferPod: "shop-api-deleted");

        await Assert.That(kept.Target!.PodName).IsEqualTo("shop-api-bbb");
        await Assert.That(gone.Target!.PodName).IsEqualTo("shop-api-aaa");
    }

    /// <summary>A terminating endpoint is not where the Service sends a new connection, so a forward does not go there either.</summary>
    [Test]
    public async Task A_terminating_endpoint_is_not_picked()
    {
        var slices = new[] { Slice("a", SliceHttp, string.Join(",", Endpoint("shop-api-aaa", "10.0.0.1", terminating: true), Endpoint("shop-api-bbb", "10.0.0.2"))) };

        var resolution = ServiceForwards.Resolve(Service(HttpPort), slices, 80);

        await Assert.That(resolution.Target!.PodName).IsEqualTo("shop-api-bbb");
    }

    /// <summary>The slice port is matched by the service port's name, so a two-port service forwards each port to its own target.</summary>
    [Test]
    public async Task Each_service_port_maps_to_its_own_slice_port()
    {
        var service = Service(HttpPort + """,{"name":"metrics","port":9100,"targetPort":9090,"protocol":"TCP"}""");
        var slices = new[] { Slice("a", SliceHttp + """,{"name":"metrics","port":9090,"protocol":"TCP"}""", Endpoint("shop-api-aaa", "10.0.0.1")) };

        await Assert.That(ServiceForwards.Resolve(service, slices, 80).Target!.PodPort).IsEqualTo(8080);
        await Assert.That(ServiceForwards.Resolve(service, slices, 9100).Target!.PodPort).IsEqualTo(9090);
        await Assert.That(ServiceForwards.Resolve(service, slices, 9100).Target!.ServicePort).IsEqualTo("9100 (metrics)");
    }

    [Test]
    public async Task An_unnamed_single_port_matches_the_unnamed_slice_port()
    {
        var service = Service("""{"port":80,"targetPort":8080}""");
        var slices = new[] { Slice("a", """{"port":8080,"protocol":"TCP"}""", Endpoint("shop-api-aaa", "10.0.0.1")) };

        var resolution = ServiceForwards.Resolve(service, slices, 80);

        await Assert.That(resolution.Target!.PodPort).IsEqualTo(8080);
        await Assert.That(resolution.Target.ServicePort).IsEqualTo("80");
    }

    [Test]
    [Arguments("deleted", "The service no longer exists.")]
    [Arguments("external", "shop-api is an ExternalName service — a DNS name with no pod behind it, so there is nothing to forward to.")]
    [Arguments("no-port", "shop-api has no port 81.")]
    [Arguments("udp", "Port 53 of shop-api is UDP; a port-forward carries TCP only.")]
    [Arguments("no-endpoints", "shop-api has no endpoints for port 80 (http) — no pod is serving it, so there is nothing to forward to.")]
    [Arguments("none-ready", "None of the 2 endpoints of shop-api is ready, so there is no pod to forward to.")]
    [Arguments("no-pod", "The ready endpoints of shop-api name no pod, and a port-forward has to go to a pod.")]
    public async Task Every_dead_end_is_its_own_sentence(string shape, string expected)
    {
        var service = Service(HttpPort + """,{"name":"dns","port":53,"protocol":"UDP"}""");
        var notReady = Slice("a", SliceHttp, string.Join(",", Endpoint("p1", "10.0.0.1", ready: false), Endpoint("p2", "10.0.0.2", ready: false)));
        var noPod = Slice("a", SliceHttp, """{"addresses":["192.168.1.9"]}""");

        var resolution = shape switch
        {
            "deleted" => ServiceForwards.Resolve(null, [], 80),
            "external" => ServiceForwards.Resolve(Service(HttpPort, type: "ExternalName"), [], 80),
            "no-port" => ServiceForwards.Resolve(service, [notReady], 81),
            "udp" => ServiceForwards.Resolve(service, [notReady], 53),
            "no-endpoints" => ServiceForwards.Resolve(service, [Slice("a", SliceHttp, "")], 80),
            "none-ready" => ServiceForwards.Resolve(service, [notReady], 80),
            _ => ServiceForwards.Resolve(service, [noPod], 80),
        };

        await Assert.That(resolution.Target).IsNull();
        await Assert.That(resolution.Problem).IsEqualTo(expected);
    }
}
