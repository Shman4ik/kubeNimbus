using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The structured half of what <c>kubectl describe pod</c> prints and this app used to
/// send people to the YAML editor for: the pod's conditions, its tolerations, its node
/// selector, its QoS and priority class, and each container's liveness/readiness/startup
/// probe configuration.
/// </summary>
/// <remarks>
/// <para>
/// It lives in Core for the same reason <see cref="NodeResources"/> does: it is pure
/// reading over one object's JSON with no UI in it, and every judgement it makes — a
/// condition's polarity, a probe's defaulted timings, the <c>key=value:Effect</c> form of
/// a toleration — is a thing that has to be <em>right</em> rather than plausible, which
/// means it has to be testable without an Avalonia application. <c>PodDetailsTests</c>
/// pins it.
/// </para>
/// <para>
/// It is deliberately not a <c>kubectl describe</c> text clone. kubectl's describe is a
/// large Go text formatter; every GUI competitor that ships this surface (Lens's
/// <c>pod-details.tsx</c>, Headlamp's <c>pod/Details.tsx</c>) renders structured fields
/// out of the object's own JSON instead, which is also the only version that is cheap
/// under NativeAOT — no new dependency, no reflection, the same <see cref="JsonElement"/>
/// reads the container specs already go through.
/// </para>
/// </remarks>
public static class PodDetails
{
    /// <summary>
    /// Pod condition types Kubernetes defines as positive — healthy when <c>True</c>.
    /// These are the four the kubelet and scheduler always set, plus the sandbox-readiness
    /// one added in 1.29.
    /// </summary>
    private static readonly string[] PositiveConditionTypes =
        ["PodScheduled", "Initialized", "ContainersReady", "Ready", "PodReadyToStartContainers"];

    /// <summary>
    /// Pod condition types where <c>True</c> is the bad news. <c>DisruptionTarget</c> is
    /// the one Kubernetes itself defines: it means the pod is about to be terminated.
    /// </summary>
    private static readonly string[] NegativeConditionTypes = ["DisruptionTarget"];

    /// <summary>Every condition the pod reports, in the order it reports them.</summary>
    public static IReadOnlyList<PodCondition> Conditions(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        return Conditions(Object(pod.Raw, "status"));
    }

    /// <inheritdoc cref="Conditions(DynamicResource)"/>
    public static IReadOnlyList<PodCondition> Conditions(JsonElement podStatus)
    {
        var result = new List<PodCondition>();
        if (podStatus.ValueKind != JsonValueKind.Object
            || !podStatus.TryGetProperty("conditions", out var conditions)
            || conditions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = Str(condition, "type");
            result.Add(new PodCondition(
                type,
                Str(condition, "status"),
                Str(condition, "reason"),
                Str(condition, "message"),
                ParseTime(Str(condition, "lastTransitionTime")),
                PolarityOf(type)));
        }

        return result;
    }

    /// <summary>
    /// Which way round a condition reads. Unlike a <em>node</em>'s conditions — where
    /// everything except <c>Ready</c> is a pressure condition and therefore healthy when
    /// False — a pod's conditions are mostly positive, and the one direction that must not
    /// be guessed is the third: a condition type this app has never heard of comes back
    /// <see cref="PodConditionPolarity.Unclassified"/> rather than being claimed healthy.
    /// A custom readiness gate is positive by construction, but an alpha condition like
    /// <c>PodResizePending</c> is not, and a False reassurance about a pod someone is
    /// debugging is the wrong way to be wrong.
    /// </summary>
    public static PodConditionPolarity PolarityOf(string conditionType) =>
        Array.IndexOf(PositiveConditionTypes, conditionType) >= 0 ? PodConditionPolarity.Positive
        : Array.IndexOf(NegativeConditionTypes, conditionType) >= 0 ? PodConditionPolarity.Negative
        : PodConditionPolarity.Unclassified;

