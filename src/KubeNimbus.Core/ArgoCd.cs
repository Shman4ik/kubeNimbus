using System.Buffers;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>How Argo CD's application controller last compared the live cluster to Git.</summary>
public enum ArgoSyncState
{
    /// <summary>The controller has not said, or said something this app does not recognize.</summary>
    Unknown,

    /// <summary>Live state matches the desired state at the target revision.</summary>
    Synced,

    /// <summary>At least one resource differs from what Git declares.</summary>
    OutOfSync,
}

/// <summary>
/// Argo CD's own health assessment for an Application, which is a different question from
/// whether it is synced: an Application can be perfectly Synced and Degraded (the manifests
/// applied, the pods crash), and OutOfSync and Healthy (someone edited the cluster by hand
/// and what they left behind works).
/// </summary>
public enum ArgoHealthState
{
    Unknown,
    Healthy,
    Progressing,
    Degraded,
    Suspended,

    /// <summary>Declared in Git, absent from the cluster.</summary>
    Missing,
}

/// <summary>
/// Reading and acting on Argo CD Applications through the Kubernetes API and nothing else —
/// no Argo API server, no <c>argocd</c> binary, no URL to paste, no second set of
/// credentials. Argo CD keeps every one of its objects (Applications, ApplicationSets,
/// AppProjects) in etcd as ordinary custom resources, and both actions this app offers are
/// patches of those objects that Argo's own controller watches for. That is the whole
/// integration, and it is what makes it work under the app's fourth hard rule: kubeconfig
/// stays the single source of truth.
///
/// <para>
/// Everything here is pure, for the same reason <see cref="WorkloadActions"/> is: each patch
/// fails <em>silently</em> when it is wrong. A refresh written under the wrong annotation
/// key, or a sync written into <c>spec</c> instead of the object's top-level
/// <c>operation</c>, is a 200 from the API server that no controller ever acts on — which is
/// indistinguishable from a dead button. <c>ArgoCdTests</c> pins the patch bodies
/// byte-for-byte, with no cluster needed.
/// </para>
/// </summary>
public static class ArgoCd
{
    /// <summary>The API group every Argo project uses — CD, Rollouts and Workflows alike.</summary>
    public const string Group = "argoproj.io";

    /// <summary>
    /// The annotation Argo CD's controller watches for a refresh request, and the one
    /// <c>argocd app get --refresh</c> ends up setting. The controller removes it again once
    /// it has re-compared, so a refresh leaves no trace on the object — which is why the UI
    /// reports "requested" rather than claiming a result it cannot observe.
    /// </summary>
    public const string RefreshAnnotation = "argocd.argoproj.io/refresh";

    /// <summary>
    /// Recorded as the operation's initiator, so an Argo CD user reading the Application's
    /// history sees which tool asked for the sync. Argo shows this verbatim in its own UI.
    /// </summary>
    public const string OperationInitiator = "kubenimbus";

    /// <summary>True for the Argo CD Application kind, whatever version this server serves it at.</summary>
    public static bool IsApplicationKind(ResourceDescriptor descriptor) =>
        descriptor is { Group: Group, Kind: "Application" };

    /// <summary>True for any kind in Argo's API group — what the sidebar buckets on.</summary>
    public static bool IsArgoKind(ResourceDescriptor descriptor) => descriptor.Group == Group;

    /// <summary>
    /// Whether this app can ask the cluster to sync or refresh objects of this kind. The kind
    /// is named here, and that is the same honest exception <see cref="NodeActions.SupportsCordon"/>
    /// makes rather than a shortcut: scale has a discovery signal (a <c>scale</c> subresource)
    /// and rollout restart has an object signal (a pod template to stamp), but a sync has
    /// neither. <c>operation</c> is a field of Argo's own Application schema; discovery says
    /// nothing about it, and an Application that has never been synced does not carry it — so
    /// "does the object have the field" answers false for exactly the Applications you would
    /// want to sync. What is left is the kind, plus the half discovery <em>can</em> answer:
    /// does this server say Applications are patchable.
    /// </summary>
    public static bool SupportsSync(ResourceDescriptor descriptor) =>
        IsApplicationKind(descriptor) && descriptor.AllowsVerb("patch");

    /// <summary>Refresh is the same patch through the same verb, so it is the same test.</summary>
    public static bool SupportsRefresh(ResourceDescriptor descriptor) => SupportsSync(descriptor);

    /// <summary>
    /// Finds the Application kind in a discovery catalog, or null when Argo CD is not
    /// installed. The version is whatever the server serves — <c>v1alpha1</c> today, and
    /// nothing here assumes that, exactly as the metrics API's version is read from discovery
    /// rather than hardcoded.
    /// </summary>
    public static ResourceDescriptor? ApplicationDescriptor(IEnumerable<ResourceDescriptor> catalog) =>
        catalog.FirstOrDefault(IsApplicationKind);

