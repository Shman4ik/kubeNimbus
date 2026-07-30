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

    /// <summary>Well-known descriptor for core/v1 Pods — used before discovery completes and by tests.</summary>
    public static readonly ResourceDescriptor Pods = new(
        Group: "", Version: "v1", Kind: "Pod", Plural: "pods", SingularName: "pod",
        Namespaced: true, ShortNames: ["po"], Categories: ["all"]);

    /// <summary>Well-known descriptor for core/v1 Events — used by the events panel.</summary>
    public static readonly ResourceDescriptor Events = new(
        Group: "", Version: "v1", Kind: "Event", Plural: "events", SingularName: "event",
        Namespaced: true, ShortNames: ["ev"], Categories: []);

    /// <summary>Well-known descriptor for core/v1 Secrets — used to read Helm release records.</summary>
    public static readonly ResourceDescriptor Secrets = new(
        Group: "", Version: "v1", Kind: "Secret", Plural: "secrets", SingularName: "secret",
        Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>Well-known descriptor for core/v1 Namespaces — used to populate the namespace selector.</summary>
    public static readonly ResourceDescriptor Namespaces = new(
        Group: "", Version: "v1", Kind: "Namespace", Plural: "namespaces", SingularName: "namespace",
        Namespaced: false, ShortNames: ["ns"], Categories: []);
}
