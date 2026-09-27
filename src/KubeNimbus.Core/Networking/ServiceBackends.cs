using System.Text.Json;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Networking;

/// <summary>
/// The three shapes a Service comes in, and they answer "where does its traffic go?" in
/// three different places. Each is its own stated state in the Service pane, because
/// each one's empty backend list means something different.
/// </summary>
public enum ServiceShape
{
    /// <summary><c>spec.selector</c> names pods; the EndpointSlice controller keeps the endpoints.</summary>
    Selector,

    /// <summary>
    /// No selector: Kubernetes picks no pods and writes no endpoints for it. Whatever
    /// EndpointSlices carry its name were written by something else — a person, an
    /// operator, a mesh — and they are the whole truth about where traffic goes.
    /// </summary>
    NoSelector,

    /// <summary>
    /// <c>type: ExternalName</c>: the cluster DNS answers with a CNAME and nothing else.
    /// There are no pods and no endpoints, and none are expected.
    /// </summary>
    ExternalName,
}

/// <summary>How the pane colours a verdict. Core's own words; the App maps them to its infoBar severities.</summary>
public enum VerdictLevel
{
    Neutral,
    Good,
    Warning,
    Problem,
}

/// <summary>A one-line answer and the sentence that explains it.</summary>
public sealed record ServiceVerdict(VerdictLevel Level, string Headline, string Detail);

/// <summary>One port as the Service declares it.</summary>
/// <param name="TargetPort">The container port traffic is sent to — a number, or a
/// container port <em>name</em>, which is resolved per pod. Defaults to the port.</param>
/// <param name="NodePort">0 when the service has none.</param>
public sealed record ServicePortInfo(
    string Name, int Port, string TargetPort, int NodePort, string Protocol, string AppProtocol)
{
    /// <summary>"80 → 8080/TCP", "443 → https/TCP (node 30443)".</summary>
    public string Display =>
        $"{Port} → {TargetPort}/{Protocol}" + (NodePort > 0 ? $" (node {NodePort})" : "");
}

/// <summary>
/// One endpoint out of one EndpointSlice: an address set, the pod behind it if the slice
/// names one, and the three conditions that decide whether it receives traffic.
/// </summary>
/// <param name="Addresses">Usually one IP; a slice of <c>addressType: FQDN</c> carries names.</param>
/// <param name="Ports">The slice's own ports ("8080/TCP", or "http" when only a name is
/// given) — the <em>resolved</em> target ports, which is how a named <c>targetPort</c>
/// becomes a number.</param>
/// <param name="Ready">Receives traffic. The API defines an unset value as true.</param>
/// <param name="Serving">Would receive traffic were it not terminating. Unset defers to
/// <paramref name="Ready"/>, as the API says.</param>
/// <param name="Terminating">Its pod is being deleted. Unset means false.</param>
public sealed record EndpointInfo(
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Ports,
    bool Ready,
    bool Serving,
    bool Terminating,
    string? PodName,
    string? PodNamespace,
    string? PodUid,
    string NodeName,
    string Zone,
    string SliceName);

/// <summary>What one backend row is doing with traffic, most useful state first.</summary>
public enum BackendState
{
    /// <summary>An endpoint marked ready: this backend receives traffic.</summary>
    Serving,

    /// <summary>Terminating, but the controller still lists it as serving — draining.</summary>
    TerminatingServing,

    /// <summary>An endpoint that is not ready — failing its readiness probe, or still starting.</summary>
    NotReady,

    /// <summary>Terminating and no longer serving.</summary>
    Terminating,

    /// <summary>
    /// The selector matches the pod but no EndpointSlice lists it: it has no IP yet, it has
    /// finished, or the controller has not caught up. It receives no traffic.
    /// </summary>
    NoEndpoint,
}

