using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Reading Argo CD's two other kinds — ApplicationSet and AppProject — into what their detail
/// panes show. Like <see cref="ArgoCd.ReadApplication"/>, every field comes straight off the
/// object the Kubernetes API returned; nothing is asked of Argo's own API server.
/// </summary>
public static partial class ArgoCd
{
    /// <summary>True for Argo CD's ApplicationSet kind, at whatever version the server serves it.</summary>
    public static bool IsApplicationSetKind(ResourceDescriptor descriptor) =>
        descriptor is { Group: Group, Kind: "ApplicationSet" };

    /// <summary>True for Argo CD's AppProject kind, at whatever version the server serves it.</summary>
    public static bool IsProjectKind(ResourceDescriptor descriptor) =>
        descriptor is { Group: Group, Kind: "AppProject" };

    /// <summary>Reads one ApplicationSet into the fields its pane renders.</summary>
    public static ArgoApplicationSet ReadApplicationSet(DynamicResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var root = resource.Raw;
        var spec = Object(root, "spec");
        var status = Object(root, "status");
        var template = Object(spec, "template");
        var templateSpec = Object(template, "spec");
        var sources = Array(templateSpec, "sources");
        var source = Object(templateSpec, "source") is { ValueKind: JsonValueKind.Object } single
            ? single
            : sources.Count > 0 ? sources[0] : default;
        var destination = Object(templateSpec, "destination");
        var policy = Object(spec, "syncPolicy");

        var conditions = new List<ArgoCondition>();
        foreach (var condition in Array(status, "conditions"))
        {
            var type = String(condition, "type") ?? "";
            var holds = String(condition, "status");
            conditions.Add(new ArgoCondition(
                Type: type,
                Message: InvisibleCharacters.Reveal(String(condition, "message") ?? ""),
                LastTransitionAt: Timestamp(condition, "lastTransitionTime"))
            {
                // ErrorOccurred is the one negative condition; the others say something went
                // right, so it is their absence (status False) that is the problem.
                Problem = type == "ErrorOccurred" ? holds == "True" : holds == "False",
            });
        }

        var generated = new List<ArgoResource>();
        foreach (var entry in Array(status, "resources"))
        {
            generated.Add(new ArgoResource(
                Group: String(entry, "group") ?? Group,
                Version: String(entry, "version") ?? "",
                Kind: String(entry, "kind") ?? "Application",
                Namespace: String(entry, "namespace") ?? resource.Namespace ?? "",
                Name: String(entry, "name") ?? "",
                Sync: ParseSync(String(entry, "status")),
                Health: ParseHealth(String(Object(entry, "health"), "status"))));
        }

        return new ArgoApplicationSet(
            Name: resource.Name,
            Namespace: resource.Namespace ?? "",
            Uid: String(Object(root, "metadata"), "uid") ?? "",
            Generators: [.. Array(spec, "generators").Select(DescribeGenerator)],
            TemplateName: String(Object(template, "metadata"), "name") ?? "",
            Project: String(templateSpec, "project") ?? "",
            RepoUrl: String(source, "repoURL") ?? "",
            SourcePath: String(source, "path") ?? String(source, "chart") ?? "",
            TargetRevision: String(source, "targetRevision") ?? "",
            Destination: String(destination, "namespace") is { Length: > 0 } ns
                ? $"{ns} on {String(destination, "server") ?? String(destination, "name") ?? "?"}"
                : String(destination, "server") ?? String(destination, "name") ?? "",
            AutoSync: Object(Object(templateSpec, "syncPolicy"), "automated").ValueKind == JsonValueKind.Object,
            PolicySummary: SetPolicySummary(policy),
            Health: String(Object(status, "health"), "status") ?? "",
            Conditions: conditions,
            Generated: generated,
            Resource: resource);
    }

