using System.Globalization;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Networking;

/// <summary>
/// Where a forward of one Service port goes: the pod picked, and the pod port the service
/// port maps to on it.
/// </summary>
/// <param name="ServicePort">The service port as the reader chose it ("80", or "80 (http)").</param>
public sealed record ServiceForwardTarget(string Namespace, string PodName, string? PodUid, int PodPort, string ServicePort);

/// <summary>
/// The answer to "which pod would a forward of this service port reach?": a target, or the
/// sentence that says why there is none. Exactly one of the two is set.
/// </summary>
public sealed record ServiceForwardResolution(ServiceForwardTarget? Target, string? Problem)
{
    public static ServiceForwardResolution Refused(string problem) => new(null, problem);
}

/// <summary>
/// Resolves a Service port to one pod and one pod port, the way <c>kubectl port-forward
/// svc/x</c> does on the client — the API has no port-forward endpoint for a Service, only
/// for a pod, so something has to pick the pod, and the pane says which one it picked.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the EndpointSlices, not the selector.</b> kubectl lists the pods the selector
/// matches and takes one; that can pick a pod the Service itself sends no traffic to (not
/// ready, crash-looping), and it cannot work at all for a selector-less service whose
/// endpoints someone wrote by hand. The slices are where the Service actually routes, found
/// by their <c>kubernetes.io/service-name</c> label (see <see cref="ServiceBackends"/>), so
/// a forward goes where the Service would send a request: a ready, non-terminating
/// endpoint that names a pod.
/// </para>
/// <para>
/// <b>The slice already carries the resolved port.</b> A Service's <c>targetPort</c> may
/// be a container port <em>name</em> that differs per pod; the EndpointSlice controller
/// resolves it and writes the number into the slice's <c>ports</c>, under the service
/// port's own name. So the pod port is read from the slice that lists the pod, never
/// guessed from the Service.
/// </para>
/// <para>
/// <b>Deterministic, and sticky.</b> Candidates are ordered by pod name; the previously
/// chosen pod is kept while it is still a candidate, so a re-resolution after a refused
/// connection does not hop between healthy pods for no reason.
/// </para>
/// </remarks>
public static class ServiceForwards
{
    /// <param name="service">The Service, or null when it could not be found.</param>
    /// <param name="slices">Every EndpointSlice labelled with the service's name.</param>
    /// <param name="servicePort">The service port (<c>spec.ports[].port</c>) to forward.</param>
    /// <param name="preferPod">The pod chosen last time, kept while it is still ready.</param>
    public static ServiceForwardResolution Resolve(
        DynamicResource? service,
        IEnumerable<DynamicResource> slices,
        int servicePort,
        string? preferPod = null)
    {
        ArgumentNullException.ThrowIfNull(slices);
        if (service is null)
        {
            return ServiceForwardResolution.Refused("The service no longer exists.");
        }

        var name = service.Name;
        if (ServiceBackends.ShapeOf(service) == ServiceShape.ExternalName)
        {
            return ServiceForwardResolution.Refused(
                $"{name} is an ExternalName service — a DNS name with no pod behind it, so there is nothing to forward to.");
        }

        var port = ServiceBackends.Ports(service).FirstOrDefault(p => p.Port == servicePort);
        if (port is null)
        {
            return ServiceForwardResolution.Refused(
                $"{name} has no port {servicePort.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!string.Equals(port.Protocol, "TCP", StringComparison.Ordinal))
        {
            return ServiceForwardResolution.Refused(
                $"Port {servicePort.ToString(CultureInfo.InvariantCulture)} of {name} is {port.Protocol}; a port-forward carries TCP only.");
        }

        var label = port.Name.Length > 0
            ? $"{port.Port.ToString(CultureInfo.InvariantCulture)} ({port.Name})"
            : port.Port.ToString(CultureInfo.InvariantCulture);

        // Every endpoint of a slice that carries this service port, with the pod port that
        // slice resolved it to.
        var candidates = new List<(EndpointInfo Endpoint, int PodPort)>();
        var listed = 0;
        foreach (var slice in slices)
        {
            if (SlicePort(slice, port.Name) is not { } podPort)
            {
                continue;
            }

            foreach (var endpoint in ServiceBackends.Endpoints(slice))
            {
                listed++;
                candidates.Add((endpoint, podPort));
            }
        }

        var ready = candidates.Where(c => c.Endpoint.Ready && !c.Endpoint.Terminating).ToList();
        var withPod = ready
            .Where(c => c.Endpoint.PodName is not null)
            .OrderBy(c => c.Endpoint.PodName, StringComparer.Ordinal)
            .ToList();

        if (withPod.Count == 0)
        {
            return ServiceForwardResolution.Refused((listed, ready.Count) switch
            {
                (0, _) => $"{name} has no endpoints for port {label} — no pod is serving it, so there is nothing to forward to.",
                (_, 0) => $"None of the {Count(listed, "endpoint")} of {name} is ready, so there is no pod to forward to.",
                _ => $"The ready endpoints of {name} name no pod, and a port-forward has to go to a pod.",
            });
        }

        var chosen = withPod.FirstOrDefault(c => string.Equals(c.Endpoint.PodName, preferPod, StringComparison.Ordinal));
        if (chosen.Endpoint is null)
        {
            chosen = withPod[0];
        }

        return new ServiceForwardResolution(
            new ServiceForwardTarget(
                chosen.Endpoint.PodNamespace ?? service.Namespace ?? "",
                chosen.Endpoint.PodName!,
                chosen.Endpoint.PodUid,
                chosen.PodPort,
                label),
            null);
    }

    /// <summary>
    /// The pod port a slice lists under the service port called <paramref name="portName"/>
    /// ("" for the one unnamed port a single-port service may have), TCP only. Null when the
    /// slice does not carry that port — or carries it without a number, which a slice may do
    /// while a named target port resolves on none of its pods.
    /// </summary>
    private static int? SlicePort(DynamicResource slice, string portName)
    {
        foreach (var entry in J.Arr(slice.Raw, "ports"))
        {
            var protocol = J.Str(entry, "protocol") is { Length: > 0 } p ? p : "TCP";
            if (string.Equals(J.Str(entry, "name"), portName, StringComparison.Ordinal)
                && string.Equals(protocol, "TCP", StringComparison.Ordinal))
            {
                return J.Int(entry, "port");
            }
        }

        return null;
    }

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