/// <summary>
/// One row of the Service pane: a pod the selector matches, joined to the endpoints that
/// carry its IP — or an endpoint that no matching pod accounts for.
/// </summary>
/// <param name="Pod">The pod object, when the selector matched one. Null for an
/// endpoint-only row: a selector-less service's hand-written endpoints, or a
/// <c>targetRef</c> to a pod the selector no longer matches.</param>
/// <param name="Name">The pod's name, or the endpoint's <c>targetRef</c> name, or its first
/// address when it names nothing.</param>
public sealed record ServiceBackend(
    DynamicResource? Pod,
    string Name,
    string? Namespace,
    string? Uid,
    IReadOnlyList<EndpointInfo> Endpoints,
    BackendState State,
    string Reason)
{
    public bool NamesPod => Pod is not null || Endpoints.Any(e => e.PodName is not null);

    public IReadOnlyList<string> Addresses => [.. Endpoints.SelectMany(e => e.Addresses).Distinct(StringComparer.Ordinal)];

    public string NodeName =>
        Endpoints.Select(e => e.NodeName).FirstOrDefault(n => n.Length > 0)
        ?? (Pod is { } pod ? J.Str(J.Obj(pod.Raw, "spec"), "nodeName") : "");
}

/// <summary>
/// "Why is traffic not reaching my pods?", read off the objects that decide it: the
/// Service, the pods its selector matches, and the EndpointSlices that actually route.
///
/// <para>
/// <b>EndpointSlices are found by their <c>kubernetes.io/service-name</c> label, not by
/// owner reference.</b> The label is the contract: it is what kube-proxy reads to program
/// a service, it is set on slices the controller writes, on slices mirrored from a legacy
/// Endpoints object, and on the ones a person or an operator writes for a selector-less
/// service — which carry no owner reference at all. Matching by owner would show a
/// selector-less service as having no endpoints while kube-proxy happily routes to them.
/// It is also a server-side <c>labelSelector</c>, so the list is exactly this service's
/// slices, not every slice in the namespace filtered here.
/// </para>
///
/// <para>
/// Pure functions over JSON, like <c>NodeResources</c>: the whole behaviour is decided by
/// the three documents, so it is tested without a cluster, and the demo cluster goes
/// through exactly this code.
/// </para>
/// </summary>
public static class ServiceBackends
{
    /// <summary>The label every EndpointSlice carries naming the Service it belongs to.</summary>
    public const string ServiceNameLabel = "kubernetes.io/service-name";

    public static ServiceShape ShapeOf(DynamicResource service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var spec = J.Obj(service.Raw, "spec");
        if (string.Equals(J.Str(spec, "type"), "ExternalName", StringComparison.Ordinal))
        {
            return ServiceShape.ExternalName;
        }

        // An empty selector is no selector: the controller skips a service whose selector
        // is nil, and a `selector: {}` is stored as nil. LabelSelector refuses an empty one
        // for its own reason (never "all pods") and the two agree here.
        return LabelSelector.ForPodsOf(service) is null ? ServiceShape.NoSelector : ServiceShape.Selector;
    }

    public static string ExternalNameOf(DynamicResource service) =>
        J.Str(J.Obj(service.Raw, "spec"), "externalName");

    /// <summary>The selector as a label query for the slices of the service called <paramref name="serviceName"/>.</summary>
    public static LabelSelector SlicesOf(string serviceName) =>
        new([new LabelRequirement(ServiceNameLabel, LabelOperator.In, [serviceName])]);

    public static IReadOnlyList<ServicePortInfo> Ports(DynamicResource service)
    {
        var ports = new List<ServicePortInfo>();
        foreach (var port in J.Arr(J.Obj(service.Raw, "spec"), "ports"))
        {
            var number = J.Int(port, "port") ?? 0;
            var target = port.TryGetProperty("targetPort", out var t)
                ? t.ValueKind switch
                {
                    JsonValueKind.Number => t.GetRawText(),
                    JsonValueKind.String => t.GetString() ?? "",
                    _ => "",
                }
                : "";

            ports.Add(new ServicePortInfo(
                J.Str(port, "name"),
                number,
                target.Length > 0 ? target : number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                J.Int(port, "nodePort") ?? 0,
                J.Str(port, "protocol") is { Length: > 0 } protocol ? protocol : "TCP",
                J.Str(port, "appProtocol")));
        }

        return ports;
    }