    /// <summary>
    /// What the set does to the Applications it generated when its generators stop producing
    /// one, or change one — <c>spec.syncPolicy.applicationsSync</c> and
    /// <c>preserveResourcesOnDeletion</c>, in words.
    /// </summary>
    private static string SetPolicySummary(JsonElement policy)
    {
        var mode = String(policy, "applicationsSync") switch
        {
            "create-only" => "Creates Applications; never updates or deletes them",
            "create-update" => "Creates and updates Applications; never deletes them",
            "create-delete" => "Creates and deletes Applications; never updates them",
            _ => "Creates, updates and deletes Applications as its generators change",
        };
        return Bool(policy, "preserveResourcesOnDeletion")
            ? $"{mode}. Deleting an Application leaves its resources on the cluster."
            : $"{mode}.";
    }

    /// <summary>
    /// One generator, in one line: its type and the part of its spec that says what it
    /// iterates over. Matrix and Merge name the generators they combine. An unknown type is
    /// named as it is spelled, never dropped.
    /// </summary>
    public static string DescribeGenerator(JsonElement generator)
    {
        if (generator.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var property in generator.EnumerateObject())
        {
            // A generator may carry a sibling `selector` that post-filters its output.
            if (property.Name == "selector")
            {
                continue;
            }

            var body = property.Value;
            return property.Name switch
            {
                "list" => Array(body, "elements").Count is var n && n == 1
                    ? "List · 1 element"
                    : $"List · {Array(body, "elements").Count} elements",
                "clusters" => Object(body, "selector") is { ValueKind: JsonValueKind.Object } selector
                    && MatchLabels(selector) is { Length: > 0 } labels
                        ? $"Clusters · labelled {labels}"
                        : "Clusters · every cluster Argo CD knows",
                "git" => DescribeGit(body),
                "matrix" or "merge" =>
                    $"{(property.Name == "matrix" ? "Matrix" : "Merge")} of "
                    + string.Join(" and ", Array(body, "generators").Select(g => FirstKey(g)).Where(k => k.Length > 0)),
                "scmProvider" => $"SCM provider · {FirstKey(body, skip: ["filters", "cloneProtocol", "requeueAfterSeconds", "template", "values"])}",
                "pullRequest" => $"Pull requests · {FirstKey(body, skip: ["filters", "requeueAfterSeconds", "template"])}",
                "clusterDecisionResource" => $"Cluster decision resource · {String(body, "configMapRef") ?? ""}",
                "plugin" => $"Plugin · {String(Object(body, "configMapRef"), "name") ?? ""}",
                _ => property.Name,
            };
        }

        return "";
    }

    private static string DescribeGit(JsonElement git)
    {
        var where = $"{String(git, "repoURL") ?? ""} @ {String(git, "revision") ?? "HEAD"}";
        var directories = Array(git, "directories").Select(d =>
            (Bool(d, "exclude") ? "not " : "") + (String(d, "path") ?? "")).ToList();
        if (directories.Count > 0)
        {
            return $"Git directories · {string.Join(", ", directories)} · {where}";
        }

        var files = Array(git, "files").Select(f => String(f, "path") ?? "").ToList();
        return files.Count > 0
            ? $"Git files · {string.Join(", ", files)} · {where}"
            : $"Git · {where}";
    }

    private static string MatchLabels(JsonElement selector)
    {
        var labels = Object(selector, "matchLabels");
        return labels.ValueKind != JsonValueKind.Object
            ? ""
            : string.Join(", ", labels.EnumerateObject().Select(l => $"{l.Name}={(l.Value.ValueKind == JsonValueKind.String ? l.Value.GetString() : l.Value.ToString())}"));
    }

