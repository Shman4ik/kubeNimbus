using System.Buffers;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The rules behind the node-level mutating actions — cordon, uncordon and drain:
/// which of them an object supports, the exact patch body cordon sends, the eviction
/// body drain posts, and (the part that carries all the risk) which pods a drain may
/// evict, which it must skip, and which it must refuse to touch without being told to.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="ClusterClient"/> for the same reason
/// <see cref="WorkloadActions"/> is: every mistake in here is <em>silent</em>. A cordon
/// patch that names the wrong field is a 200 that schedules nothing differently, and a
/// drain that misclassifies one pod deletes data nobody agreed to lose — which is not a
/// hypothetical, it is the open bug this classification was written against
/// (kubernetes-sigs/headlamp#7268, a hand-rolled drain that deleted mirror pods and
/// <c>emptyDir</c> pods without warning). <c>NodeActionsTests</c> pins the patch bodies
/// byte for byte and every branch of the classification, with no cluster needed.
/// </para>
/// </remarks>
public static class NodeActions
{
    /// <summary>
    /// The <c>eviction</c> subresource's name in discovery and in a request path. Its
    /// presence is what tells the app the cluster serves the Eviction API at all — the
    /// same evidence <see cref="WorkloadActions.SupportsScale"/> takes from
    /// <c>scale</c>, rather than a hardcoded version check.
    /// </summary>
    public const string EvictionSubresource = "eviction";

    /// <summary>
    /// The API version of the Eviction object posted to <c>pods/{name}/eviction</c>.
    /// <c>policy/v1</c> has been served since Kubernetes 1.22 (2021) and is what
    /// <c>kubectl</c> itself sends; <c>policy/v1beta1</c> was removed in 1.25. A server
    /// too old for it answers with its own message, which the drain surfaces verbatim
    /// rather than guessing a second version to retry with.
    /// </summary>
    public const string EvictionApiVersion = "policy/v1";

    /// <summary>The annotation the kubelet stamps on a static (mirror) pod.</summary>
    public const string MirrorPodAnnotation = "kubernetes.io/config.mirror";

    /// <summary>
    /// Whether this kind can be cordoned. Unlike scale (a subresource discovery
    /// declares) and restart (a pod template you can read off the object), there is
    /// <em>no</em> generalizable signal for cordon: <c>spec.unschedulable</c> is a field
    /// of the core <c>v1.Node</c> schema, discovery says nothing about it, and an
    /// uncordoned node omits it entirely — so "does the object have the field" answers
    /// false for exactly the nodes you want to cordon. Naming the kind is therefore the
    /// accurate test rather than a shortcut, and discovery still contributes the half it
    /// can: whether the server says nodes are patchable at all.
    /// </summary>
    public static bool SupportsCordon(ResourceDescriptor descriptor) =>
        IsNodeKind(descriptor) && descriptor.AllowsVerb("patch");

    /// <summary>
    /// Whether a drain can be offered: the node is cordonable (a drain cordons first,
    /// and a drain that could not stop scheduling would evict pods the scheduler then
    /// puts straight back) and the server serves <c>pods/eviction</c>.
    /// </summary>
    /// <param name="nodeDescriptor">The Node kind's descriptor, from this cluster's discovery.</param>
    /// <param name="podDescriptor">
    /// The Pod kind's descriptor from the <em>same</em> cluster's discovery, or null when
    /// the catalog has not been read yet — in which case the answer is false, because a
    /// drain with nothing to evict pods through is a button that cannot work.
    /// </param>
    public static bool SupportsDrain(ResourceDescriptor nodeDescriptor, ResourceDescriptor? podDescriptor) =>
        SupportsCordon(nodeDescriptor)
        && podDescriptor is not null
        && podDescriptor.HasSubresource(EvictionSubresource);

    /// <summary>The node a pod is assigned to, or an empty string when it is unscheduled.</summary>
    public static string NodeNameOf(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        return Str(Object(pod.Raw, "spec"), "nodeName");
    }

    /// <summary>True for core/v1 Node.</summary>
    public static bool IsNodeKind(ResourceDescriptor descriptor) =>
        descriptor is { Group: "", Kind: "Node" };

