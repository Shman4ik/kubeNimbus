using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The read-only half of the node surface: the conditions, taints and kubelet
/// information a node reports about itself, and the arithmetic behind "allocatable vs
/// requested" — how much of this node the scheduler has already promised away.
/// </summary>
/// <remarks>
/// <para>
/// It lives in Core, not in the App layer, for the same reason <see cref="Quantity"/>
/// and <see cref="PrinterColumns"/> do: it is pure reading and arithmetic over
/// Kubernetes objects with no UI in it, and the requested-vs-allocatable formula is
/// something that has to be <em>right</em> rather than plausible — a headroom number
/// that quietly disagrees with the scheduler is worse than no number, because it is the
/// number someone decides to drain on. <c>NodeResourcesTests</c> pins it.
/// </para>
/// </remarks>
public static class NodeResources
{
    /// <summary>
    /// The resources this app accounts for. CPU, memory and pod count are what
    /// <c>kubectl describe node</c> prints under "Allocated resources" and what people
    /// read a node's water level from; ephemeral storage is deliberately not here (it
    /// is rarely requested, so its column would be all zeroes on most clusters).
    /// </summary>
    public const string Cpu = "cpu";

    public const string Memory = "memory";

    public const string Pods = "pods";

    /// <summary>Every condition the node reports, in the order it reports them.</summary>
    public static IReadOnlyList<NodeCondition> Conditions(DynamicResource node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var result = new List<NodeCondition>();
        var status = NodeActions.Object(node.Raw, "status");
        if (!status.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new NodeCondition(
                NodeActions.Str(condition, "type"),
                NodeActions.Str(condition, "status"),
                NodeActions.Str(condition, "reason"),
                NodeActions.Str(condition, "message"),
                ParseTime(NodeActions.Str(condition, "lastTransitionTime"))));
        }