    private static string FirstKey(JsonElement owner, string[]? skip = null)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var property in owner.EnumerateObject())
        {
            if (property.Name != "selector" && (skip is null || !skip.Contains(property.Name)))
            {
                return property.Name;
            }
        }

        return "";
    }

    /// <summary>Reads one AppProject into the fields its pane renders.</summary>
    public static ArgoProject ReadProject(DynamicResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var spec = Object(resource.Raw, "spec");

        static IReadOnlyList<string> Strings(JsonElement owner, string name) =>
            [.. Array(owner, name).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];

        static IReadOnlyList<string> GroupKinds(JsonElement owner, string name) =>
            [.. Array(owner, name).Select(e =>
            {
                var group = String(e, "group") ?? "";
                var kind = String(e, "kind") ?? "";
                var label = $"{(group.Length == 0 ? "core" : group)}/{kind}";
                return String(e, "name") is { Length: > 0 } named ? $"{label} named {named}" : label;
            })];

        return new ArgoProject(
            Name: resource.Name,
            Namespace: resource.Namespace ?? "",
            Description: InvisibleCharacters.Reveal(String(spec, "description") ?? ""),
            SourceRepos: Strings(spec, "sourceRepos"),
            Destinations: [.. Array(spec, "destinations").Select(d => new ArgoDestination(
                String(d, "server") ?? "", String(d, "name") ?? "", String(d, "namespace") ?? ""))],
            SourceNamespaces: Strings(spec, "sourceNamespaces"),
            ClusterResourceAllow: GroupKinds(spec, "clusterResourceWhitelist"),
            ClusterResourceDeny: GroupKinds(spec, "clusterResourceBlacklist"),
            // Absent and empty mean different things for this one list (see NamespacedKindsRule).
            NamespaceResourceAllow: spec.ValueKind == JsonValueKind.Object
                && spec.TryGetProperty("namespaceResourceWhitelist", out var allow)
                && allow.ValueKind == JsonValueKind.Array
                    ? GroupKinds(spec, "namespaceResourceWhitelist")
                    : null,
            NamespaceResourceDeny: GroupKinds(spec, "namespaceResourceBlacklist"),
            SyncWindows: [.. Array(spec, "syncWindows").Select(w => new ArgoSyncWindow(
                Kind: String(w, "kind") ?? "",
                Schedule: String(w, "schedule") ?? "",
                Duration: String(w, "duration") ?? "",
                TimeZone: String(w, "timeZone") ?? "",
                ManualSync: Bool(w, "manualSync"),
                Applications: Strings(w, "applications"),
                Namespaces: Strings(w, "namespaces"),
                Clusters: Strings(w, "clusters")))],
            Roles: [.. Array(spec, "roles").Select(r => new ArgoProjectRole(
                Name: String(r, "name") ?? "",
                Description: String(r, "description") ?? "",
                Policies: Array(r, "policies").Count,
                Groups: Strings(r, "groups")))],
            Resource: resource);
    }
}

/// <summary>One Argo CD ApplicationSet, as the cluster reports it.</summary>
/// <param name="Generated">
/// The Applications it generated, from its own <c>status.resources</c> (Argo CD 2.6+). The
/// pane overlays each one's live sync and health when it can read Applications.
/// </param>
public sealed record ArgoApplicationSet(
    string Name,
    string Namespace,
    string Uid,
    IReadOnlyList<string> Generators,
    string TemplateName,
    string Project,
    string RepoUrl,
    string SourcePath,
    string TargetRevision,
    string Destination,
    bool AutoSync,
    string PolicySummary,
    string Health,
    IReadOnlyList<ArgoCondition> Conditions,
    IReadOnlyList<ArgoResource> Generated,
    DynamicResource Resource)
{
    public string Key => $"{Namespace}/{Name}";

    /// <summary>"main · apps/{{.env}}" — the template's source, as the Application pane writes one.</summary>
    public string SourceSummary
    {
        get
        {
            var target = TargetRevision.Length > 0 ? TargetRevision : "HEAD";
            return SourcePath.Length > 0 ? $"{target} · {SourcePath}" : target;
        }
    }
}

