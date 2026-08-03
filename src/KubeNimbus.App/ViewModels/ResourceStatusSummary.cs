using System.Text;
using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The four words the status dot and the status pill are styled on
/// (<c>Ellipse.statusDot.*</c> / <c>Border.statusPill.*</c> in Theme.axaml).
/// Constants rather than literals because a typo here is invisible at runtime:
/// an unknown class simply matches no style, so the pill renders untinted and
/// nothing tells you the classification was wrong.
/// </summary>
public static class ResourceHealth
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Error = "error";
    public const string Idle = "idle";
}

/// <summary>
/// Everything a list row derives from one object's JSON. One record rather than
/// several independent readers because they come out of a single pass: a pod's
/// STATUS, READY and RESTARTS are literally the same walk over
/// <c>status.containerStatuses</c>, which is why kubectl computes them together
/// too (<c>pkg/printers/internalversion/printers.go</c>, <c>printPod</c>).
/// </summary>
/// <param name="Status">kubectl's STATUS column — a container reason where there
/// is one, the pod phase only as a fallback.</param>
/// <param name="Health">One of <see cref="ResourceHealth"/>.</param>
/// <param name="Ready">"2/3", or empty for kinds that have no readiness notion.</param>
/// <param name="Restarts">Total container restarts; 0 for non-pods.</param>
/// <param name="LastRestartAt">When the most recent restart happened, so the
/// Restarts cell can say "3 (43m ago)" the way kubectl does. Null when nothing
/// has restarted.</param>
/// <param name="Details">The kind-specific extra kubectl would show in place of a
/// status — a Service's type/IP/ports, a ConfigMap's key count. Empty for kinds
/// whose status column already carries the story.</param>
public sealed record ResourceSummary(
    string Status,
    string Health,
    string Ready,
    int Restarts,
    DateTimeOffset? LastRestartAt,
    string Details)
{
    /// <summary>Nothing worth showing — an empty status cell, not an error.</summary>
    public static readonly ResourceSummary None = new("", ResourceHealth.Idle, "", 0, null, "");
}

/// <summary>
/// Derives the generic list view's per-row display fields from whatever shape an
/// object actually has. Every built-in kind (and most CRDs, which follow the same
/// conventions) reports status differently, so this reads whichever of the common
/// patterns is present rather than hardcoding a table per Kind.
///
/// Pure functions over <see cref="DynamicResource"/> on purpose: the whole thing
/// is decided by the JSON in front of it, with no client, no cluster and no view
/// model involved, so it can be exercised from a fixture document alone.
/// </summary>
public static class ResourceStatusSummary
{
    public static ResourceSummary Summarize(DynamicResource resource)
    {
        var group = GroupOf(resource.ApiVersion);
        var kind = resource.Kind;
        var spec = Obj(resource.Raw, "spec");
        var status = Obj(resource.Raw, "status");
        var details = Describe(group, kind, resource.Raw, spec, status);

        // core/v1 Event: Type/Reason/Count live at the top level, not under status —
        // "Warning" events read as warn (so they visually stand out in the sidebar's
        // Events view the same way pod-detail's Events tab already colors them).
        if (group.Length == 0 && kind == "Event")
        {
            var eventReason = resource.Reason();
            if (eventReason.Length == 0)
            {
                return ResourceSummary.None;
            }

            var count = resource.Count();
            var text = count > 1 ? $"{eventReason} ×{count}" : eventReason;
            var health = string.Equals(resource.Type(), "Warning", StringComparison.OrdinalIgnoreCase)
                ? ResourceHealth.Warn
                : ResourceHealth.Ok;
            return new ResourceSummary(text, health, "", 0, null, "");
        }

        if (group.Length == 0 && kind == "Pod")
        {
            return SummarizePod(resource, spec, status);
        }

        if (group.Length == 0 && kind == "Node")
        {
            return SummarizeNode(spec, status, details);
        }

        if (group == "batch" && kind == "Job")
        {
            return SummarizeJob(spec, status, details);
        }

        // Workload controllers (Deployment/ReplicaSet/StatefulSet/DaemonSet, and any
        // CRD that copies the convention). The Status column says something the Ready
        // column doesn't — repeating "2/3" in both would waste the width.
        if (TryReplicaCounts(spec, status, out var ready, out var desired))
        {
            var (text, health) = desired switch
            {
                0 => ("Scaled to 0", ResourceHealth.Idle),
                _ when ready >= desired => ("Available", ResourceHealth.Ok),
                _ when ready == 0 => ("Unavailable", ResourceHealth.Error),
                _ => ("Degraded", ResourceHealth.Warn),
            };
            return new ResourceSummary(text, health, $"{ready}/{desired}", 0, null, details);
        }

        // status.phase: Namespace (Active/Terminating), PV/PVC (Bound/Pending/Lost),
        // and the many CRDs that copied the field.
        if (Str(status, "phase") is { Length: > 0 } phase)
        {
            return new ResourceSummary(phase, ClassifyPhase(phase), "", 0, null, details);
        }

        // Anything with a standard "conditions" array: surface the most relevant one.
        // Rendered as the word itself ("Ready" / "NotReady") rather than "Ready: True",
        // because the latter is a debug dump, not a status.
        foreach (var condition in Items(Arr(status, "conditions")))
        {
            var type = Str(condition, "type");
            if (type is not ("Ready" or "Available"))
            {
                continue;
            }

            var ok = Str(condition, "status") == "True";
            var text = ok ? type : type == "Ready" ? "NotReady" : "Unavailable";
            return new ResourceSummary(
                text, ok ? ResourceHealth.Ok : ResourceHealth.Warn, "", 0, null, details);
        }

        return ResourceSummary.None with { Details = details };
    }

