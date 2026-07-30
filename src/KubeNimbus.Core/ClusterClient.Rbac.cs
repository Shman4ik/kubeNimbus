using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// RBAC inspection: what the current user may do in a namespace, and which
/// bindings/roles grant a given subject its permissions.
/// </summary>
/// <remarks>
/// Two very different questions, answered two different ways:
/// <list type="bullet">
/// <item>"What can I do here?" is answered by the API server itself
/// (<c>SelfSubjectRulesReview</c>) — never by re-implementing RBAC evaluation
/// locally, which would quietly disagree with the server the moment webhooks,
/// aggregation or impersonation are involved.</item>
/// <item>"Where does this subject's access come from?" has no server-side
/// endpoint, so it's assembled from (Cluster)RoleBindings whose subjects match,
/// with each binding's role resolved to its rules. That's a provenance view, not
/// an authorization decision.</item>
/// </list>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>
    /// The current user's effective rules in a namespace, straight from the API
    /// server's own evaluation. <see cref="SelfSubjectRules.Incomplete"/> is the
    /// server telling you the list may be missing entries (an authorizer that
    /// can't enumerate, e.g. a webhook) — it must be surfaced, not hidden.
    /// </summary>
    public async Task<SelfSubjectRules> GetSelfSubjectRulesAsync(
        string @namespace, CancellationToken cancellationToken = default)
    {
        // Built by hand rather than serialized: JsonSerializer.Serialize on an
        // untyped value is the reflection path, which isn't trim/AOT-safe.
        // JsonEncodedText does the escaping without any of that.
        var body = "{\"apiVersion\":\"authorization.k8s.io/v1\",\"kind\":\"SelfSubjectRulesReview\","
            + "\"spec\":{\"namespace\":\"" + JsonEncodedText.Encode(@namespace) + "\"}}";

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendRequestAsync(
            HttpMethod.Post, "apis/authorization.k8s.io/v1/selfsubjectrulesreviews", content,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return new SelfSubjectRules([], [], Incomplete: false, EvaluationError: null);
        }

        return new SelfSubjectRules(
            ResourceRules: ReadRules(status, "resourceRules"),
            NonResourceRules: ReadRules(status, "nonResourceRules"),
            Incomplete: status.TryGetProperty("incomplete", out var inc) && inc.ValueKind == JsonValueKind.True,
            EvaluationError: status.TryGetProperty("evaluationError", out var err) ? err.GetString() : null);
    }

    /// <summary>
    /// Every RoleBinding/ClusterRoleBinding that names <paramref name="subject"/>,
    /// with the referenced role's rules resolved. Bindings whose role has been
    /// deleted are still listed (with no rules) — a dangling binding is exactly
    /// the kind of thing you open an RBAC view to find.
    /// </summary>
    public async Task<IReadOnlyList<SubjectBinding>> GetBindingsForSubjectAsync(
        SubjectRef subject, CancellationToken cancellationToken = default)
    {
        var roleBindings = await ListResourceOnceAsync(
            ResourceDescriptor.RoleBindings, @namespace: null, fieldSelector: null, cancellationToken).ConfigureAwait(false);
        var clusterRoleBindings = await ListResourceOnceAsync(
            ResourceDescriptor.ClusterRoleBindings, @namespace: null, fieldSelector: null, cancellationToken).ConfigureAwait(false);

        var result = new List<SubjectBinding>();
        var ruleCache = new Dictionary<string, IReadOnlyList<PolicyRule>>(StringComparer.Ordinal);

        foreach (var binding in roleBindings.Concat(clusterRoleBindings))
        {
            if (!BindsSubject(binding, subject))
            {
                continue;
            }

            var (roleKind, roleName) = ReadRoleRef(binding);
            if (roleName is null)
            {
                continue;
            }

            // A RoleBinding may reference either a namespaced Role (in the
            // binding's own namespace) or a ClusterRole; a ClusterRoleBinding
            // always references a ClusterRole.
            var roleNamespace = roleKind == "Role" ? binding.Namespace : null;
            var cacheKey = $"{roleKind}/{roleNamespace}/{roleName}";
            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await ReadRoleRulesAsync(roleKind, roleNamespace, roleName, cancellationToken).ConfigureAwait(false);
                ruleCache[cacheKey] = rules;
            }

            result.Add(new SubjectBinding(
                BindingKind: binding.Kind is { Length: > 0 } kind ? kind : (binding.Namespace is null ? "ClusterRoleBinding" : "RoleBinding"),
                BindingName: binding.Name,
                BindingNamespace: binding.Namespace,
                RoleKind: roleKind,
                RoleName: roleName,
                Rules: rules));
        }

        return [.. result.OrderBy(b => b.BindingNamespace ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.BindingName, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<IReadOnlyList<PolicyRule>> ReadRoleRulesAsync(
        string roleKind, string? roleNamespace, string roleName, CancellationToken ct)
    {
        var descriptor = roleKind == "Role" ? ResourceDescriptor.Roles : ResourceDescriptor.ClusterRoles;
        try
        {
            var role = await ReadResourceAsync(descriptor, roleNamespace, roleName, ct).ConfigureAwait(false);
            return role is null ? [] : ReadRules(role.Raw, "rules");
        }
        catch (HttpRequestException)
        {
            return []; // not readable by this user; the binding itself is still worth showing
        }
    }

    private static bool BindsSubject(DynamicResource binding, SubjectRef subject)
    {
        if (!binding.Raw.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var s in subjects.EnumerateArray())
        {
            var kind = s.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
            var ns = s.TryGetProperty("namespace", out var sn) ? sn.GetString() : null;

            if (!string.Equals(kind, subject.Kind, StringComparison.Ordinal)
                || !string.Equals(name, subject.Name, StringComparison.Ordinal))
            {
                continue;
            }

            // Only ServiceAccount subjects carry a namespace; for User/Group the
            // subject namespace is meaningless and must not be compared.
            if (subject.Kind != "ServiceAccount" || string.Equals(ns, subject.Namespace, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (string RoleKind, string? RoleName) ReadRoleRef(DynamicResource binding)
    {
        if (!binding.Raw.TryGetProperty("roleRef", out var roleRef) || roleRef.ValueKind != JsonValueKind.Object)
        {
            return ("ClusterRole", null);
        }

        var kind = roleRef.TryGetProperty("kind", out var k) ? k.GetString() ?? "ClusterRole" : "ClusterRole";
        var name = roleRef.TryGetProperty("name", out var n) ? n.GetString() : null;
        return (kind, name);
    }

    internal static IReadOnlyList<PolicyRule> ReadRules(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PolicyRule>();
        foreach (var rule in rules.EnumerateArray())
        {
            result.Add(new PolicyRule(
                Verbs: ReadStringArray(rule, "verbs"),
                ApiGroups: ReadStringArray(rule, "apiGroups"),
                Resources: ReadStringArray(rule, "resources"),
                ResourceNames: ReadStringArray(rule, "resourceNames"),
                NonResourceUrls: ReadStringArray(rule, "nonResourceURLs")));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.GetString() is { } value)
            {
                result.Add(value);
            }
        }

        return result;
    }
}

/// <summary>One RBAC rule, in the shape both Role/ClusterRole and SelfSubjectRulesReview use.</summary>
public sealed record PolicyRule(
    IReadOnlyList<string> Verbs,
    IReadOnlyList<string> ApiGroups,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> ResourceNames,
    IReadOnlyList<string> NonResourceUrls)
{
    /// <summary>Resources with their API group, kubectl-style: "pods", "deployments.apps".</summary>
    public string ResourcesText
    {
        get
        {
            if (Resources.Count == 0)
            {
                return string.Join(", ", NonResourceUrls);
            }

            var groups = ApiGroups.Where(g => g.Length > 0).ToArray();
            return string.Join(", ", Resources.Select(r => groups.Length == 0 ? r : $"{r}.{string.Join('/', groups)}"));
        }
    }

    public string VerbsText => string.Join(", ", Verbs);

    /// <summary>Non-empty only for rules narrowed to specific object names.</summary>
    public string ResourceNamesText => string.Join(", ", ResourceNames);

    /// <summary>True for the rules that hand out everything — worth flagging in a viewer.</summary>
    public bool IsWildcard =>
        Verbs.Contains("*") && (Resources.Contains("*") || NonResourceUrls.Contains("*"));
}

/// <summary>The API server's own answer to "what may I do in this namespace?".</summary>
public sealed record SelfSubjectRules(
    IReadOnlyList<PolicyRule> ResourceRules,
    IReadOnlyList<PolicyRule> NonResourceRules,
    bool Incomplete,
    string? EvaluationError);

/// <summary>A subject to inspect: ServiceAccount (namespaced), User or Group.</summary>
public sealed record SubjectRef(string Kind, string Name, string? Namespace);

/// <summary>One binding that grants a subject a role, with that role's rules resolved.</summary>
public sealed record SubjectBinding(
    string BindingKind,
    string BindingName,
    string? BindingNamespace,
    string RoleKind,
    string RoleName,
    IReadOnlyList<PolicyRule> Rules)
{
    public string Scope => BindingNamespace is null ? "cluster-wide" : BindingNamespace;

    /// <summary>False for a binding whose role is gone (or unreadable) — worth calling out in the UI.</summary>
    public bool HasRules => Rules.Count > 0;

    public string RoleText => $"{RoleKind}/{RoleName}";
}