    /// <summary>
    /// Where this pod is allowed to run and what it is worth: QoS class, priority class
    /// and priority, node selector and tolerations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The QoS class is <em>read</em>, never derived. It is a function of the containers'
    /// requests and limits and could be recomputed here, but the API server has already
    /// computed it and written it down; a locally-derived value that disagreed with
    /// <c>status.qosClass</c> would be worse than an empty cell, because the server's is
    /// the one the eviction path actually uses. An object that carries none (a
    /// hand-written manifest that never reached a server) reports an empty string, and
    /// the caller says so rather than inventing one.
    /// </para>
    /// <para>
    /// Every toleration is listed, including the two
    /// (<c>node.kubernetes.io/not-ready</c> and <c>node.kubernetes.io/unreachable</c>,
    /// both <c>NoExecute</c> for 300s) that the DefaultTolerationSeconds admission plugin
    /// adds to nearly every pod. They are the truth about the object, they are what
    /// <c>kubectl describe</c> shows, and hiding them would make a pod that genuinely
    /// declares one of them indistinguishable from one that does not.
    /// </para>
    /// </remarks>
    public static PodPlacement Placement(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var spec = Object(pod.Raw, "spec");
        var status = Object(pod.Raw, "status");

        int? priority = null;
        if (spec.TryGetProperty("priority", out var priorityValue)
            && priorityValue.ValueKind == JsonValueKind.Number
            && priorityValue.TryGetInt32(out var parsedPriority))
        {
            priority = parsedPriority;
        }

        return new PodPlacement(
            Str(status, "qosClass"),
            Str(spec, "priorityClassName"),
            priority,
            NodeSelector(spec),
            Tolerations(spec));
    }

    private static IReadOnlyList<PodNodeSelectorTerm> NodeSelector(JsonElement spec)
    {
        var result = new List<PodNodeSelectorTerm>();
        if (spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty("nodeSelector", out var selector)
            || selector.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var entry in selector.EnumerateObject())
        {
            result.Add(new PodNodeSelectorTerm(
                entry.Name,
                entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? "" : ""));
        }

        return result;
    }

    private static IReadOnlyList<PodToleration> Tolerations(JsonElement spec)
    {
        var result = new List<PodToleration>();
        if (spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty("tolerations", out var tolerations)
            || tolerations.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var toleration in tolerations.EnumerateArray())
        {
            if (toleration.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            long? seconds = null;
            if (toleration.TryGetProperty("tolerationSeconds", out var secondsValue)
                && secondsValue.ValueKind == JsonValueKind.Number
                && secondsValue.TryGetInt64(out var parsedSeconds))
            {
                seconds = parsedSeconds;
            }

            result.Add(new PodToleration(
                Str(toleration, "key"),
                Str(toleration, "operator"),
                Str(toleration, "value"),
                Str(toleration, "effect"),
                seconds));
        }

        return result;
    }

    /// <summary>
    /// One container's probes, in the order <c>kubectl describe</c> prints them —
    /// Liveness, Readiness, Startup. Looks in all three container arrays, because an init
    /// container carries a startup probe as readily as an app container does and an
    /// ephemeral one is the container someone attached to debug the others.
    /// </summary>
    public static IReadOnlyList<ContainerProbe> Probes(DynamicResource pod, string containerName)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var container = ContainerSpec(Object(pod.Raw, "spec"), containerName);
        if (container is not { } spec)
        {
            return [];
        }

        var result = new List<ContainerProbe>();
        foreach (var (property, kind) in ((string, string)[])
                 [("livenessProbe", "Liveness"), ("readinessProbe", "Readiness"), ("startupProbe", "Startup")])
        {
            if (spec.TryGetProperty(property, out var probe) && probe.ValueKind == JsonValueKind.Object)
            {
                result.Add(ReadProbe(kind, probe));
            }
        }

        return result;
    }