    /// <summary>Every endpoint one EndpointSlice lists, conditions defaulted the way the API defines them.</summary>
    public static IReadOnlyList<EndpointInfo> Endpoints(DynamicResource slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var ports = new List<string>();
        foreach (var port in J.Arr(slice.Raw, "ports"))
        {
            var protocol = J.Str(port, "protocol") is { Length: > 0 } p ? p : "TCP";
            ports.Add(J.Int(port, "port") is { } number
                ? $"{number}/{protocol}"
                : J.Str(port, "name") is { Length: > 0 } name ? name : "*");
        }

        var result = new List<EndpointInfo>();
        foreach (var endpoint in J.Arr(slice.Raw, "endpoints"))
        {
            var addresses = new List<string>();
            foreach (var address in J.Arr(endpoint, "addresses"))
            {
                if (address.ValueKind == JsonValueKind.String && address.GetString() is { Length: > 0 } text)
                {
                    addresses.Add(text);
                }
            }

            var conditions = J.Obj(endpoint, "conditions");
            var ready = Condition(conditions, "ready") ?? true;
            var serving = Condition(conditions, "serving") ?? ready;
            var terminating = Condition(conditions, "terminating") ?? false;

            var target = J.Obj(endpoint, "targetRef");
            var isPod = string.Equals(J.Str(target, "kind"), "Pod", StringComparison.Ordinal);

            result.Add(new EndpointInfo(
                addresses,
                ports,
                ready,
                serving,
                terminating,
                isPod ? NullIfEmpty(J.Str(target, "name")) : null,
                isPod ? NullIfEmpty(J.Str(target, "namespace")) ?? slice.Namespace : null,
                isPod ? NullIfEmpty(J.Str(target, "uid")) : null,
                J.Str(endpoint, "nodeName"),
                J.Str(endpoint, "zone"),
                slice.Name));
        }

        return result;
    }

