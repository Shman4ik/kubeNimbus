using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The cluster-wide direction of the access review: "who can do X?".
/// </summary>
/// <remarks>
/// <para>
/// Kubernetes has no server-side endpoint for this question. <c>SelfSubjectRulesReview</c>
/// only answers for the caller, and <c>SubjectAccessReview</c> only answers for a subject
/// you already named — neither enumerates subjects. So <see cref="ClusterClient.WhoCanAsync"/>
/// scans the RBAC objects (Roles/ClusterRoles + the bindings that reference them) and
/// matches their rules against the query, the same way <c>kubectl-who-can</c> does.
/// </para>
/// <para>
/// That makes this a <b>provenance view over RBAC, not an authorization decision</b> — the
/// same framing as <see cref="ClusterClient.GetBindingsForSubjectAsync"/>, and it must stay
/// that way. A local scan cannot see webhook authorizers, the node/ABAC authorizers or
/// impersonation, so it can both miss access and list access some other authorizer denies.
/// The one authoritative answer available is per-subject, and it is
/// <see cref="ClusterClient.CheckAccessAsync"/> (<c>SubjectAccessReview</c>) — which is why
/// the UI offers it as a per-subject confirmation on top of the scan rather than presenting
/// the scan as the truth.
/// </para>
/// <para>
/// Rule matching follows the API server's own semantics (see
/// <c>k8s.io/kubernetes/pkg/registry/rbac/validation</c>): a rule matches when the verb,
/// the API group and the resource each match exactly or via the <c>*</c> wildcard, where
/// the resource is compared as the combined <c>resource/subresource</c> name. RBAC has no
/// partial wildcards — <c>pods/*</c> is a literal string that matches nothing.
/// </para>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>
    /// Every subject an RBAC (Cluster)RoleBinding grants <paramref name="query"/> to.
    /// Never throws for a listing this user may not read — that becomes a warning on the
    /// result, because a silently short list is the one thing worse than no list.
    /// </summary>
    public async Task<WhoCanResult> WhoCanAsync(AccessQuery query, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        // A namespaced query only needs that namespace's Roles/RoleBindings — cheaper,
        // and far more likely to be permitted than a cluster-wide list.
        var roleNamespace = query.Namespace;

        var clusterRoles = await TryListAsync(ResourceDescriptor.ClusterRoles, null, warnings, cancellationToken).ConfigureAwait(false);
        var clusterRoleBindings = await TryListAsync(ResourceDescriptor.ClusterRoleBindings, null, warnings, cancellationToken).ConfigureAwait(false);

        // A cluster-scoped resource (nodes, namespaces, CRDs…) can only ever be granted by
        // a ClusterRoleBinding: a RoleBinding confines even a ClusterRole to its namespace,
        // where a cluster-scoped object does not exist. Listing them would be pure noise.
        IReadOnlyList<DynamicResource> roles = [];
        IReadOnlyList<DynamicResource> roleBindings = [];
        if (!query.ClusterScopedResource)
        {
            roles = await TryListAsync(ResourceDescriptor.Roles, roleNamespace, warnings, cancellationToken).ConfigureAwait(false);
            roleBindings = await TryListAsync(ResourceDescriptor.RoleBindings, roleNamespace, warnings, cancellationToken).ConfigureAwait(false);
        }

        // Roles are indexed by the rules that actually match, so a binding lookup answers
        // "does this grant the query?" directly. A role with no matching rule is absent.
        var matchingClusterRoles = IndexMatchingRoles(clusterRoles, query, namespaced: false);
        var matchingRoles = IndexMatchingRoles(roles, query, namespaced: true);

        var bySubject = new Dictionary<SubjectRef, List<SubjectBinding>>();

        foreach (var binding in clusterRoleBindings)
        {
            var (roleKind, roleName) = ReadRoleRef(binding);
            if (roleName is null || roleKind != "ClusterRole"
                || !matchingClusterRoles.TryGetValue(RoleKey(null, roleName), out var rules))
            {
                continue;
            }

            AddGrant(bySubject, binding, bindingNamespace: null, "ClusterRoleBinding", roleKind, roleName, rules);
        }

        foreach (var binding in roleBindings)
        {
            // A RoleBinding's grant is confined to its own namespace, whichever kind of
            // role it references — so an all-namespaces query keeps them all, and a
            // namespaced one already listed only that namespace.
            var bindingNamespace = binding.Namespace;
            if (bindingNamespace is null)
            {
                continue;
            }

            var (roleKind, roleName) = ReadRoleRef(binding);
            if (roleName is null)
            {
                continue;
            }

            var index = roleKind == "Role" ? matchingRoles : matchingClusterRoles;
            var key = roleKind == "Role" ? RoleKey(bindingNamespace, roleName) : RoleKey(null, roleName);
            if (!index.TryGetValue(key, out var rules))
            {
                continue;
            }

            AddGrant(bySubject, binding, bindingNamespace, "RoleBinding", roleKind, roleName, rules);
        }

        var subjects = bySubject
            .Select(pair => new SubjectAccess(pair.Key, pair.Value))
            .OrderBy(s => s.Subject.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.Subject.Namespace ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Subject.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WhoCanResult(query, subjects, warnings);
    }

    /// <summary>
    /// The API server's own verdict on whether one subject may perform the query
    /// (<c>SubjectAccessReview</c>) — the authoritative counterpart to
    /// <see cref="WhoCanAsync"/>'s scan, and the only call here that consults every
    /// authorizer rather than RBAC alone.
    /// </summary>
    /// <remarks>
    /// Creating a SubjectAccessReview is itself a privileged operation; a caller without
    /// it gets an <see cref="HttpRequestException"/>, which callers surface per subject
    /// rather than failing the whole view.
    /// </remarks>
    public async Task<AccessDecision> CheckAccessAsync(
        SubjectRef subject, AccessQuery query, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        // Written with Utf8JsonWriter rather than JsonSerializer: serializing an untyped
        // value is the reflection path, which does not survive trimming/NativeAOT.
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", "authorization.k8s.io/v1");
            writer.WriteString("kind", "SubjectAccessReview");
            writer.WriteStartObject("spec");

            switch (subject.Kind)
            {
                case "ServiceAccount":
                    // ServiceAccounts authenticate as a user with this canonical name;
                    // the review has no serviceaccount-shaped field.
                    writer.WriteString("user", $"system:serviceaccount:{subject.Namespace}:{subject.Name}");
                    break;
                case "Group":
                    writer.WriteStartArray("groups");
                    writer.WriteStringValue(subject.Name);
                    writer.WriteEndArray();
                    break;
                default:
                    writer.WriteString("user", subject.Name);
                    break;
            }

            writer.WriteStartObject("resourceAttributes");
            writer.WriteString("verb", query.Verb);
            writer.WriteString("group", query.ApiGroup);
            writer.WriteString("resource", query.BaseResource);
            if (query.Subresource is { Length: > 0 } subresource)
            {
                writer.WriteString("subresource", subresource);
            }

            if (query.Namespace is { Length: > 0 } ns)
            {
                writer.WriteString("namespace", ns);
            }

            if (query.ResourceName is { Length: > 0 } name)
            {
                writer.WriteString("name", name);
            }

            writer.WriteEndObject(); // resourceAttributes
            writer.WriteEndObject(); // spec
            writer.WriteEndObject();
        }

        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await SendRequestAsync(
            HttpMethod.Post, "apis/authorization.k8s.io/v1/subjectaccessreviews", content,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return new AccessDecision(Allowed: false, Denied: false, Reason: null, EvaluationError: null);
        }

        return new AccessDecision(
            Allowed: status.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.True,
            Denied: status.TryGetProperty("denied", out var denied) && denied.ValueKind == JsonValueKind.True,
            Reason: status.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            EvaluationError: status.TryGetProperty("evaluationError", out var err) ? err.GetString() : null);
    }

    private async Task<IReadOnlyList<DynamicResource>> TryListAsync(
        ResourceDescriptor descriptor, string? @namespace, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await ListResourceOnceAsync(descriptor, @namespace, fieldSelector: null, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Not readable by this user (or gone). The scan continues on what is readable,
            // and says out loud which half it could not see.
            warnings.Add($"Could not list {descriptor.Plural}: {ex.Message}");
            return [];
        }
    }

    private static Dictionary<string, IReadOnlyList<PolicyRule>> IndexMatchingRoles(
        IReadOnlyList<DynamicResource> roles, AccessQuery query, bool namespaced)
    {
        var index = new Dictionary<string, IReadOnlyList<PolicyRule>>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            var matching = ReadRules(role.Raw, "rules").Where(rule => Matches(rule, query)).ToArray();
            if (matching.Length == 0)
            {
                continue;
            }

            index[RoleKey(namespaced ? role.Namespace : null, role.Name)] = matching;
        }

        return index;
    }

    private static string RoleKey(string? @namespace, string name) => $"{@namespace}/{name}";

    private static void AddGrant(
        Dictionary<SubjectRef, List<SubjectBinding>> bySubject,
        DynamicResource binding,
        string? bindingNamespace,
        string bindingKind,
        string roleKind,
        string roleName,
        IReadOnlyList<PolicyRule> rules)
    {
        foreach (var subject in ReadSubjects(binding))
        {
            if (!bySubject.TryGetValue(subject, out var grants))
            {
                grants = [];
                bySubject[subject] = grants;
            }

            grants.Add(new SubjectBinding(
                BindingKind: bindingKind,
                BindingName: binding.Name,
                BindingNamespace: bindingNamespace,
                RoleKind: roleKind,
                RoleName: roleName,
                Rules: rules));
        }
    }

    private static IEnumerable<SubjectRef> ReadSubjects(DynamicResource binding)
    {
        if (!binding.Raw.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var s in subjects.EnumerateArray())
        {
            var kind = s.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (kind is null || name is null)
            {
                continue;
            }

            // Only ServiceAccounts are namespaced subjects; carrying a namespace for a
            // User/Group would split one subject into several look-alike rows.
            var ns = kind == "ServiceAccount" && s.TryGetProperty("namespace", out var sn) ? sn.GetString() : null;
            yield return new SubjectRef(kind, name, ns);
        }
    }

    /// <summary>
    /// Whether one RBAC rule grants <paramref name="query"/>, using the API server's own
    /// matching semantics. Internal so the rules can be unit-tested without a cluster.
    /// </summary>
    internal static bool Matches(PolicyRule rule, AccessQuery query)
    {
        // A non-resource rule (nonResourceURLs) never grants a resource verb.
        if (rule.Resources.Count == 0)
        {
            return false;
        }

        if (!MatchesAny(rule.Verbs, query.Verb)
            || !MatchesAny(rule.ApiGroups, query.ApiGroup)
            || !MatchesAny(rule.Resources, query.Resource))
        {
            return false;
        }

        // An empty resourceNames means "every object of this kind". A non-empty one narrows
        // the grant: it only answers a named query, and for an unnamed query it is kept
        // (the subject really can act on those objects) with the names visible in the UI.
        if (rule.ResourceNames.Count == 0 || query.ResourceName is not { Length: > 0 })
        {
            return true;
        }

        return rule.ResourceNames.Contains(query.ResourceName, StringComparer.Ordinal);
    }

    private static bool MatchesAny(IReadOnlyList<string> candidates, string value)
    {
        foreach (var candidate in candidates)
        {
            // "*" is RBAC's only wildcard — it is whole-value, never a prefix or glob.
            if (candidate == "*" || string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One "can somebody do X?" question: a verb against a resource kind, optionally narrowed
/// to a single object and/or one namespace.
/// </summary>
/// <param name="Verb">An RBAC verb — get, list, watch, create, update, patch, delete, deletecollection…</param>
/// <param name="Resource">The plural resource, optionally with a subresource: "pods", "pods/log".</param>
/// <param name="ApiGroup">The API group; the empty string is the core group, as on the wire.</param>
/// <param name="ResourceName">A single object name, when the question is about one object.</param>
/// <param name="Namespace">The namespace to ask about; null asks across every namespace.</param>
/// <param name="ClusterScopedResource">
/// True for a cluster-scoped kind, which only a ClusterRoleBinding can ever grant.
/// </param>
public sealed record AccessQuery(
    string Verb,
    string Resource,
    string ApiGroup = "",
    string? ResourceName = null,
    string? Namespace = null,
    bool ClusterScopedResource = false)
{
    /// <summary>The resource without its subresource — the shape SubjectAccessReview wants.</summary>
    public string BaseResource
    {
        get
        {
            var slash = Resource.IndexOf('/');
            return slash < 0 ? Resource : Resource[..slash];
        }
    }

    /// <summary>The subresource ("log" of "pods/log"), or null when the query names none.</summary>
    public string? Subresource
    {
        get
        {
            var slash = Resource.IndexOf('/');
            return slash < 0 ? null : Resource[(slash + 1)..];
        }
    }

    /// <summary>kubectl-style rendering of the target: "pods", "deployments.apps", "pods/log".</summary>
    public string ResourceText => ApiGroup.Length == 0 ? Resource : $"{Resource}.{ApiGroup}";

    /// <summary>The whole question in one line, for a header or a palette subtitle.</summary>
    public string Text
    {
        get
        {
            var target = ResourceName is { Length: > 0 } name ? $"{ResourceText}/{name}" : ResourceText;
            var scope = Namespace is { Length: > 0 } ns ? $"in {ns}" : "in any namespace";
            return $"{Verb} {target} {scope}";
        }
    }
}

/// <summary>Every subject the RBAC scan found for one query, plus what it could not read.</summary>
public sealed record WhoCanResult(
    AccessQuery Query,
    IReadOnlyList<SubjectAccess> Subjects,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when part of the RBAC surface was unreadable, so the list may be short.</summary>
    public bool IsPartial => Warnings.Count > 0;
}

/// <summary>One subject, with every binding that grants it the query.</summary>
public sealed record SubjectAccess(SubjectRef Subject, IReadOnlyList<SubjectBinding> Bindings)
{
    /// <summary>"ServiceAccount payments:deploy-bot", "User alice", "Group dev".</summary>
    public string DisplayName => Subject.Namespace is { Length: > 0 } ns
        ? $"{Subject.Kind} {ns}:{Subject.Name}"
        : $"{Subject.Kind} {Subject.Name}";

    /// <summary>True when at least one grant is cluster-wide — the ones worth noticing first.</summary>
    public bool IsClusterWide => Bindings.Any(b => b.BindingNamespace is null);

    /// <summary>Where the access applies: "cluster-wide", or the namespaces that granted it.</summary>
    public string ScopeText => IsClusterWide
        ? "cluster-wide"
        : string.Join(", ", Bindings.Select(b => b.Scope).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

    /// <summary>True when a grant comes from a rule that hands out everything.</summary>
    public bool ViaWildcard => Bindings.Any(b => b.Rules.Any(r => r.IsWildcard));
}

/// <summary>The API server's answer for one subject (SubjectAccessReview).</summary>
/// <remarks>
/// <paramref name="Denied"/> is not merely "not allowed": it is an authorizer explicitly
/// denying, which matters because a plain "no" can simply mean nothing granted it.
/// </remarks>
public sealed record AccessDecision(bool Allowed, bool Denied, string? Reason, string? EvaluationError)
{
    public string Text => Allowed ? "allowed" : Denied ? "denied" : "not allowed";
}