    /// <summary>One container's spec, across all three of a pod's container arrays.</summary>
    public static JsonElement? ContainerSpec(JsonElement podSpec, string containerName)
    {
        if (podSpec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var arrayName in (string[])["containers", "initContainers", "ephemeralContainers"])
        {
            if (!podSpec.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var container in array.EnumerateArray())
            {
                if (container.ValueKind == JsonValueKind.Object
                    && container.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() == containerName)
                {
                    return container;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The timing fields are defaulted rather than left blank when the object omits them,
    /// and the defaults are the API server's own (delay 0s, period 10s, timeout 1s,
    /// #success 1, #failure 3). A probe that came from a real cluster always carries all
    /// five, because the server defaults them on admission; one that does not has never
    /// been through a server, and printing what it <em>would</em> get is more useful than
    /// printing nothing. It is also what <c>kubectl describe</c> ends up showing for the
    /// same object.
    /// </summary>
    private static ContainerProbe ReadProbe(string kind, JsonElement probe) =>
        new(
            kind,
            ProbeHandler(probe),
            Int(probe, "initialDelaySeconds", 0),
            Int(probe, "periodSeconds", 10),
            Int(probe, "timeoutSeconds", 1),
            Int(probe, "successThreshold", 1),
            Int(probe, "failureThreshold", 3));

    /// <summary>
    /// The handler in <c>kubectl describe</c>'s own shorthand — <c>http-get
    /// http://:8080/healthz</c>, <c>exec [cat /tmp/ready]</c>, <c>tcp-socket :5432</c>,
    /// <c>grpc &lt;port&gt; &lt;service&gt;</c>. Matching kubectl's wording rather than
    /// inventing one means a probe read here and a probe read in a terminal are visibly
    /// the same probe.
    /// </summary>
    private static string ProbeHandler(JsonElement probe)
    {
        if (probe.TryGetProperty("httpGet", out var http) && http.ValueKind == JsonValueKind.Object)
        {
            var scheme = Str(http, "scheme");
            scheme = scheme.Length == 0 ? "http" : scheme.ToLowerInvariant();
            var path = Str(http, "path");
            return $"http-get {scheme}://{Str(http, "host")}:{PortText(http)}{(path.Length == 0 ? "/" : path)}";
        }

        if (probe.TryGetProperty("exec", out var exec) && exec.ValueKind == JsonValueKind.Object)
        {
            var command = new List<string>();
            if (exec.TryGetProperty("command", out var argv) && argv.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in argv.EnumerateArray())
                {
                    if (argument.ValueKind == JsonValueKind.String)
                    {
                        command.Add(argument.GetString() ?? "");
                    }
                }
            }

            return $"exec [{string.Join(' ', command)}]";
        }

        if (probe.TryGetProperty("tcpSocket", out var tcp) && tcp.ValueKind == JsonValueKind.Object)
        {
            return $"tcp-socket {Str(tcp, "host")}:{PortText(tcp)}";
        }

        if (probe.TryGetProperty("grpc", out var grpc) && grpc.ValueKind == JsonValueKind.Object)
        {
            var service = Str(grpc, "service");
            return $"grpc {PortText(grpc)}{(service.Length == 0 ? "" : $" {service}")}";
        }

        // A probe with no handler this app knows: say that, rather than render an empty
        // line that reads as a probe with no configuration.
        return "unrecognized handler";
    }

    /// <summary>
    /// A probe port is an IntOrString: <c>8080</c> or <c>"metrics"</c>. Both are printed
    /// as written — resolving a named port against the container's own port list is a
    /// second lookup that would silently disagree with the manifest whenever the name is
    /// wrong, which is exactly the case someone reading a failing probe is chasing.
    /// </summary>
    private static string PortText(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("port", out var port))
        {
            return "";
        }

        return port.ValueKind switch
        {
            JsonValueKind.String => port.GetString() ?? "",
            JsonValueKind.Number => port.GetRawText(),
            _ => "",
        };
    }

    private static int Int(JsonElement owner, string name, int fallback) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static JsonElement Object(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string Str(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

/// <summary>Which way round a pod condition reads. See <see cref="PodDetails.PolarityOf"/>.</summary>
public enum PodConditionPolarity
{
    /// <summary>Healthy when <c>True</c> — the four standard pod conditions.</summary>
    Positive,

    /// <summary>Healthy when <c>False</c> — <c>DisruptionTarget</c>.</summary>
    Negative,

    /// <summary>Not a condition type this app knows: shown, not judged.</summary>
    Unclassified,
}

/// <summary>One entry of a pod's <c>status.conditions</c>.</summary>
public sealed record PodCondition(
    string Type,
    string Status,
    string Reason,
    string Message,
    DateTimeOffset? LastTransition,
    PodConditionPolarity Polarity)
{
    /// <summary>An <c>Unknown</c> status is neither healthy nor a stated fault.</summary>
    public bool IsUnknown => Status == "Unknown";

    /// <summary>
    /// True when this condition is the bad kind, false when it is fine, and null when the
    /// condition type is one this app does not classify (or its status is
    /// <c>Unknown</c>) — three answers, because "we do not know" and "it is fine" lead
    /// somewhere different.
    /// </summary>
    public bool? IsProblem => (Polarity, Status) switch
    {
        (PodConditionPolarity.Unclassified, _) => null,
        (_, "Unknown") => null,
        (PodConditionPolarity.Positive, var status) => status != "True",
        (PodConditionPolarity.Negative, var status) => status == "True",
        _ => null,
    };
}

/// <summary>One entry of a pod's <c>spec.tolerations</c>.</summary>
public sealed record PodToleration(
    string Key, string Operator, string Value, string Effect, long? TolerationSeconds)
{
    /// <summary>
    /// The form <c>kubectl describe</c> prints: <c>key=value:Effect</c>, <c>key:Effect
    /// op=Exists</c>, or — for the empty-key toleration that matches everything —
    /// <c>op=Exists</c>. An empty effect means "every effect", which is why it is dropped
    /// from the string rather than printed as a trailing colon.
    /// </summary>
    public string Display
    {
        get
        {
            var head = (Key.Length, Value.Length) switch
            {
                (0, _) => "",
                (_, 0) => Key,
                _ => $"{Key}={Value}",
            };

            var body = (head.Length, Effect.Length) switch
            {
                (0, 0) => "",
                (0, _) => $":{Effect}",
                (_, 0) => head,
                _ => $"{head}:{Effect}",
            };

            // Exists is the operator a value-less toleration carries, and saying so is
            // what distinguishes "tolerates this key with any value" from "tolerates this
            // exact key=value pair".
            var op = Operator is "Exists" ? (body.Length == 0 ? "op=Exists" : $"{body} op=Exists") : body;
            return TolerationSeconds is { } seconds ? $"{op} for {seconds}s" : op;
        }
    }
}

/// <summary>One <c>spec.nodeSelector</c> entry.</summary>
public sealed record PodNodeSelectorTerm(string Key, string Value)
{
    public string Display => $"{Key}={Value}";
}

/// <summary>Where a pod may run and what it is worth. See <see cref="PodDetails.Placement"/>.</summary>
public sealed record PodPlacement(
    string QosClass,
    string PriorityClassName,
    int? Priority,
    IReadOnlyList<PodNodeSelectorTerm> NodeSelector,
    IReadOnlyList<PodToleration> Tolerations)
{
    /// <summary>
    /// A pod with no <c>priorityClassName</c> still has a priority (0, or whatever a
    /// global default priority class set), so the number is shown even when the name is
    /// absent — it is the half the scheduler actually compares.
    /// </summary>
    public string PriorityDisplay => (PriorityClassName.Length, Priority) switch
    {
        (0, null) => "",
        (0, { } priority) => priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
        (_, null) => PriorityClassName,
        (_, { } priority) => $"{PriorityClassName} ({priority})",
    };
}

/// <summary>
/// One container probe, as <c>kubectl describe</c> would print it: the handler, then the
/// five timings.
/// </summary>
public sealed record ContainerProbe(
    string Kind,
    string Handler,
    int InitialDelaySeconds,
    int PeriodSeconds,
    int TimeoutSeconds,
    int SuccessThreshold,
    int FailureThreshold)
{
    /// <summary>kubectl's own timing line: <c>delay=0s timeout=1s period=10s #success=1 #failure=3</c>.</summary>
    public string Timing =>
        $"delay={InitialDelaySeconds}s timeout={TimeoutSeconds}s period={PeriodSeconds}s "
        + $"#success={SuccessThreshold} #failure={FailureThreshold}";
}
