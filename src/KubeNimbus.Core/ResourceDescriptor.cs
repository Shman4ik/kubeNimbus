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

    /// <summary>
    /// List/watch path for a namespace (or cluster-scoped / all-namespaces when null).
    /// Throws <see cref="ArgumentException"/> for a namespace that cannot be a path segment
    /// (see <see cref="IsValidPathSegment"/>).
    /// </summary>
    public string CollectionPath(string? @namespace) =>
        Namespaced && @namespace is not null
            ? string.IsNullOrEmpty(Group)
                ? $"api/{Version}/namespaces/{PathSegment(@namespace, "namespace")}/{Plural}"
                : $"apis/{Group}/{Version}/namespaces/{PathSegment(@namespace, "namespace")}/{Plural}"
            : BasePath;

    /// <summary>
    /// Path to one object by name (used for get/patch/delete). Throws
    /// <see cref="ArgumentException"/> for a name or namespace that cannot be a path segment.
    /// </summary>
    public string ItemPath(string? @namespace, string name) =>
        $"{CollectionPath(@namespace)}/{PathSegment(name, "name")}";

    /// <summary>Path to one of this kind's subresources, e.g. <c>…/deployments/web/scale</c>.</summary>
    public string SubresourcePath(string? @namespace, string name, string subresource) =>
        $"{ItemPath(@namespace, name)}/{subresource}";

    /// <summary>
    /// Whether <paramref name="value"/> can be one segment of an API path: not empty, not
    /// <c>.</c> or <c>..</c>, and without <c>/</c> or <c>%</c>. That is the API server's own
    /// rule for an object's name (<c>path.IsValidPathSegmentName</c>), so no object that exists
    /// fails it.
    /// </summary>
    /// <remarks>
    /// The names that reach these paths are often not ones the app chose: an owner reference,
    /// the object an Event is about, an env var's ConfigMap, an Argo Application's
    /// <c>status.resources</c> — strings whoever wrote that object wrote. Escaping is not
    /// enough on its own: <see cref="Uri.EscapeDataString"/> leaves <c>.</c> alone, and
    /// <c>new Uri(base, relative)</c> then collapses dot segments, so a name of <c>..</c> turned
    /// <c>…/namespaces/a/pods/..</c> into <c>…/namespaces/a/</c> and a namespace of <c>..</c> made
    /// a request cluster-wide; an empty name was the collection itself.
    /// </remarks>
    public static bool IsValidPathSegment(string? value) =>
        !string.IsNullOrEmpty(value)
        && value is not "." and not ".."
        && value.AsSpan().IndexOfAny('/', '%') < 0;

    /// <summary>
    /// Whether a discovery value (group, version, plural) is safe to put in a path unescaped,
    /// the way <see cref="BasePath"/> does: letters, digits, <c>-</c>, <c>.</c> and <c>_</c>, and
    /// not a dot segment. Every real group, version and resource name is that shape; a
    /// discovery document that says otherwise is dropped from the catalog rather than
    /// trusted to build a path.
    /// </summary>
    public static bool IsSafeDiscoveryValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    /// <summary>Whether every part of <see cref="BasePath"/> is <see cref="IsSafeDiscoveryValue"/> (the group may be empty: the core group).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSafePath =>
        (Group.Length == 0 || IsSafeDiscoveryValue(Group)) && IsSafeDiscoveryValue(Version) && IsSafeDiscoveryValue(Plural);

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="name"/> can be an object's name.</summary>
    public static void RequireName(string? name, string parameterName = "name")
    {
        if (!IsValidPathSegment(name))
        {
            throw new ArgumentException(InvalidSegmentMessage(name, "name"), parameterName);
        }
    }

    /// <summary>
    /// <paramref name="value"/> escaped as one path segment, or <see cref="ArgumentException"/> when it
    /// cannot be one (<see cref="IsValidPathSegment"/>). For the paths built outside this type —
    /// a pod's log, its metrics, a CRD by name.
    /// </summary>
    public static string PathSegment(string value, string role = "name") =>
        IsValidPathSegment(value)
            ? Uri.EscapeDataString(value)
            : throw new ArgumentException(InvalidSegmentMessage(value, role), role);

    private static string InvalidSegmentMessage(string? value, string role) =>
        string.IsNullOrEmpty(value)
            ? $"An object's {role} cannot be empty."
            : $"\"{value}\" cannot be an object's {role}: it is a dot segment or contains '/' or '%'.";

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

    /// <summary>
    /// Well-known descriptor for discovery.k8s.io/v1 EndpointSlices — the Service pane's
    /// endpoints. GA since Kubernetes 1.21; an older server answers the list with a 404,
    /// which the pane states in place of its endpoints.
    /// </summary>
    public static readonly ResourceDescriptor EndpointSlices = new(
        Group: "discovery.k8s.io", Version: "v1", Kind: "EndpointSlice", Plural: "endpointslices",
        SingularName: "endpointslice", Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>Well-known descriptor for core/v1 ConfigMaps — used by the env-var reveal path.</summary>
    public static readonly ResourceDescriptor ConfigMaps = new(
        Group: "", Version: "v1", Kind: "ConfigMap", Plural: "configmaps", SingularName: "configmap",
        Namespaced: true, ShortNames: [], Categories: []);
}