    /// <summary>
    /// Joins the pods a selector matched to the endpoints that carry them. Every matched pod
    /// gets a row, with or without an endpoint — "matched but not listed" is the finding the
    /// pane exists for. Every endpoint that no matched pod accounts for gets a row of its
    /// own after them, because an endpoint routes traffic whether or not we can say whose
    /// it is.
    /// </summary>
    /// <remarks>
    /// Endpoints are matched to pods by <c>targetRef</c> name and namespace, and a pod may
    /// own several (a dual-stack service lists it once per address family, in two slices).
    /// Matched pods come first, then the unaccounted endpoints, each by name, so the list
    /// reads the same on every watch tick.
    /// </remarks>
    /// <param name="hasSelector">False for a selector-less service, whose endpoints are
    /// <em>expected</em> to belong to no matched pod and should not be described as strays.</param>
    public static IReadOnlyList<ServiceBackend> Join(
        IEnumerable<DynamicResource> pods, IEnumerable<DynamicResource> slices, bool hasSelector = true)
    {
        ArgumentNullException.ThrowIfNull(pods);
        ArgumentNullException.ThrowIfNull(slices);

        var endpoints = slices.SelectMany(Endpoints).ToList();
        var claimed = new HashSet<EndpointInfo>(ReferenceEqualityComparer.Instance);
        var rows = new List<ServiceBackend>();

        foreach (var pod in pods)
        {
            var mine = endpoints
                .Where(e => e.PodName is not null
                    && string.Equals(e.PodName, pod.Name, StringComparison.Ordinal)
                    && string.Equals(e.PodNamespace ?? pod.Namespace, pod.Namespace, StringComparison.Ordinal))
                .ToList();
            foreach (var endpoint in mine)
            {
                claimed.Add(endpoint);
            }

            var (state, reason) = mine.Count > 0 ? StateOf(mine) : WhyNotListed(pod);
            rows.Add(new ServiceBackend(pod, pod.Name, pod.Namespace, pod.Uid, mine, state, reason));
        }

        // Endpoints nobody matched, grouped by the pod they name (or by address when they
        // name none), so a dual-stack pod the selector no longer matches is still one row.
        foreach (var group in endpoints
                     .Where(e => !claimed.Contains(e))
                     .GroupBy(e => e.PodName is { } pod
                         ? $"pod:{e.PodNamespace}/{pod}"
                         : $"addr:{string.Join(",", e.Addresses)}", StringComparer.Ordinal))
        {
            var list = group.ToList();
            var first = list[0];
            var (state, reason) = StateOf(list);
            var origin = (first.PodName, hasSelector) switch
            {
                (null, _) => "Names no pod.",
                (_, true) => "Its pod is not one the selector matches — its labels changed, or this list is catching up.",
                _ => "",
            };
            rows.Add(new ServiceBackend(
                null,
                first.PodName ?? (first.Addresses.Count > 0 ? first.Addresses[0] : first.SliceName),
                first.PodNamespace,
                first.PodUid,
                list,
                state,
                string.Join(" ", new[] { origin, reason }.Where(s => s.Length > 0))));
        }

        // By name, never by state: a row that moves every time a probe flips is a row the
        // reader loses, and the state column already carries the colour.
        return [.. rows
            .OrderBy(r => r.Pod is null ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The pane's one-sentence answer, for a Service whose pods and slices have both been
    /// read. The three shapes are three different answers, and the selector shape splits
    /// again on the one finding that matters most: a selector that matches nothing.
    /// </summary>
    /// <remarks>
    /// Never called while either list is still loading — "matches no pod" said before the
    /// pod list has arrived is exactly the premature verdict UI rule 18 forbids.
    /// </remarks>
    public static ServiceVerdict Verdict(DynamicResource service, IReadOnlyList<ServiceBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(backends);

        var name = service.Name;
        var ns = service.Namespace ?? "";
        switch (ShapeOf(service))
        {
            case ServiceShape.ExternalName:
                var target = ExternalNameOf(service);
                return new ServiceVerdict(
                    VerdictLevel.Neutral,
                    target.Length > 0 ? $"ExternalName → {target}" : "ExternalName with no name to point at",
                    $"The cluster DNS answers {name}.{ns}.svc with a CNAME"
                    + (target.Length > 0 ? $" to {target}" : "")
                    + ". No pods and no endpoints are involved, so there is nothing here to be ready or not — "
                    + "if traffic fails, it fails at that name.");

            case ServiceShape.NoSelector:
                var serving = backends.Count(b => b.State == BackendState.Serving);
                return backends.Count == 0
                    ? new ServiceVerdict(
                        VerdictLevel.Problem,
                        "No selector, and no endpoints",
                        "Kubernetes does not pick this service's endpoints: without a selector they come only from "
                        + $"EndpointSlices labelled {ServiceNameLabel}={name}, and there are none. Traffic to it goes nowhere.")
                    : new ServiceVerdict(
                        serving == backends.Count ? VerdictLevel.Good : serving == 0 ? VerdictLevel.Problem : VerdictLevel.Warning,
                        $"No selector — {Count(backends.Count, "endpoint")}, {serving} serving",
                        "Kubernetes does not pick this service's endpoints: they come from EndpointSlices "
                        + $"labelled {ServiceNameLabel}={name}, written by something other than its own controller.");
        }

        var selector = LabelSelector.ForPodsOf(service)?.ToQuery() ?? "";
        var pods = backends.Where(b => b.Pod is not null).ToList();
        var strays = backends.Count - pods.Count;
        var strayNote = strays > 0
            ? $" {Count(strays, "more endpoint")} {(strays == 1 ? "belongs" : "belong")} to no matched pod."
            : "";

        if (pods.Count == 0)
        {
            return new ServiceVerdict(
                VerdictLevel.Problem,
                "The selector matches no pod",
                $"No pod in {ns} has the labels {selector}. Traffic to this service goes nowhere — compare the "
                + "selector with the labels on the pod template it is meant to reach." + strayNote);
        }

        var servingPods = pods.Count(b => b.State == BackendState.Serving);
        var notServing = pods
            .Where(b => b.State != BackendState.Serving)
            .GroupBy(b => b.State)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Words(g.Key)}");
        var breakdown = string.Join(", ", notServing);

        if (servingPods == pods.Count)
        {
            return new ServiceVerdict(
                VerdictLevel.Good,
                pods.Count == 1 ? "The one matching pod is serving" : $"All {pods.Count} matching pods are serving",
                $"Selector {selector}." + strayNote);
        }

        return servingPods == 0
            ? new ServiceVerdict(
                VerdictLevel.Problem,
                $"{Count(pods.Count, "pod")} {(pods.Count == 1 ? "matches" : "match")}, none is serving",
                $"Selector {selector}: {breakdown}. The service has nowhere to send traffic." + strayNote)
            : new ServiceVerdict(
                VerdictLevel.Warning,
                $"{servingPods} of {pods.Count} matching pods serving",
                $"Selector {selector}: {breakdown}." + strayNote);
    }

    /// <summary>A state as it reads after a count: "1 not ready", "2 with no endpoint".</summary>
    public static string Words(BackendState state) => state switch
    {
        BackendState.Serving => "serving",
        BackendState.TerminatingServing => "terminating (still serving)",
        BackendState.NotReady => "not ready",
        BackendState.Terminating => "terminating",
        _ => "with no endpoint",
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static (BackendState State, string Reason) StateOf(IReadOnlyList<EndpointInfo> endpoints)
    {
        if (endpoints.Any(e => e.Ready && !e.Terminating))
        {
            return (BackendState.Serving, "");
        }

        if (endpoints.Any(e => e.Terminating))
        {
            return endpoints.Any(e => e.Serving)
                ? (BackendState.TerminatingServing, "Being deleted; still serving the connections it has while it drains.")
                : (BackendState.Terminating, "Being deleted; no longer receives traffic.");
        }

        return (BackendState.NotReady, "Listed but not ready — its readiness probe is failing or it is still starting. No traffic is sent to it.");
    }

    /// <summary>
    /// Why a pod the selector matched is in no EndpointSlice. The controller lists a pod
    /// once it has an IP and until it finishes; a pod outside that window is the common
    /// case, and saying which side of it the pod is on is the diagnosis.
    /// </summary>
    private static (BackendState State, string Reason) WhyNotListed(DynamicResource pod)
    {
        var status = J.Obj(pod.Raw, "status");
        var phase = J.Str(status, "phase");
        if (phase is "Succeeded" or "Failed")
        {
            return (BackendState.NoEndpoint, $"Finished ({phase}); a finished pod is never an endpoint.");
        }

        if (J.Str(J.Obj(pod.Raw, "metadata"), "deletionTimestamp").Length > 0)
        {
            return (BackendState.NoEndpoint, "Being deleted.");
        }

        if (J.Str(status, "podIP").Length == 0)
        {
            return (BackendState.NoEndpoint,
                J.Str(J.Obj(pod.Raw, "spec"), "nodeName").Length == 0
                    ? "Not scheduled yet, so it has no IP to route to."
                    : "Has no IP yet — its sandbox is still being created.");
        }

        return (BackendState.NoEndpoint, "Not listed in any EndpointSlice yet — the controller may still be catching up.");
    }

    private static bool? Condition(JsonElement conditions, string name) =>
        conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