    // --------------------------------------------------------------- patch bodies

    /// <summary>
    /// The refresh patch: one annotation and nothing else. <paramref name="hard"/> is Argo's
    /// own "hard refresh", which additionally re-renders the manifests from source instead of
    /// reusing the repo-server's cache — the thing you reach for when Git has changed and
    /// Argo is still showing yesterday's diff.
    /// </summary>
    public static string RefreshPatch(bool hard)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("metadata");
            writer.WriteStartObject("annotations");
            writer.WriteString(RefreshAnnotation, hard ? "hard" : "normal");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The sync patch: the Application's top-level <c>operation</c> field, which is the
    /// documented Kubernetes-native way to ask for a sync and exactly what Argo's own API
    /// server writes when somebody presses Sync in its UI. The application controller watches
    /// for a non-null <c>operation</c>, runs it, and moves the outcome into
    /// <c>status.operationState</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>revision</c> is written, deliberately. Omitting it makes the controller sync to
    /// the Application's own <c>spec.source.targetRevision</c> — which is what the Application
    /// says it wants and what a reader of this app would expect "Sync" to mean. Pinning a
    /// revision here would quietly turn a sync into a deploy of something else.
    /// </para>
    /// <para>
    /// <paramref name="prune"/> is off by default and surfaced in the confirm before it is
    /// sent, because it is the half of a sync that <em>deletes</em>: resources that have left
    /// Git are removed from the cluster. That is ordinary GitOps and also the way a sync
    /// destroys something, so it is a decision, not a default.
    /// </para>
    /// </remarks>
    public static string SyncPatch(bool prune)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("operation");
            writer.WriteStartObject("initiatedBy");
            writer.WriteString("username", OperationInitiator);
            writer.WriteEndObject();
            writer.WriteStartArray("info");
            writer.WriteStartObject();
            writer.WriteString("name", "Reason");
            writer.WriteString("value", "Sync requested from kubeNimbus");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("sync");
            writer.WriteBoolean("prune", prune);
            writer.WriteStartObject("syncStrategy");
            writer.WriteStartObject("hook");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ----------------------------------------------------------------- parsing

    /// <summary>
    /// Argo spells its states in title case (<c>Synced</c>, <c>OutOfSync</c>); anything else
    /// — including a state a future Argo adds — comes back <see cref="ArgoSyncState.Unknown"/>
    /// rather than being guessed at, which is the same "an unclassified condition is its own
    /// answer" rule pod detail's Overview tab settled.
    /// </summary>
    public static ArgoSyncState ParseSync(string? value) => value switch
    {
        "Synced" => ArgoSyncState.Synced,
        "OutOfSync" => ArgoSyncState.OutOfSync,
        _ => ArgoSyncState.Unknown,
    };

    public static ArgoHealthState ParseHealth(string? value) => value switch
    {
        "Healthy" => ArgoHealthState.Healthy,
        "Progressing" => ArgoHealthState.Progressing,
        "Degraded" => ArgoHealthState.Degraded,
        "Suspended" => ArgoHealthState.Suspended,
        "Missing" => ArgoHealthState.Missing,
        _ => ArgoHealthState.Unknown,
    };

    /// <summary>Reads one Argo CD Application object into the fields this app renders.</summary>
    public static ArgoApplication ReadApplication(DynamicResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var root = resource.Raw;
        var spec = Object(root, "spec");
        var status = Object(root, "status");
        var sync = Object(status, "sync");
        var health = Object(status, "health");
        var operationState = Object(status, "operationState");
        var destination = Object(spec, "destination");
        var automated = Object(Object(spec, "syncPolicy"), "automated");

        // A multi-source Application (spec.sources[]) is the newer shape and is increasingly
        // common; the single spec.source is still what most Applications use. Reading the
        // first of sources[] when there is no source at all keeps the summary line honest for
        // both, and the count is carried so the pane can say "+2 more sources" rather than
        // silently showing one of three.
        var sources = Array(spec, "sources");
        var source = spec.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object
            ? s
            : sources.Count > 0 ? sources[0] : default;

        return new ArgoApplication(
            Name: resource.Name,
            Namespace: resource.Namespace ?? "",
            Project: String(spec, "project") ?? "default",
            Sync: ParseSync(String(sync, "status")),
            Health: ParseHealth(String(health, "status")),
            HealthMessage: String(health, "message") ?? "",
            Revision: String(sync, "revision") ?? "",
            RepoUrl: String(source, "repoURL") ?? "",
            SourcePath: String(source, "path") ?? String(source, "chart") ?? "",
            TargetRevision: String(source, "targetRevision") ?? "",
            SourceCount: Math.Max(sources.Count, source.ValueKind == JsonValueKind.Object ? 1 : 0),
            DestinationServer: String(destination, "server") ?? String(destination, "name") ?? "",
            DestinationNamespace: String(destination, "namespace") ?? "",
            AutoSync: automated.ValueKind == JsonValueKind.Object,
            AutoPrune: Bool(automated, "prune"),
            SelfHeal: Bool(automated, "selfHeal"),
            OperationPhase: String(operationState, "phase") ?? "",
            OperationMessage: String(operationState, "message") ?? "",
            OperationStartedAt: Timestamp(operationState, "startedAt"),
            OperationFinishedAt: Timestamp(operationState, "finishedAt"),
            Conditions: ReadConditions(status),
            Resources: ReadResources(status),
            History: ReadHistory(status),
            Resource: resource);
    }