    // ---------------------------------------------------------------- pods

    /// <summary>
    /// kubectl's STATUS column for a pod, reimplemented from <c>printPod</c>. The
    /// phase alone is not the status and never was: an image that won't pull is
    /// phase <c>Pending</c>, and a container in CrashLoopBackOff is phase
    /// <c>Running</c> — both of which read as "fine" if you only look at the phase.
    /// The real answer lives in the container <c>waiting</c>/<c>terminated</c>
    /// reasons, with the phase as the fallback for a pod that has neither.
    /// </summary>
    private static ResourceSummary SummarizePod(DynamicResource resource, JsonElement spec, JsonElement status)
    {
        var phase = Str(status, "phase");
        var podReason = Str(status, "reason");

        // A pod-level reason (Evicted, NodeLost, Shutdown, UnexpectedAdmissionError)
        // outranks the phase: "Failed" says nothing, "Evicted" says what happened.
        var reason = podReason.Length > 0 ? podReason : phase.Length > 0 ? phase : "Unknown";

        // A scheduling-gated pod is Pending with no container statuses at all, so the
        // gate is the only thing that explains why nothing is happening.
        foreach (var condition in Items(Arr(status, "conditions")))
        {
            if (Str(condition, "type") == "PodScheduled" && Str(condition, "reason") == "SchedulingGated")
            {
                reason = "SchedulingGated";
            }
        }

        var specContainers = Arr(spec, "containers");
        var initSpecs = Arr(spec, "initContainers");
        var containerStatuses = Arr(status, "containerStatuses");

        // Denominator comes from the spec, not from the statuses: a pod that has not
        // created its containers yet must read 0/1, not 0/0.
        var total = ArrayLength(specContainers);
        if (total == 0)
        {
            total = ArrayLength(containerStatuses);
        }

        // Sidecars (init containers with restartPolicy: Always) keep running next to
        // the app containers, so they count toward READY the way kubectl counts them.
        foreach (var container in Items(initSpecs))
        {
            if (Str(container, "restartPolicy") == "Always")
            {
                total++;
            }
        }

        var ready = 0;
        var restarts = 0;
        var sidecarRestarts = 0;
        DateTimeOffset? lastRestart = null;
        DateTimeOffset? lastSidecarRestart = null;
        var initializing = false;

        var index = 0;
        foreach (var containerStatus in Items(Arr(status, "initContainerStatuses")))
        {
            restarts += Int(containerStatus, "restartCount");
            TakeLater(ref lastRestart, LastTerminationAt(containerStatus));

            var sidecar = IsSidecar(initSpecs, Str(containerStatus, "name"));
            if (sidecar)
            {
                sidecarRestarts += Int(containerStatus, "restartCount");
                TakeLater(ref lastSidecarRestart, LastTerminationAt(containerStatus));
            }

            var state = Obj(containerStatus, "state");
            var terminated = Obj(state, "terminated");
            var waiting = Obj(state, "waiting");

            if (terminated.ValueKind == JsonValueKind.Object && Int(terminated, "exitCode") == 0)
            {
                index++;
                continue; // this init container is done; initialization moves on
            }

            if (sidecar && Flag(containerStatus, "started"))
            {
                if (Flag(containerStatus, "ready"))
                {
                    ready++;
                }

                index++;
                continue; // a started sidecar doesn't block initialization
            }

            // Anything else means initialization is stuck here, and the pod's status
            // is that container's problem, prefixed so it reads as an init failure.
            if (terminated.ValueKind == JsonValueKind.Object)
            {
                var terminatedReason = Str(terminated, "reason");
                var signal = Int(terminated, "signal");
                reason = terminatedReason.Length > 0
                    ? "Init:" + terminatedReason
                    : signal != 0
                        ? $"Init:Signal:{signal}"
                        : $"Init:ExitCode:{Int(terminated, "exitCode")}";
            }
            else if (Str(waiting, "reason") is { Length: > 0 } waitingReason && waitingReason != "PodInitializing")
            {
                reason = "Init:" + waitingReason;
            }
            else
            {
                reason = $"Init:{index}/{ArrayLength(initSpecs)}";
            }

            initializing = true;
            break;
        }

        if (!initializing || IsConditionTrue(status, "Initialized"))
        {
            // Once initialization is done, ordinary init-container restarts stop being
            // interesting (they happened before the pod ran); only sidecars keep
            // restarting alongside the app. Same reset kubectl does.
            restarts = sidecarRestarts;
            lastRestart = lastSidecarRestart;

            var hasRunning = false;

            // Backwards, so the *first* container's reason is the one left standing —
            // kubectl's order, and it keeps the reported reason stable rather than
            // flipping to whichever container was appended last.
            for (var i = ArrayLength(containerStatuses) - 1; i >= 0; i--)
            {
                var containerStatus = containerStatuses[i];
                restarts += Int(containerStatus, "restartCount");
                TakeLater(ref lastRestart, LastTerminationAt(containerStatus));

                var state = Obj(containerStatus, "state");
                var waiting = Obj(state, "waiting");
                var terminated = Obj(state, "terminated");
                var waitingReason = Str(waiting, "reason");
                var terminatedReason = Str(terminated, "reason");

                if (waitingReason.Length > 0)
                {
                    reason = waitingReason; // CrashLoopBackOff, ImagePullBackOff, ContainerCreating…
                }
                else if (terminatedReason.Length > 0)
                {
                    reason = terminatedReason; // Completed, Error, OOMKilled
                }
                else if (terminated.ValueKind == JsonValueKind.Object)
                {
                    var signal = Int(terminated, "signal");
                    reason = signal != 0 ? $"Signal:{signal}" : $"ExitCode:{Int(terminated, "exitCode")}";
                }
                else if (Flag(containerStatus, "ready") && Obj(state, "running").ValueKind == JsonValueKind.Object)
                {
                    hasRunning = true;
                    ready++;
                }
            }

            // A multi-container pod where one container exited cleanly but the rest are
            // still serving is not "Completed" — kubectl's own correction for exactly
            // the sidecar-exits case.
            if (reason == "Completed" && hasRunning)
            {
                reason = IsConditionTrue(status, "Ready") ? "Running" : "NotReady";
            }
        }

        // Being deleted outranks everything the containers say: a pod stuck in
        // Terminating is the thing you are looking for when you look at the list.
        if (Str(Obj(resource.Raw, "metadata"), "deletionTimestamp").Length > 0 && phase is not ("Succeeded" or "Failed"))
        {
            reason = podReason == "NodeLost" ? "Unknown" : "Terminating";
        }

        return new ResourceSummary(
            reason, ClassifyPod(reason, ready, total), $"{ready}/{total}", restarts, lastRestart, "");
    }

