namespace KubeNimbus.Core.Applications;

/// <summary>
/// Which namespaces the Applications list watches when it may not list the cluster.
/// </summary>
/// <remarks>
/// The list tries one cluster-wide list+watch per kind first. Plenty of real RBAC grants a
/// user a handful of namespaces and nothing else — no <c>list namespaces</c>, no cluster-wide
/// list — and a 403 there must not end the mode. The fallback watches, per namespace, every
/// namespace the tab already has a reason to believe in: the kubeconfig context's own
/// namespace (what kubectl would use), the namespaces the user recently picked, every Argo
/// CD Application's destination namespace (Applications are read from their own namespace,
/// which the user may be allowed to list even when the rest is closed), and the namespace
/// selected in Resources mode. The list then says which namespaces it covers and which were
/// refused — a silently partial list would read as "everything is fine" exactly where it is
/// not looking.
/// </remarks>
public static class ApplicationScope
{
    public static IReadOnlyList<string> CandidateNamespaces(
        string? contextNamespace,
        IEnumerable<string> recentNamespaces,
        IEnumerable<ArgoApplication> argoApplications,
        string? selectedNamespace)
    {
        var result = new List<string>();
        void Add(string? ns)
        {
            if (!string.IsNullOrWhiteSpace(ns) && !result.Contains(ns, StringComparer.Ordinal))
            {
                result.Add(ns);
            }
        }

        Add(contextNamespace);
        Add(selectedNamespace);
        foreach (var ns in recentNamespaces)
        {
            Add(ns);
        }

        foreach (var app in argoApplications)
        {
            Add(app.DestinationNamespace);
        }

        return result;
    }

    /// <summary>
    /// Whether a namespace is one of Kubernetes' own (<c>kube-system</c>, <c>kube-public</c>,
    /// <c>kube-node-lease</c>, and anything else under the <c>kube-</c> prefix) — hidden from
    /// the list by default behind a chip that states their count.
    /// </summary>
    public static bool IsSystemNamespace(string @namespace) =>
        @namespace.StartsWith("kube-", StringComparison.Ordinal);
}

/// <summary>
/// The one Argo CD fact that is not in the Kubernetes API's shape of an Application: where
/// Argo's own UI is. <c>argocd-cm</c>'s <c>data.url</c> is the external URL Argo is
/// configured with (it is what its SSO redirects use), so when that ConfigMap is readable
/// the page can offer "Open in Argo CD"; when it is not, the action is hidden rather than
/// guessed at.
/// </summary>
public static class ArgoUi
{
    public const string ConfigMapName = "argocd-cm";

    /// <summary>
    /// The namespace <c>argocd-cm</c> is read from for <paramref name="app"/>: Argo CD's own,
    /// never one an Application's author picked. Null when that cannot be settled, and then
    /// the page offers no link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ConfigMap used to be read from the Application's own namespace. With Argo's
    /// "applications in any namespace" a tenant who can create an Application in their
    /// namespace can also create a ConfigMap there called <c>argocd-cm</c>, with a
    /// <c>data.url</c> of their choosing, and the button opened it in the operator's browser.
    /// </para>
    /// <para>
    /// Argo CD 2.5+ writes <c>status.controllerNamespace</c> on every Application it
    /// reconciles. That is the claim read first; before 2.5 there is no field, and Argo only
    /// reconciled Applications in its own namespace, so the Application's namespace is the
    /// claim then. But the CRD has no status subresource, so whoever writes an Application
    /// writes its status too, and a claim is checked against the cluster's other
    /// Applications: an Application that names a controller namespace other than its own is
    /// evidence of where Argo runs (a tenant gains nothing by naming somebody else's
    /// namespace). When there is such evidence from the <em>other</em> Applications in view,
    /// the claim must be in it; when there is none (the classic setup, every Application in
    /// Argo's namespace), the claim stands. A claim that disagrees with the evidence gives no
    /// link rather than a guess — one Argo instance's link on another's Application would be
    /// wrong too.
    /// </para>
    /// <para>
    /// What this cannot catch, stated: an author with nothing else in view to compare against
    /// (the only Applications the operator can see are theirs), or one who can write in two
    /// namespaces and names one from the other. The button's tooltip names the host either
    /// way, which is the last check, and only http and https are opened.
    /// </para>
    /// </remarks>
    public static string? ConfigMapNamespace(ArgoApplication app, IEnumerable<ArgoApplication> visible)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(visible);
        var claimed = Claim(app);
        if (claimed is null)
        {
            return null;
        }

        var evidence = visible
            .Where(other => !string.Equals(other.Key, app.Key, StringComparison.Ordinal))
            .Select(other => (Claim: Claim(other), other.Namespace))
            .Where(other => other.Claim is not null && !string.Equals(other.Claim, other.Namespace, StringComparison.Ordinal))
            .Select(other => other.Claim!)
            .ToHashSet(StringComparer.Ordinal);

        return evidence.Count == 0 || evidence.Contains(claimed) ? claimed : null;
    }

    /// <summary>The controller namespace an Application claims: its status field, else its own namespace.</summary>
    private static string? Claim(ArgoApplication app)
    {
        var claimed = app.ControllerNamespace.Length > 0 ? app.ControllerNamespace : app.Namespace;
        return ResourceDescriptor.IsValidPathSegment(claimed) ? claimed : null;
    }

    public static Uri? BaseUrl(DynamicResource? configMap)
    {
        if (configMap is null)
        {
            return null;
        }

        var url = J.Str(J.Obj(configMap.Raw, "data"), "url").Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
    }

    /// <summary><c>&lt;base&gt;/applications/&lt;app namespace&gt;/&lt;name&gt;</c> — the path Argo CD 2.5+ serves for every Application.</summary>
    public static Uri ApplicationUrl(Uri baseUrl, ArgoApplication app)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(app);
        var root = baseUrl.AbsoluteUri.TrimEnd('/');
        return new Uri($"{root}/applications/{Uri.EscapeDataString(app.Namespace)}/{Uri.EscapeDataString(app.Name)}");
    }
}