    /// <summary>
    /// Whether the node is currently cordoned (<c>spec.unschedulable: true</c>). Absent
    /// means schedulable — the API server omits the field rather than writing false.
    /// </summary>
    public static bool IsCordoned(DynamicResource node) =>
        node.Raw.ValueKind == JsonValueKind.Object
        && node.Raw.TryGetProperty("spec", out var spec)
        && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("unschedulable", out var flag)
        && flag.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The cordon/uncordon patch body — <c>{"spec":{"unschedulable":true|false}}</c> and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Uncordon writes an explicit <c>false</c> rather than a JSON <c>null</c>, which in
    /// an RFC 7386 merge patch would <em>remove</em> the field. Removing it happens to
    /// mean the same thing to the scheduler, but it also means the object no longer
    /// records that anything ever set it, and `kubectl uncordon` writes false — a
    /// cordon from kubeNimbus and one from kubectl must be the same event to whoever
    /// reads the object afterwards, which is the same argument
    /// <see cref="WorkloadActions.RestartedAtAnnotation"/> is under.
    /// </remarks>
    public static string CordonPatch(bool unschedulable)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("spec");
            writer.WriteBoolean("unschedulable", unschedulable);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The body posted to a pod's <c>eviction</c> subresource. An Eviction names the pod
    /// it is about in its own metadata (the API server checks it against the path) and
    /// optionally carries delete options.
    /// </summary>
    /// <param name="gracePeriodSeconds">
    /// Null leaves the pod's own <c>terminationGracePeriodSeconds</c> alone, which is
    /// the right default: an app's shutdown window is a property of the app, not of who
    /// happens to be draining the node.
    /// </param>
    public static string EvictionBody(string @namespace, string name, int? gracePeriodSeconds = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", EvictionApiVersion);
            writer.WriteString("kind", "Eviction");
            writer.WriteStartObject("metadata");
            writer.WriteString("name", name);
            writer.WriteString("namespace", @namespace);
            writer.WriteEndObject();
            if (gracePeriodSeconds is { } grace)
            {
                writer.WriteStartObject("deleteOptions");
                writer.WriteNumber("gracePeriodSeconds", grace);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Classifies every pod on a node into what a drain will do with it. Pure, so the
    /// whole of the dangerous half of drain is decidable without a cluster — and so the
    /// UI can state the plan <em>before</em> anything is evicted, which is the only
    /// point at which a refusal is any use.
    /// </summary>
    public static DrainPlan Plan(IEnumerable<DynamicResource> podsOnNode, DrainOptions options)
    {
        ArgumentNullException.ThrowIfNull(podsOnNode);
        ArgumentNullException.ThrowIfNull(options);

        var entries = new List<DrainPodPlan>();
        foreach (var pod in podsOnNode)
        {
            entries.Add(Classify(pod, options));
        }

        // Sorted so the plan reads the same way twice and the pods a drain will act on
        // come first — a list whose order depends on the API server's paging order is a
        // list nobody can compare against the last time they looked.
        entries.Sort(static (a, b) =>
        {
            var byDisposition = a.Disposition.CompareTo(b.Disposition);
            return byDisposition != 0
                ? byDisposition
                : string.CompareOrdinal($"{a.Namespace}/{a.Name}", $"{b.Namespace}/{b.Name}");
        });

        return new DrainPlan(entries);
    }

    /// <summary>
    /// What a drain does with one pod. The order of the checks is the order kubectl's
    /// own filters run in, and each one is here because skipping it is a known way to
    /// break a cluster.
    /// </summary>
    private static DrainPodPlan Classify(DynamicResource pod, DrainOptions options)
    {
        var @namespace = pod.Namespace ?? "";
        var name = pod.Name;
        var spec = Object(pod.Raw, "spec");
        var status = Object(pod.Raw, "status");

        // Already on its way out: evicting it again achieves nothing and its 404 would
        // read as a failure. The drain still waits for it, because the node is not
        // drained until it is gone.
        if (Object(pod.Raw, "metadata").TryGetProperty("deletionTimestamp", out var deleting)
            && deleting.ValueKind == JsonValueKind.String)
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.AlreadyTerminating,
                "already terminating");
        }

        // A mirror pod is the kubelet's read-only shadow of a static pod on disk.
        // Evicting it deletes the shadow and the kubelet recreates it seconds later —
        // so it is not a way to remove the workload, it is a way to make the drain look
        // like it never finished. kubectl skips these unconditionally, and so does this.
        if (pod.Annotations.ContainsKey(MirrorPodAnnotation))
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.SkippedMirror,
                "static pod — the kubelet owns it and would recreate it");
        }

        // A pod that has finished holds no compute and cannot be evicted meaningfully;
        // its record is all that is left on the node.
        var phase = Str(status, "phase");
        if (phase is "Succeeded" or "Failed")
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.SkippedFinished,
                $"already {phase.ToLowerInvariant()}");
        }

        // DaemonSet pods come back the instant they are removed, by design: the
        // DaemonSet controller ignores `unschedulable`. kubectl refuses to start
        // without --ignore-daemonsets; this app always ignores them and *says so in the
        // plan*, because a drain that will not begin until you tick a box whose only
        // possible answer is yes is a worse gate than a sentence naming what was left
        // behind.
        if (ControllerKind(pod) is "DaemonSet")
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.SkippedDaemonSet,
                "DaemonSet pod — its controller ignores cordon and would recreate it");
        }

        // Nothing recreates a pod with no controller: evicting it destroys it. kubectl
        // calls this --force and so does this, and it is refused rather than assumed.
        var unmanaged = ControllerKind(pod) is null;

        // An emptyDir is node-local storage. Evicting the pod deletes it with no copy
        // anywhere, which is exactly the silent data loss headlamp#7268 reports; kubectl
        // calls this --delete-emptydir-data.
        var emptyDirs = EmptyDirVolumeCount(spec);

        if (unmanaged && !options.Force)
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.BlockedUnmanaged,
                "not managed by a controller — nothing would recreate it");
        }

        if (emptyDirs > 0 && !options.DeleteEmptyDirData)
        {
            return new DrainPodPlan(@namespace, name, DrainDisposition.BlockedLocalData,
                emptyDirs == 1
                    ? "uses an emptyDir volume — its contents are deleted with the pod"
                    : $"uses {emptyDirs} emptyDir volumes — their contents are deleted with the pod");
        }

        var note = (unmanaged, emptyDirs > 0) switch
        {
            (true, true) => "unmanaged, and its emptyDir data is deleted with it",
            (true, false) => "unmanaged — nothing will recreate it",
            (false, true) => "its emptyDir data is deleted with it",
            _ => "",
        };

        return new DrainPodPlan(@namespace, name, DrainDisposition.Evict, note);
    }

    /// <summary>The kind of the pod's controlling owner, or null when it has none.</summary>
    private static string? ControllerKind(DynamicResource pod)
    {
        foreach (var owner in pod.OwnerReferences)
        {
            if (owner.Controller)
            {
                return owner.Kind;
            }
        }

        return null;
    }

    /// <summary>How many <c>emptyDir</c> volumes the pod declares.</summary>
    private static int EmptyDirVolumeCount(JsonElement spec)
    {
        if (!spec.TryGetProperty("volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var volume in volumes.EnumerateArray())
        {
            if (volume.ValueKind == JsonValueKind.Object && volume.TryGetProperty("emptyDir", out var emptyDir)
                && emptyDir.ValueKind == JsonValueKind.Object)
            {
                count++;
            }
        }

        return count;
    }

    internal static JsonElement Object(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    internal static string Str(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>
/// The two questions a drain has to ask before it starts, plus the grace period. They
/// are <c>kubectl drain</c>'s <c>--force</c> and <c>--delete-emptydir-data</c> under
/// their plain-English names, and they are options rather than defaults because each
/// one authorizes destroying something that cannot be recovered.
/// </summary>
/// <param name="Force">
/// Evict pods that no controller owns. Without it such a pod is refused, because
/// nothing recreates it: draining the node deletes the workload.
/// </param>
/// <param name="DeleteEmptyDirData">
/// Evict pods with <c>emptyDir</c> volumes. Without it they are refused, because an
/// <c>emptyDir</c> lives on this node's disk and goes with the pod.
/// </param>
/// <param name="GracePeriodSeconds">
/// Override the pods' own termination grace period. Null means "use each pod's own",
/// which is almost always what is wanted.
/// </param>
public sealed record DrainOptions(
    bool Force = false,
    bool DeleteEmptyDirData = false,
    int? GracePeriodSeconds = null);

/// <summary>
/// What a drain will do with one pod. The numeric order is the order the plan is
/// listed in: what will be acted on, then what is refused, then what is skipped.
/// </summary>
public enum DrainDisposition
{
    /// <summary>Will be evicted.</summary>
    Evict = 0,

    /// <summary>Refused: no controller owns it, and <see cref="DrainOptions.Force"/> was not given.</summary>
    BlockedUnmanaged = 1,

    /// <summary>Refused: it has <c>emptyDir</c> data, and <see cref="DrainOptions.DeleteEmptyDirData"/> was not given.</summary>
    BlockedLocalData = 2,

    /// <summary>Already being deleted; the drain waits for it rather than evicting it again.</summary>
    AlreadyTerminating = 3,

    /// <summary>A static pod's mirror — the kubelet owns it and would recreate it.</summary>
    SkippedMirror = 4,

    /// <summary>Owned by a DaemonSet, whose controller ignores cordon.</summary>
    SkippedDaemonSet = 5,

    /// <summary>Already Succeeded or Failed; there is nothing running to evict.</summary>
    SkippedFinished = 6,
}

/// <summary>One pod's place in a drain plan, with the sentence explaining it.</summary>
public sealed record DrainPodPlan(string Namespace, string Name, DrainDisposition Disposition, string Note)
{
    public string Key => $"{Namespace}/{Name}";
}

/// <summary>
/// What a drain would do to a node, decided before anything is evicted. A plan with
/// <see cref="IsBlocked"/> set is one the drain refuses to run: the blocked pods are
/// named, with the option that would unblock each, and nothing at all has happened yet.
/// </summary>
public sealed record DrainPlan(IReadOnlyList<DrainPodPlan> Pods)
{
    public IEnumerable<DrainPodPlan> Evictable => Pods.Where(p => p.Disposition == DrainDisposition.Evict);

    public IEnumerable<DrainPodPlan> Blocked => Pods.Where(p =>
        p.Disposition is DrainDisposition.BlockedUnmanaged or DrainDisposition.BlockedLocalData);

    public IEnumerable<DrainPodPlan> Waiting => Pods.Where(p => p.Disposition == DrainDisposition.AlreadyTerminating);

    public IEnumerable<DrainPodPlan> Skipped => Pods.Where(p => p.Disposition
        is DrainDisposition.SkippedMirror or DrainDisposition.SkippedDaemonSet or DrainDisposition.SkippedFinished);

    public int EvictCount => Evictable.Count();

    public int BlockedCount => Blocked.Count();

    public int SkippedCount => Skipped.Count();

    public int WaitingCount => Waiting.Count();

    /// <summary>True when at least one pod needs an option nobody has given — the drain must not start.</summary>
    public bool IsBlocked => BlockedCount > 0;

    /// <summary>True when there is nothing at all for the drain to do.</summary>
    public bool IsEmpty => EvictCount == 0 && WaitingCount == 0;

    /// <summary>
    /// The plan in one sentence, for the confirm step. Says what will be evicted and
    /// what will be left behind — a drain that reports only its own work leaves the
    /// reader believing the node is empty afterwards, and on any real cluster it is not
    /// (DaemonSet and static pods stay, exactly as with kubectl).
    /// </summary>
    public string Summary()
    {
        var parts = new List<string>();
        parts.Add(EvictCount == 1 ? "1 pod will be evicted" : $"{EvictCount} pods will be evicted");
        if (WaitingCount > 0)
        {
            parts.Add($"{WaitingCount} already terminating");
        }

        if (SkippedCount > 0)
        {
            parts.Add($"{SkippedCount} left in place (DaemonSet, static or finished pods)");
        }

        return string.Join(" · ", parts) + ".";
    }
}
