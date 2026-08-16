namespace KubeNimbus.Core;

/// <summary>
/// One browsable resource kind (built-in or CRD) as reported by the discovery
/// API — group/version/kind plus enough shape info to build list/watch paths
/// and a sidebar entry. Discovered at connect time, never hardcoded.
/// </summary>
public sealed record ResourceDescriptor(
    string Group,
    string Version,
    string Kind,
    string Plural,
    string SingularName,
    bool Namespaced,
    IReadOnlyList<string> ShortNames,
    IReadOnlyList<string> Categories)
{
    /// <summary>"v1" for core, "group/version" otherwise — matches apiVersion on wire objects.</summary>
    public string ApiVersion => string.IsNullOrEmpty(Group) ? Version : $"{Group}/{Version}";

    /// <summary>REST base path for this resource kind, e.g. "api/v1/pods" or "apis/apps/v1/deployments".</summary>
    public string BasePath => string.IsNullOrEmpty(Group)
        ? $"api/{Version}/{Plural}"
        : $"apis/{Group}/{Version}/{Plural}";

    /// <summary>List/watch path for a namespace (or cluster-scoped / all-namespaces when null).</summary>
    public string CollectionPath(string? @namespace) =>
        Namespaced && @namespace is not null
            ? string.IsNullOrEmpty(Group)
                ? $"api/{Version}/namespaces/{Uri.EscapeDataString(@namespace)}/{Plural}"
                : $"apis/{Group}/{Version}/namespaces/{Uri.EscapeDataString(@namespace)}/{Plural}"
            : BasePath;

    /// <summary>Path to one object by name (used for get/patch/delete).</summary>
    public string ItemPath(string? @namespace, string name) =>
        $"{CollectionPath(@namespace)}/{Uri.EscapeDataString(name)}";

    /// <summary>Path to one of this kind's subresources, e.g. <c>…/deployments/web/scale</c>.</summary>
    public string SubresourcePath(string? @namespace, string name, string subresource) =>
        $"{ItemPath(@namespace, name)}/{subresource}";

    /// <summary>
    /// The subresources discovery reported for this kind — <c>"scale"</c>, <c>"status"</c>,
    /// <c>"log"</c>, … (the part after the slash in the discovery entry's name). This is
    /// how the app knows a kind can be scaled without keeping a list of kinds that can:
    /// a CRD that declares a <c>scale</c> subresource is scalable, and an <c>apps/v1</c>
    /// resource on a server that doesn't serve one isn't.
    /// </summary>
    public IReadOnlyList<string> Subresources { get; init; } = [];

    /// <summary>
    /// The verbs discovery reported for this kind (<c>list</c>, <c>patch</c>,
    /// <c>delete</c>, …). Empty means <em>not known</em>, not "none": descriptors built
    /// by hand (the well-known ones above, the demo catalog, test fixtures) carry no
    /// verbs, and a capability check must not read that as a prohibition —
    /// see <see cref="AllowsVerb"/>.
    /// </summary>
    public IReadOnlyList<string> Verbs { get; init; } = [];

    /// <summary>True when the server reported this subresource for this kind.</summary>
    public bool HasSubresource(string name) =>
        Subresources.Any(s => string.Equals(s, name, StringComparison.Ordinal));

    /// <summary>
    /// Whether the server said this kind supports <paramref name="verb"/>. Unknown
    /// (an empty <see cref="Verbs"/>) answers <c>true</c>: discovery is used here to
    /// hide what a server has said it cannot do, never to invent a prohibition it
    /// didn't state. RBAC is not in this answer either way — the API server is the
    /// authority on permission, and its 403 is what the UI surfaces.
    /// </summary>
    public bool AllowsVerb(string verb) =>
        Verbs.Count == 0 || Verbs.Any(v => string.Equals(v, verb, StringComparison.Ordinal));

    /// <summary>Well-known descriptor for core/v1 Pods — used before discovery completes and by tests.</summary>
    public static readonly ResourceDescriptor Pods = new(
        Group: "", Version: "v1", Kind: "Pod", Plural: "pods", SingularName: "pod",
        Namespaced: true, ShortNames: ["po"], Categories: ["all"]);

    /// <summary>Well-known descriptor for core/v1 Events — used by the events panel.</summary>
    public static readonly ResourceDescriptor Events = new(
        Group: "", Version: "v1", Kind: "Event", Plural: "events", SingularName: "event",
        Namespaced: true, ShortNames: ["ev"], Categories: []);

    /// <summary>Well-known descriptor for core/v1 Secrets — used to read Helm release records and by the env-var reveal path.</summary>
    public static readonly ResourceDescriptor Secrets = new(
        Group: "", Version: "v1", Kind: "Secret", Plural: "secrets", SingularName: "secret",
        Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>Well-known RBAC descriptors — used by the access-review panel to trace a subject's bindings.</summary>
    public static readonly ResourceDescriptor RoleBindings = new(
        Group: "rbac.authorization.k8s.io", Version: "v1", Kind: "RoleBinding", Plural: "rolebindings",
        SingularName: "rolebinding", Namespaced: true, ShortNames: [], Categories: []);

    public static readonly ResourceDescriptor ClusterRoleBindings = new(
        Group: "rbac.authorization.k8s.io", Version: "v1", Kind: "ClusterRoleBinding", Plural: "clusterrolebindings",
        SingularName: "clusterrolebinding", Namespaced: false, ShortNames: [], Categories: []);

    public static readonly ResourceDescriptor Roles = new(
        Group: "rbac.authorization.k8s.io", Version: "v1", Kind: "Role", Plural: "roles",
        SingularName: "role", Namespaced: true, ShortNames: [], Categories: []);

    public static readonly ResourceDescriptor ClusterRoles = new(
        Group: "rbac.authorization.k8s.io", Version: "v1", Kind: "ClusterRole", Plural: "clusterroles",
        SingularName: "clusterrole", Namespaced: false, ShortNames: [], Categories: []);

    /// <summary>Well-known descriptor for core/v1 Namespaces — used to populate the namespace selector.</summary>
    public static readonly ResourceDescriptor Namespaces = new(
        Group: "", Version: "v1", Kind: "Namespace", Plural: "namespaces", SingularName: "namespace",
        Namespaced: false, ShortNames: ["ns"], Categories: []);

    /// <summary>Well-known descriptor for core/v1 ConfigMaps — used by the env-var reveal path.</summary>
    public static readonly ResourceDescriptor ConfigMaps = new(
        Group: "", Version: "v1", Kind: "ConfigMap", Plural: "configmaps", SingularName: "configmap",
        Namespaced: true, ShortNames: [], Categories: []);
}