    private static IReadOnlyList<ArgoCondition> ReadConditions(JsonElement status)
    {
        var result = new List<ArgoCondition>();
        foreach (var condition in Array(status, "conditions"))
        {
            result.Add(new ArgoCondition(
                Type: String(condition, "type") ?? "",
                Message: String(condition, "message") ?? "",
                LastTransitionAt: Timestamp(condition, "lastTransitionTime")));
        }

        return result;
    }

    private static IReadOnlyList<ArgoResource> ReadResources(JsonElement status)
    {
        var result = new List<ArgoResource>();
        foreach (var resource in Array(status, "resources"))
        {
            result.Add(new ArgoResource(
                Group: String(resource, "group") ?? "",
                Version: String(resource, "version") ?? "",
                Kind: String(resource, "kind") ?? "",
                Namespace: String(resource, "namespace") ?? "",
                Name: String(resource, "name") ?? "",
                Sync: ParseSync(String(resource, "status")),
                Health: ParseHealth(String(Object(resource, "health"), "status"))));
        }

        return result;
    }

    private static IReadOnlyList<ArgoRevision> ReadHistory(JsonElement status)
    {
        var result = new List<ArgoRevision>();
        foreach (var entry in Array(status, "history"))
        {
            result.Add(new ArgoRevision(
                Id: entry.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0,
                Revision: String(entry, "revision") ?? "",
                DeployedAt: Timestamp(entry, "deployedAt") ?? Timestamp(entry, "deployStartedAt")));
        }

        // Newest first, which is how Argo's own history reads and the order somebody
        // asking "what changed" wants.
        result.Reverse();
        return result;
    }