/// <summary>One Argo CD AppProject: what its Applications may deploy, from where, to where.</summary>
/// <param name="NamespaceResourceAllow">
/// Null when the project does not set the list, which allows every namespaced kind; an empty
/// list allows none. Argo reads the two differently (<c>IsGroupKindNamePermitted</c>), so they
/// are kept apart here.
/// </param>
public sealed record ArgoProject(
    string Name,
    string Namespace,
    string Description,
    IReadOnlyList<string> SourceRepos,
    IReadOnlyList<ArgoDestination> Destinations,
    IReadOnlyList<string> SourceNamespaces,
    IReadOnlyList<string> ClusterResourceAllow,
    IReadOnlyList<string> ClusterResourceDeny,
    IReadOnlyList<string>? NamespaceResourceAllow,
    IReadOnlyList<string> NamespaceResourceDeny,
    IReadOnlyList<ArgoSyncWindow> SyncWindows,
    IReadOnlyList<ArgoProjectRole> Roles,
    DynamicResource Resource)
{
    public string Key => $"{Namespace}/{Name}";

    /// <summary>
    /// Argo permits a source only if some entry matches it, so an empty list allows no
    /// repository at all. A leading <c>!</c> makes an entry a deny pattern.
    /// </summary>
    public string SourceReposRule => SourceRepos.Count == 0
        ? "No repository is allowed: the list is empty."
        : SourceRepos.Contains("*") ? "Any repository." : "";

    public string DestinationsRule => Destinations.Count == 0
        ? "No destination is allowed: the list is empty."
        : "";

    /// <summary>A cluster-scoped kind must be on the allow list, so an empty list allows none.</summary>
    public string ClusterKindsRule => ClusterResourceAllow.Count == 0
        ? "No cluster-scoped kind may be deployed: the allow list is empty."
        : "";

    public string NamespacedKindsRule => NamespaceResourceAllow switch
    {
        null => NamespaceResourceDeny.Count == 0
            ? "Every namespaced kind."
            : "Every namespaced kind except the deny list below.",
        { Count: 0 } => "No namespaced kind may be deployed: the allow list is set and empty.",
        _ => "",
    };

    /// <summary>
    /// Applications in these namespaces may use the project, besides those in Argo CD's own
    /// namespace ("applications in any namespace"). Empty is the classic install.
    /// </summary>
    public string SourceNamespacesRule => SourceNamespaces.Count == 0
        ? "Only Applications in Argo CD's own namespace."
        : "";
}

/// <summary>One allowed destination: a cluster (by server URL or by name) and a namespace pattern.</summary>
public sealed record ArgoDestination(string Server, string Name, string Namespace)
{
    public string Cluster => Server.Length > 0 ? Server : Name.Length > 0 ? Name : "*";

    public string Text => $"{(Namespace.Length > 0 ? Namespace : "*")} on {Cluster}";
}

/// <summary>One sync window: when Argo allows or denies syncs for the Applications it matches.</summary>
public sealed record ArgoSyncWindow(
    string Kind,
    string Schedule,
    string Duration,
    string TimeZone,
    bool ManualSync,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> Clusters)
{
    public bool IsDeny => Kind == "deny";

    /// <summary>"deny · 0 22 * * * for 8h (UTC)".</summary>
    public string When => $"{Kind} · {Schedule} for {Duration}{(TimeZone.Length > 0 ? $" ({TimeZone})" : "")}";

    /// <summary>
    /// Which Applications it covers, and the one switch that changes what a manual sync — the
    /// kind kubeNimbus sends — does inside it.
    /// </summary>
    public string Scope
    {
        get
        {
            var parts = new List<string>(4);
            if (Applications.Count > 0)
            {
                parts.Add($"applications {string.Join(", ", Applications)}");
            }

            if (Namespaces.Count > 0)
            {
                parts.Add($"namespaces {string.Join(", ", Namespaces)}");
            }

            if (Clusters.Count > 0)
            {
                parts.Add($"clusters {string.Join(", ", Clusters)}");
            }

            var scope = parts.Count == 0 ? "Matches no Application" : $"Matches {string.Join("; ", parts)}";
            return Kind switch
            {
                "deny" => ManualSync ? $"{scope}. Manual syncs are still allowed." : $"{scope}. Manual syncs are blocked too.",
                "allow" => ManualSync
                    ? $"{scope}. Outside its hours only manual syncs run."
                    : $"{scope}. Outside its hours syncs are blocked, manual ones too.",
                _ => scope,
            };
        }
    }
}

/// <summary>One project role, without its policies' text: a name, who holds it, how many rules.</summary>
public sealed record ArgoProjectRole(string Name, string Description, int Policies, IReadOnlyList<string> Groups)
{
    public string Summary
    {
        get
        {
            var rules = Policies == 1 ? "1 policy" : $"{Policies} policies";
            var groups = Groups.Count == 0 ? "" : $" · groups {string.Join(", ", Groups)}";
            return Description.Length > 0 ? $"{Description} · {rules}{groups}" : $"{rules}{groups}";
        }
    }
}