        return result;
    }

    /// <summary>
    /// Every taint on the node. A taint is half of why a pod is not on a node, and the
    /// cordon a drain applies shows up here too — the scheduler enforces
    /// <c>spec.unschedulable</c> by way of the <c>node.kubernetes.io/unschedulable</c>
    /// taint, so a cordoned node has both, and a reader who sees only one of them will
    /// wonder which is real.
    /// </summary>
    public static IReadOnlyList<NodeTaint> Taints(DynamicResource node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var result = new List<NodeTaint>();
        var spec = NodeActions.Object(node.Raw, "spec");
        if (!spec.TryGetProperty("taints", out var taints) || taints.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var taint in taints.EnumerateArray())
        {
            if (taint.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new NodeTaint(
                NodeActions.Str(taint, "key"),
                NodeActions.Str(taint, "value"),
                NodeActions.Str(taint, "effect")));
        }

        return result;
    }

    /// <summary>What the kubelet reports about the machine it runs on.</summary>
    public static NodeInfo Info(DynamicResource node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var status = NodeActions.Object(node.Raw, "status");
        var info = NodeActions.Object(status, "nodeInfo");

        string? internalIp = null;
        if (status.TryGetProperty("addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
        {
            foreach (var address in addresses.EnumerateArray())
            {
                if (NodeActions.Str(address, "type") == "InternalIP")
                {
                    internalIp = NodeActions.Str(address, "address");
                    break;
                }
            }
        }

        return new NodeInfo(
            NodeActions.Str(info, "kubeletVersion"),
            NodeActions.Str(info, "osImage"),
            NodeActions.Str(info, "kernelVersion"),
            NodeActions.Str(info, "containerRuntimeVersion"),
            NodeActions.Str(info, "architecture"),
            internalIp ?? "");
    }

    /// <summary>
    /// Allocatable vs requested vs limits for CPU, memory and pod count, summed over the
    /// pods given (which the caller has already narrowed to this node).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocatable, not capacity, is the denominator, because allocatable is what the
    /// scheduler will actually hand out — capacity includes what the kubelet and the OS
    /// have reserved, and a headroom figure computed against it overstates the room by
    /// however much <c>--system-reserved</c> and <c>--kube-reserved</c> hold back.
    /// Capacity is reported alongside so the difference is visible rather than lost.
    /// </para>
    /// <para>
    /// Terminal pods (Succeeded/Failed) are excluded, as <c>kubectl describe node</c>
    /// excludes them: they hold no resources and counting them would show a node as full
    /// of finished Jobs.
    /// </para>
    /// </remarks>
    public static NodeResourceSummary Summarize(DynamicResource node, IEnumerable<DynamicResource> podsOnNode)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(podsOnNode);

        var status = NodeActions.Object(node.Raw, "status");
        var allocatable = NodeActions.Object(status, "allocatable");
        var capacity = NodeActions.Object(status, "capacity");

        double cpuRequested = 0, cpuLimit = 0, memoryRequested = 0, memoryLimit = 0;
        var podCount = 0;

        foreach (var pod in podsOnNode)
        {
            var phase = NodeActions.Str(NodeActions.Object(pod.Raw, "status"), "phase");
            if (phase is "Succeeded" or "Failed")
            {
                continue;
            }

            podCount++;
            var spec = NodeActions.Object(pod.Raw, "spec");
            cpuRequested += EffectiveRequest(spec, Cpu, "requests");
            cpuLimit += EffectiveRequest(spec, Cpu, "limits");
            memoryRequested += EffectiveRequest(spec, Memory, "requests");
            memoryLimit += EffectiveRequest(spec, Memory, "limits");
        }

        return new NodeResourceSummary(
            new NodeResourceLine(
                Cpu,
                Quantity.Parse(QuantityString(allocatable, Cpu)),
                Quantity.Parse(QuantityString(capacity, Cpu)),
                cpuRequested,
                cpuLimit),
            new NodeResourceLine(
                Memory,
                Quantity.Parse(QuantityString(allocatable, Memory)),
                Quantity.Parse(QuantityString(capacity, Memory)),
                memoryRequested,
                memoryLimit),
            new NodeResourceLine(
                Pods,
                Quantity.Parse(QuantityString(allocatable, Pods)),
                Quantity.Parse(QuantityString(capacity, Pods)),
                podCount,
                Limit: null),
            podCount);
    }

    /// <summary>
    /// One pod's effective request (or limit) for one resource, by the scheduler's own
    /// formula: the sum over the regular containers, floored by the largest single init
    /// container — an init container runs alone and then exits, so it needs its own
    /// share while it runs but does not add to the steady state. Sidecars (init
    /// containers with <c>restartPolicy: Always</c>, Kubernetes 1.28+) run for the pod's
    /// whole life and therefore count into the sum instead. Pod-level
    /// <c>spec.overhead</c> is added on top, which is what a runtime class charges for
    /// the sandbox itself.
    /// </summary>
    /// <remarks>
    /// Getting this wrong is not visible: summing every container including init
    /// containers overstates a node running Jobs, and ignoring init containers entirely
    /// understates a node mid-startup. It is pinned by <c>NodeResourcesTests</c> in both
    /// directions.
    /// </remarks>
    public static double EffectiveRequest(DynamicResource pod, string resource, string section)
    {
        ArgumentNullException.ThrowIfNull(pod);
        return EffectiveRequest(NodeActions.Object(pod.Raw, "spec"), resource, section);
    }

    /// <inheritdoc cref="EffectiveRequest(DynamicResource, string, string)"/>
    public static double EffectiveRequest(JsonElement podSpec, string resource, string section)
    {
        double running = 0;
        foreach (var container in Containers(podSpec, "containers"))
        {
            running += ContainerAmount(container, resource, section);
        }

        double initMax = 0;
        foreach (var container in Containers(podSpec, "initContainers"))
        {
            var amount = ContainerAmount(container, resource, section);
            if (NodeActions.Str(container, "restartPolicy") == "Always")
            {
                // A native sidecar never exits, so it is part of the steady state.
                running += amount;
            }
            else if (amount > initMax)
            {
                initMax = amount;
            }
        }

        var overhead = Quantity.Parse(QuantityString(NodeActions.Object(podSpec, "overhead"), resource)) ?? 0;
        return Math.Max(running, initMax) + overhead;
    }

    private static IEnumerable<JsonElement> Containers(JsonElement podSpec, string property)
    {
        if (podSpec.ValueKind != JsonValueKind.Object
            || !podSpec.TryGetProperty(property, out var containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var container in containers.EnumerateArray())
        {
            if (container.ValueKind == JsonValueKind.Object)
            {
                yield return container;
            }
        }
    }

    private static double ContainerAmount(JsonElement container, string resource, string section) =>
        Quantity.Parse(QuantityString(
            NodeActions.Object(NodeActions.Object(container, "resources"), section), resource)) ?? 0;

    /// <summary>
    /// A quantity out of a resource map. Numbers as well as strings, because a
    /// hand-written manifest may carry <c>cpu: 2</c> and the API server echoes back
    /// what it was given for unset fields in some CRD-shaped objects.
    /// </summary>
    private static string? QuantityString(JsonElement map, string key)
    {
        if (map.ValueKind != JsonValueKind.Object || !map.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

/// <summary>One entry of a node's <c>status.conditions</c>.</summary>
public sealed record NodeCondition(
    string Type, string Status, string Reason, string Message, DateTimeOffset? LastTransition)
{
    /// <summary>
    /// Whether this condition is the bad kind. <c>Ready</c> is healthy when True and
    /// every pressure condition (<c>MemoryPressure</c>, <c>DiskPressure</c>,
    /// <c>PIDPressure</c>, <c>NetworkUnavailable</c>, and whatever a cloud provider or
    /// node-problem-detector adds) is healthy when False — so the polarity is read from
    /// the one condition Kubernetes defines as positive rather than from a list of the
    /// negative ones, which would classify an unknown condition wrongly by default.
    /// </summary>
    public bool IsProblem => Type == "Ready" ? Status != "True" : Status == "True";

    /// <summary>An <c>Unknown</c> status is neither healthy nor a stated fault — usually a lost kubelet.</summary>
    public bool IsUnknown => Status == "Unknown";
}

/// <summary>One entry of a node's <c>spec.taints</c>.</summary>
public sealed record NodeTaint(string Key, string Value, string Effect)
{
    /// <summary>The <c>key=value:Effect</c> form kubectl prints (and the <c>key:Effect</c> form when there is no value).</summary>
    public string Display => Value.Length == 0 ? $"{Key}:{Effect}" : $"{Key}={Value}:{Effect}";
}

/// <summary>What the kubelet reports about its machine.</summary>
public sealed record NodeInfo(
    string KubeletVersion,
    string OsImage,
    string KernelVersion,
    string ContainerRuntime,
    string Architecture,
    string InternalIp);

/// <summary>
/// One resource's numbers on a node: what the scheduler may hand out, what the machine
/// has, and what is already promised. All in base units (cores, bytes, pods) — the
/// display formatting is the App layer's job.
/// </summary>
public sealed record NodeResourceLine(
    string Resource, double? Allocatable, double? Capacity, double Requested, double? Limit)
{
    /// <summary>Requested as a percentage of allocatable, or null when the node did not report allocatable.</summary>
    public double? RequestedPercent =>
        Allocatable is { } a && a > 0 ? Math.Min(Requested * 100d / a, 999d) : null;

    /// <summary>Limits as a percentage of allocatable. Legitimately over 100% — limits routinely oversubscribe.</summary>
    public double? LimitPercent =>
        Limit is { } l && Allocatable is { } a && a > 0 ? Math.Min(l * 100d / a, 999d) : null;

    /// <summary>What is left unpromised, or null when allocatable is unknown. Never negative.</summary>
    public double? Free => Allocatable is { } a ? Math.Max(a - Requested, 0) : null;
}

/// <summary>Allocatable vs requested for the three resources a node is read by.</summary>
public sealed record NodeResourceSummary(
    NodeResourceLine Cpu, NodeResourceLine Memory, NodeResourceLine Pods, int PodCount);