    /// <summary>
    /// Reasons that mean the workload is not doing its job. Deliberately explicit:
    /// classifying by "ready == 0" instead would call a freshly created pod broken
    /// and a Completed job pod broken too — and a finished Job pod is normal, which
    /// is why <c>Completed</c>/<c>Succeeded</c> are checked before anything else.
    /// </summary>
    private static readonly HashSet<string> ErrorReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "ErrImageNeverPull", "InvalidImageName",
        "ImageInspectError", "RegistryUnavailable", "CreateContainerConfigError", "CreateContainerError",
        "RunContainerError", "PostStartHookError", "PreStartHookError", "StartError",
        "OOMKilled", "Error", "Evicted", "DeadlineExceeded", "Failed", "NodeLost", "NodeAffinity",
        "NodeShutdown", "Shutdown", "UnexpectedAdmissionError", "ContainerStatusUnknown", "Unknown",
        "OutOfcpu", "OutOfmemory", "OutOfpods",
    };

    private static string ClassifyPod(string reason, int ready, int total)
    {
        // A finished Job pod is a success, not a failure — it must never read as error.
        if (reason is "Completed" or "Succeeded")
        {
            return ResourceHealth.Ok;
        }

        if (ErrorReasons.Contains(reason)
            || reason.StartsWith("Signal:", StringComparison.Ordinal)
            || reason.StartsWith("ExitCode:", StringComparison.Ordinal)
            || reason.StartsWith("Init:Signal:", StringComparison.Ordinal)
            || reason.StartsWith("Init:ExitCode:", StringComparison.Ordinal))
        {
            return ResourceHealth.Error;
        }

        if (reason == "Running")
        {
            // Running but not all containers ready is a real problem (a failing
            // readiness probe), but it is a degraded pod, not a dead one — and the
            // Ready column right next to it already says 0/1.
            return total > 0 && ready == total ? ResourceHealth.Ok : ResourceHealth.Warn;
        }

        // Pending, ContainerCreating, PodInitializing, Init:n/m, Terminating,
        // SchedulingGated, NotReady: in flight, not broken.
        return ResourceHealth.Warn;
    }

    // ------------------------------------------------------- other kinds

    private static ResourceSummary SummarizeNode(JsonElement spec, JsonElement status, string details)
    {
        var known = false;
        var ready = false;
        foreach (var condition in Items(Arr(status, "conditions")))
        {
            if (Str(condition, "type") != "Ready")
            {
                continue;
            }

            known = true;
            ready = Str(condition, "status") == "True";
            break;
        }

        // Cordoned nodes still report Ready; kubectl appends the cordon because a node
        // that takes no new pods is the answer to "why is nothing scheduling here".
        var cordoned = Flag(spec, "unschedulable");
        var text = (known ? ready ? "Ready" : "NotReady" : "Unknown") + (cordoned ? ",SchedulingDisabled" : "");
        var health = !known || !ready
            ? ResourceHealth.Error
            : cordoned
                ? ResourceHealth.Warn
                : ResourceHealth.Ok;

        return new ResourceSummary(text, health, "", 0, null, details);
    }

    private static ResourceSummary SummarizeJob(JsonElement spec, JsonElement status, string details)
    {
        var succeeded = Int(status, "succeeded");
        // A Job with no explicit completions finishes after one success.
        var completions = Has(spec, "completions") ? Int(spec, "completions", 1) : 1;
        var readyText = $"{succeeded}/{completions}";

        foreach (var condition in Items(Arr(status, "conditions")))
        {
            if (Str(condition, "status") != "True")
            {
                continue;
            }

            switch (Str(condition, "type"))
            {
                case "Complete":
                    return new ResourceSummary("Complete", ResourceHealth.Ok, readyText, 0, null, details);
                case "Failed":
                    return new ResourceSummary("Failed", ResourceHealth.Error, readyText, 0, null, details);
            }
        }

        var text = Int(status, "active") > 0 ? "Running" : "Pending";
        return new ResourceSummary(text, ResourceHealth.Warn, readyText, 0, null, details);
    }

    /// <summary>
    /// Ready/desired for anything replica-shaped. DaemonSets are the reason this
    /// reads three different desired fields: they carry no <c>replicas</c> at all
    /// (<c>desiredNumberScheduled</c>/<c>numberReady</c> instead), so a check for
    /// <c>status.replicas</c> alone leaves every DaemonSet with a blank status.
    /// </summary>
    private static bool TryReplicaCounts(JsonElement spec, JsonElement status, out int ready, out int desired)
    {
        ready = 0;

        if (Has(spec, "replicas"))
        {
            desired = Int(spec, "replicas"); // the intent, which is what READY compares against
        }
        else if (Has(status, "replicas"))
        {
            desired = Int(status, "replicas");
        }
        else if (Has(status, "desiredNumberScheduled"))
        {
            desired = Int(status, "desiredNumberScheduled");
        }
        else
        {
            desired = 0;
            return false;
        }

        ready = Has(status, "readyReplicas")
            ? Int(status, "readyReplicas")
            : Int(status, "numberReady");
        return true;
    }

    private static string ClassifyPhase(string phase) => phase switch
    {
        "Active" or "Bound" or "Available" or "Succeeded" or "Running" => ResourceHealth.Ok,
        "Pending" or "Terminating" or "Released" => ResourceHealth.Warn,
        "Failed" or "Lost" or "Unknown" => ResourceHealth.Error,
        // An unrecognized phase from a CRD: show the word, claim nothing about it.
        _ => ResourceHealth.Idle,
    };

    // ------------------------------------------------- the Details column

    /// <summary>
    /// What kubectl shows instead of a status for kinds that have none. Kept to the
    /// handful people actually browse — a per-kind table for all of Kubernetes would
    /// be a maintenance liability, and an unlisted kind loses nothing (it just has no
    /// Details column, exactly as before).
    /// </summary>
    private static string Describe(
        string group, string kind, JsonElement raw, JsonElement spec, JsonElement status) => (group, kind) switch
    {
        ("", "ConfigMap") => KeyCount(PropertyCount(Obj(raw, "data")) + PropertyCount(Obj(raw, "binaryData"))),
        ("", "Secret") => Join(
            Str(raw, "type"),
            KeyCount(PropertyCount(Obj(raw, "data")) + PropertyCount(Obj(raw, "stringData")))),
        ("", "Service") => DescribeService(spec, status),
        ("", "Node") => Join(NodeRoles(raw), Str(Obj(status, "nodeInfo"), "kubeletVersion")),
        ("", "PersistentVolumeClaim") => Join(
            Str(Obj(status, "capacity"), "storage"), AccessModes(spec), Str(spec, "storageClassName")),
        ("", "PersistentVolume") => Join(
            Str(Obj(spec, "capacity"), "storage"), AccessModes(spec), Str(spec, "storageClassName")),
        ("batch", "CronJob") => DescribeCronJob(spec, status),
        ("networking.k8s.io", "Ingress") => Join(IngressHosts(spec), LoadBalancerAddress(status)),
        _ => "",
    };

    private static string DescribeCronJob(JsonElement spec, JsonElement status)
    {
        var active = ArrayLength(Arr(status, "active"));
        return Join(
            Str(spec, "schedule"),
            Flag(spec, "suspend") ? "suspended" : "",
            active > 0 ? $"{active} active" : "");
    }

    private static string KeyCount(int count) => count == 1 ? "1 key" : $"{count} keys";

    private static string DescribeService(JsonElement spec, JsonElement status)
    {
        var type = Str(spec, "type") is { Length: > 0 } t ? t : "ClusterIP";
        var external = LoadBalancerAddress(status);
        if (external.Length == 0 && ArrayLength(Arr(spec, "externalIPs")) > 0)
        {
            external = Arr(spec, "externalIPs")[0].GetString() ?? "";
        }

        // A LoadBalancer with no address yet is the single most common Service
        // question there is; saying nothing would look like it had one.
        if (external.Length == 0 && type == "LoadBalancer")
        {
            external = "<pending>";
        }

        var ports = new StringBuilder();
        foreach (var port in Items(Arr(spec, "ports")))
        {
            if (ports.Length > 0)
            {
                ports.Append(',');
            }

            ports.Append(Int(port, "port"));
            var nodePort = Int(port, "nodePort");
            if (nodePort != 0)
            {
                ports.Append(':').Append(nodePort);
            }

            ports.Append('/').Append(Str(port, "protocol") is { Length: > 0 } p ? p : "TCP");
        }

        return Join(type, Str(spec, "clusterIP"), external, ports.ToString());
    }

    private static string LoadBalancerAddress(JsonElement status)
    {
        foreach (var ingress in Items(Arr(Obj(status, "loadBalancer"), "ingress")))
        {
            if (Str(ingress, "ip") is { Length: > 0 } ip)
            {
                return ip;
            }

            if (Str(ingress, "hostname") is { Length: > 0 } hostname)
            {
                return hostname;
            }
        }

        return "";
    }

    private static string IngressHosts(JsonElement spec)
    {
        var hosts = new StringBuilder();
        foreach (var rule in Items(Arr(spec, "rules")))
        {
            if (Str(rule, "host") is not { Length: > 0 } host)
            {
                continue;
            }

            if (hosts.Length > 0)
            {
                hosts.Append(',');
            }

            hosts.Append(host);
        }

        return hosts.ToString();
    }

    private static string NodeRoles(JsonElement raw)
    {
        const string prefix = "node-role.kubernetes.io/";
        var roles = new StringBuilder();
        foreach (var label in PropertiesOf(Obj(Obj(raw, "metadata"), "labels")))
        {
            if (!label.Name.StartsWith(prefix, StringComparison.Ordinal) || label.Name.Length == prefix.Length)
            {
                continue;
            }

            if (roles.Length > 0)
            {
                roles.Append(',');
            }

            roles.Append(label.Name.AsSpan(prefix.Length));
        }

        return roles.Length > 0 ? roles.ToString() : "<none>";
    }

    /// <summary>RWO/ROX/RWX/RWOP — the abbreviations kubectl prints, because the full names don't fit a cell.</summary>
    private static string AccessModes(JsonElement spec)
    {
        var modes = new StringBuilder();
        foreach (var mode in Items(Arr(spec, "accessModes")))
        {
            var text = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
            var shortName = text switch
            {
                "ReadWriteOnce" => "RWO",
                "ReadOnlyMany" => "ROX",
                "ReadWriteMany" => "RWX",
                "ReadWriteOncePod" => "RWOP",
                _ => text,
            };

            if (string.IsNullOrEmpty(shortName))
            {
                continue;
            }

            if (modes.Length > 0)
            {
                modes.Append(',');
            }

            modes.Append(shortName);
        }

        return modes.ToString();
    }

    // ------------------------------------------------- column visibility

    /// <summary>
    /// Kinds that genuinely have no status to report. Without this the list keeps a
    /// 150px Status column and a 28px dot column for every ConfigMap and Service row
    /// — permanently empty apart from a meaningless grey dot, which reads as a bug
    /// rather than as "there is nothing here". Anything not listed (every CRD
    /// included) keeps the column: guessing wrong in that direction only costs width.
    /// </summary>
    private static readonly HashSet<string> StatuslessKinds = new(StringComparer.Ordinal)
    {
        "/ConfigMap", "/Secret", "/ServiceAccount", "/Service", "/Endpoints", "/LimitRange",
        "batch/CronJob",
        "apps/ControllerRevision",
        "rbac.authorization.k8s.io/Role", "rbac.authorization.k8s.io/RoleBinding",
        "rbac.authorization.k8s.io/ClusterRole", "rbac.authorization.k8s.io/ClusterRoleBinding",
        "discovery.k8s.io/EndpointSlice",
        "coordination.k8s.io/Lease",
        "networking.k8s.io/Ingress", "networking.k8s.io/IngressClass", "networking.k8s.io/NetworkPolicy",
        "storage.k8s.io/StorageClass", "storage.k8s.io/CSIDriver",
        "scheduling.k8s.io/PriorityClass",
        "node.k8s.io/RuntimeClass",
        "admissionregistration.k8s.io/MutatingWebhookConfiguration",
        "admissionregistration.k8s.io/ValidatingWebhookConfiguration",
    };

    /// <summary>Kinds whose rows produce a "x/y" readiness. Everything else has none to show.</summary>
    private static readonly HashSet<string> ReadyKinds = new(StringComparer.Ordinal)
    {
        "/Pod", "/ReplicationController",
        "apps/Deployment", "apps/ReplicaSet", "apps/StatefulSet", "apps/DaemonSet",
        "batch/Job",
    };

    /// <summary>Kinds <see cref="Describe"/> has something to say about.</summary>
    private static readonly HashSet<string> DetailKinds = new(StringComparer.Ordinal)
    {
        "/ConfigMap", "/Secret", "/Service", "/Node", "/PersistentVolumeClaim", "/PersistentVolume",
        "batch/CronJob", "networking.k8s.io/Ingress",
    };

    private static string KeyOf(ResourceDescriptor descriptor) => $"{descriptor.Group}/{descriptor.Kind}";

    /// <summary>Whether the Status pill and its dot have anything to show for this kind.</summary>
    public static bool ShowsStatus(ResourceDescriptor? descriptor) =>
        descriptor is null || !StatuslessKinds.Contains(KeyOf(descriptor));

    /// <summary>Whether the Ready column applies (pods and the workload controllers).</summary>
    public static bool ShowsReady(ResourceDescriptor? descriptor) =>
        descriptor is not null && ReadyKinds.Contains(KeyOf(descriptor));

    /// <summary>
    /// Only pods restart containers. A Deployment's rollouts are not restarts, and a
    /// column of zeros next to every controller would just be noise.
    /// </summary>
    public static bool ShowsRestarts(ResourceDescriptor? descriptor) =>
        descriptor is { Group: "", Kind: "Pod" };

    /// <summary>Whether the Details column has kind-specific content for this kind.</summary>
    public static bool ShowsDetails(ResourceDescriptor? descriptor) =>
        descriptor is not null && DetailKinds.Contains(KeyOf(descriptor));

    // ------------------------------------------------------- JSON helpers

    /// <summary>"v1" → "" (core), "apps/v1" → "apps" — matches <see cref="ResourceDescriptor.Group"/>.</summary>
    private static string GroupOf(string apiVersion)
    {
        var slash = apiVersion.IndexOf('/');
        return slash < 0 ? "" : apiVersion[..slash];
    }

    private static JsonElement Obj(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static JsonElement Arr(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : default;

    private static string Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement parent, string name, int fallback = 0) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static bool Flag(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool Has(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out _);

    private static int ArrayLength(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;

    /// <summary>
    /// Safe enumeration: a missing property comes back as <c>default(JsonElement)</c>
    /// (ValueKind Undefined), which throws if enumerated directly.
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            yield return item;
        }
    }

    private static IEnumerable<JsonProperty> PropertiesOf(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in obj.EnumerateObject())
        {
            yield return property;
        }
    }

    private static int PropertyCount(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in obj.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static bool IsConditionTrue(JsonElement status, string type)
    {
        foreach (var condition in Items(Arr(status, "conditions")))
        {
            if (Str(condition, "type") == type)
            {
                return Str(condition, "status") == "True";
            }
        }

        return false;
    }

    private static bool IsSidecar(JsonElement initSpecs, string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var container in Items(initSpecs))
        {
            if (Str(container, "name") == name)
            {
                return Str(container, "restartPolicy") == "Always";
            }
        }

        return false;
    }

    private static DateTimeOffset? LastTerminationAt(JsonElement containerStatus)
    {
        var finishedAt = Str(Obj(Obj(containerStatus, "lastState"), "terminated"), "finishedAt");
        return finishedAt.Length > 0 && DateTimeOffset.TryParse(finishedAt, out var at) ? at : null;
    }

    private static void TakeLater(ref DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (candidate is { } value && (current is null || value > current))
        {
            current = value;
        }
    }

    private static string Join(params string?[] parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(" · ");
            }

            text.Append(part);
        }

        return text.ToString();
    }
}