    private static JsonElement Object(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static IReadOnlyList<JsonElement> Array(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : [];

    private static string? String(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Timestamp(JsonElement owner, string name) =>
        String(owner, name) is { } text && DateTimeOffset.TryParse(text, out var value) ? value : null;
}

/// <summary>One Argo CD Application, as the cluster reports it.</summary>
/// <param name="Resource">
/// The object itself, kept so the list row, the YAML editor and the sync action all act on
/// the same thing the summary was read from rather than on a re-fetch that may already
/// disagree with it.
/// </param>
public sealed record ArgoApplication(
    string Name,
    string Namespace,
    string Project,
    ArgoSyncState Sync,
    ArgoHealthState Health,
    string HealthMessage,
    string Revision,
    string RepoUrl,
    string SourcePath,
    string TargetRevision,
    int SourceCount,
    string DestinationServer,
    string DestinationNamespace,
    bool AutoSync,
    bool AutoPrune,
    bool SelfHeal,
    string OperationPhase,
    string OperationMessage,
    DateTimeOffset? OperationStartedAt,
    DateTimeOffset? OperationFinishedAt,
    IReadOnlyList<ArgoCondition> Conditions,
    IReadOnlyList<ArgoResource> Resources,
    IReadOnlyList<ArgoRevision> History,
    DynamicResource Resource)
{
    /// <summary>"payments/checkout" — unique across the cluster, and the row key.</summary>
    public string Key => $"{Namespace}/{Name}";

    /// <summary>
    /// Whether this Application is one of the ones to look at first. Degraded, Missing and
    /// OutOfSync qualify; Progressing does not, because a rollout in flight is the system
    /// working rather than a problem, and a dashboard that flagged every deploy would be
    /// flagging nothing.
    /// </summary>
    public bool NeedsAttention =>
        Health is ArgoHealthState.Degraded or ArgoHealthState.Missing || Sync == ArgoSyncState.OutOfSync;

    /// <summary>
    /// The one word this Application is filed under when it needs attention — <b>health
    /// outranks sync</b>. A Degraded Application whose sync is also wrong is a degraded
    /// Application: the pods are down either way, and being told it is out of sync sends
    /// somebody to Git when the problem is in the cluster.
    /// </summary>
    public string AttentionReason => Health switch
    {
        ArgoHealthState.Degraded => "Degraded",
        ArgoHealthState.Missing => "Missing",
        _ when Sync == ArgoSyncState.OutOfSync => "OutOfSync",
        _ => "",
    };

    /// <summary>True while Argo is running a sync on this Application right now.</summary>
    public bool IsOperationRunning => OperationPhase is "Running" or "Terminating";

    /// <summary>The short commit Argo last reconciled, which is what a revision column shows.</summary>
    public string ShortRevision => Revision.Length > 7 ? Revision[..7] : Revision;

    /// <summary>"main · apps/checkout" — where the desired state comes from, in one line.</summary>
    public string SourceSummary
    {
        get
        {
            var target = TargetRevision.Length > 0 ? TargetRevision : "HEAD";
            var line = SourcePath.Length > 0 ? $"{target} · {SourcePath}" : target;
            return SourceCount > 1 ? $"{line} (+{SourceCount - 1} more)" : line;
        }
    }

    /// <summary>How Argo's automation is configured, in the words its own UI uses.</summary>
    public string SyncPolicySummary
    {
        get
        {
            if (!AutoSync)
            {
                return "Manual";
            }

            var extras = new List<string>(2);
            if (AutoPrune)
            {
                extras.Add("prune");
            }

            if (SelfHeal)
            {
                extras.Add("self-heal");
            }

            return extras.Count == 0 ? "Automated" : $"Automated ({string.Join(", ", extras)})";
        }
    }
}

/// <summary>One entry of an Application's <c>status.conditions</c>.</summary>
public sealed record ArgoCondition(string Type, string Message, DateTimeOffset? LastTransitionAt)
{
    /// <summary>
    /// Argo names its bad conditions for what they are — every type ending in "Error", plus
    /// the comparison and sync failures. Matched on the suffix rather than against a list of
    /// known types so a condition a newer Argo adds is still read as a problem: this is the
    /// opposite default from pod detail's, and deliberately, because Argo raises a condition
    /// to <em>report a fault</em> where Kubernetes uses them for ordinary state.
    /// </summary>
    public bool IsProblem =>
        Type.EndsWith("Error", StringComparison.Ordinal)
        || Type.Contains("Failed", StringComparison.Ordinal)
        || Type == "OrphanedResourceWarning";
}

/// <summary>One object Argo manages as part of an Application, with its own sync and health.</summary>
public sealed record ArgoResource(
    string Group,
    string Version,
    string Kind,
    string Namespace,
    string Name,
    ArgoSyncState Sync,
    ArgoHealthState Health)
{
    /// <summary>The apiVersion this resource lives at — "v1" in the core group, "group/version" otherwise.</summary>
    public string ApiVersion => Group.Length == 0 ? Version : $"{Group}/{Version}";
}

/// <summary>One entry of an Application's deployment history.</summary>
public sealed record ArgoRevision(long Id, string Revision, DateTimeOffset? DeployedAt)
{
    public string ShortRevision => Revision.Length > 7 ? Revision[..7] : Revision;
}

/// <summary>
/// The seven numbers the Argo dashboard shows across every Application on the cluster. They
/// are counted rather than derived from each other on purpose: Synced and Healthy overlap,
/// and an Application can be both OutOfSync and Degraded, so no two of these add up to the
/// total and none of them can be inferred from the rest.
/// </summary>
public sealed record ArgoSummary(
    int Total,
    int Synced,
    int Healthy,
    int OutOfSync,
    int Degraded,
    int Missing,
    int Progressing)
{
    public static ArgoSummary Of(IEnumerable<ArgoApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);

        int total = 0, synced = 0, healthy = 0, outOfSync = 0, degraded = 0, missing = 0, progressing = 0;
        foreach (var app in applications)
        {
            total++;
            switch (app.Sync)
            {
                case ArgoSyncState.Synced: synced++; break;
                case ArgoSyncState.OutOfSync: outOfSync++; break;
            }

            switch (app.Health)
            {
                case ArgoHealthState.Healthy: healthy++; break;
                case ArgoHealthState.Degraded: degraded++; break;
                case ArgoHealthState.Missing: missing++; break;
                case ArgoHealthState.Progressing: progressing++; break;
            }
        }

        return new ArgoSummary(total, synced, healthy, outOfSync, degraded, missing, progressing);
    }

    /// <summary>True when every Application on the cluster is both Synced and Healthy.</summary>
    public bool IsAllWell => Total > 0 && Synced == Total && Healthy == Total;
}
