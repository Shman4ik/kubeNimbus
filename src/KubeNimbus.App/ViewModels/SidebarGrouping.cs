using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Buckets a discovered <see cref="ResourceDescriptor"/> into one of the six fixed
/// sidebar sections (Workloads/Network/Config/Storage/Cluster/CRDs). Discovery still
/// drives WHICH kinds exist; this only decides which section a given kind's icon/row
/// lands in, so CRDs are never hardcoded — anything from a group this classifier
/// doesn't recognize as built-in falls through to CRDs automatically, and that
/// property is deliberately preserved.
///
/// **Outside the core group, bucketing is by API group, never by a Kind allow-list.**
/// The old Kind-first rule is what turned Config into a junk drawer: it named the
/// kinds it wanted and dropped everything else recognized-but-unlisted into Config,
/// so a bare k3s opened on Workloads 8, Network 6 and **Config 33** — APIServices,
/// CertificateSigningRequests, ClusterRoles and the whole of flowcontrol,
/// admissionregistration, apiregistration and coordination, all filed as
/// "configuration". Group-driven bucketing has no such residue. It also stops a CRD
/// that happens to be called <c>Deployment</c> or <c>Job</c> from being classified as
/// a built-in workload, which the Kind-first rule did.
///
/// Kind matters only inside the core group (<c>""</c>), which is the one group that
/// holds workloads, networking, storage and cluster infrastructure at once — nothing
/// but the Kind can separate a Pod from a PersistentVolume there.
/// </summary>
public static class SidebarGrouping
{
    /// <summary>
    /// Cluster administration and API machinery: the section that exists so Config
    /// doesn't have to hold it. Collapsed by default — these are the kinds you go
    /// looking for deliberately, not the ones you browse.
    /// </summary>
    public const string ClusterSection = "Cluster";

    // --- core group (""), where the Kind is the only signal --------------------

    private static readonly HashSet<string> CoreWorkloadKinds =
        ["Pod", "ReplicationController", "PodTemplate"];

    private static readonly HashSet<string> CoreNetworkKinds =
        ["Service", "Endpoints"];

    private static readonly HashSet<string> CoreStorageKinds =
        ["PersistentVolume", "PersistentVolumeClaim"];

    /// <summary>
    /// Core kinds that describe the cluster itself rather than anything deployed on
    /// it. Nodes and Namespaces are infrastructure however often they're visited —
    /// filing them under "Config" was never defensible, and the sidebar's filter and
    /// Recent section are what make a collapsed section cheap to reach.
    /// </summary>
    private static readonly HashSet<string> CoreClusterKinds =
        ["Node", "Namespace", "ComponentStatus"];

    // --- everything else, by API group ---------------------------------------

    private static readonly HashSet<string> WorkloadGroups =
        ["apps", "batch", "autoscaling"];

    private static readonly HashSet<string> NetworkGroups =
        ["networking.k8s.io", "discovery.k8s.io"];

    private static readonly HashSet<string> StorageGroups =
        ["storage.k8s.io"];

    /// <summary>
    /// The groups that make up cluster administration: RBAC, admission, API
    /// registration/extension, scheduling, flow control, leases, certificates, and
    /// the aggregated metrics API (an aggregated API is machinery, not a CRD, so it
    /// belongs here rather than falling through to CRDs).
    /// </summary>
    private static readonly HashSet<string> ClusterGroups =
    [
        "rbac.authorization.k8s.io", "apiregistration.k8s.io",
        "admissionregistration.k8s.io", "flowcontrol.apiserver.k8s.io",
        "coordination.k8s.io", "certificates.k8s.io", "scheduling.k8s.io",
        "node.k8s.io", "authentication.k8s.io", "authorization.k8s.io",
        "apiextensions.k8s.io", "apiserverinternal.k8s.io",
        "storagemigration.k8s.io", "resource.k8s.io", "metrics.k8s.io",
    ];

    /// <summary>
    /// The only built-in groups left for Config: the alternate Event group and
    /// <c>policy</c> (PodDisruptionBudget — a declaration about a workload, not a
    /// controller of one, which is why it stays here while <c>autoscaling</c>'s HPA
    /// sits with what it scales). A built-in group missing from every set above would
    /// land in CRDs, so each one is placed deliberately.
    /// </summary>
    private static readonly HashSet<string> ConfigGroups =
        ["events.k8s.io", "policy"];

    public static string SectionFor(ResourceDescriptor descriptor)
    {
        if (descriptor.Group.Length == 0)
        {
            if (CoreWorkloadKinds.Contains(descriptor.Kind))
            {
                return "Workloads";
            }

            if (CoreNetworkKinds.Contains(descriptor.Kind))
            {
                return "Network";
            }

            if (CoreStorageKinds.Contains(descriptor.Kind))
            {
                return "Storage";
            }

            if (CoreClusterKinds.Contains(descriptor.Kind))
            {
                return ClusterSection;
            }

            // ConfigMap, Secret, Event, ServiceAccount, LimitRange, ResourceQuota…
            // — what "Config" is supposed to mean, and now all it holds from core.
            return "Config";
        }

        if (WorkloadGroups.Contains(descriptor.Group))
        {
            return "Workloads";
        }

        if (NetworkGroups.Contains(descriptor.Group))
        {
            return "Network";
        }

        if (StorageGroups.Contains(descriptor.Group))
        {
            return "Storage";
        }

        if (ClusterGroups.Contains(descriptor.Group))
        {
            return ClusterSection;
        }

        if (ArgoCd.IsArgoKind(descriptor))
        {
            return ArgoSection;
        }

        // Unrecognized group → CRDs, with nothing hardcoded about which CRDs exist.
        return ConfigGroups.Contains(descriptor.Group) ? "Config" : "CRDs";
    }

    /// <summary>
    /// Argo's own section, so its kinds stop being ~8 more rows in a CRDs section that
    /// already runs past a hundred. It is the same group-driven bucketing every other
    /// section uses — <c>argoproj.io</c> is one API group — rather than a special case for
    /// one product, and a cluster with no Argo installed never sees it (a section with no
    /// kinds is not added).
    ///
    /// <para>
    /// The group is Argo's <em>whole</em> family, so a cluster running Argo Rollouts or Argo
    /// Workflows lands their kinds here too. That is why the section is "Argo" and the
    /// dashboard row inside it is "Argo CD": filing a Workflow under a heading that says
    /// Argo CD would be wrong, and filing it back in CRDs would be worse.
    /// </para>
    /// </summary>
    public const string ArgoSection = "Argo";

    public static readonly string[] SectionOrder =
        ["Workloads", "Network", "Config", "Storage", ClusterSection, ArgoSection, "CRDs"];

    /// <summary>
    /// Which sections a fresh connection opens expanded. Config, Cluster and CRDs
    /// start collapsed: measured on a bare k3s the discovery catalog is ~50 kinds
    /// before a single CRD is installed and ~130 on a real cluster, so expanding
    /// everything means opening on a wall of rows nobody scrolls. The three that
    /// stay open — Workloads, Network, Storage — are ~20 rows together and cover
    /// what a session actually starts on.
    /// </summary>
    public static bool IsExpandedByDefault(string section) =>
        section is not ("Config" or ClusterSection or "CRDs");

    /// <summary>Sidebar section for Helm releases — appended after the discovery-driven ones.</summary>
    public const string HelmSection = "Helm";

    /// <summary>
    /// Recently selected kinds, pinned above the discovery-driven sections. Not part
    /// of <see cref="SectionOrder"/>: nothing is ever classified *into* it — it holds
    /// second instances of kinds that also live in their real section, inserted at the
    /// top of the sidebar so a 100+ kind catalog doesn't have to be re-navigated to get
    /// back to the three kinds you're actually working with.
    /// </summary>
    public const string RecentSection = "Recent";

    /// <summary>
    /// Synthetic descriptor for the Helm sidebar entry. Helm releases are NOT an
    /// API kind (they're Secrets of type helm.sh/release.v1), so discovery will
    /// never produce this; it exists so the Helm entry can reuse
    /// <see cref="SidebarKindViewModel"/> like every other row. The bogus group
    /// is what <see cref="SidebarKindViewModel.IsHelmReleases"/> keys off, and it
    /// can never collide with a real one (Kubernetes API groups are DNS names,
    /// and no server serves "helm.sh").
    /// </summary>
    public static readonly ResourceDescriptor HelmReleaseDescriptor =
        new("helm.sh", "v1", "Release", "releases", "release", Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>
    /// Synthetic descriptor for the Argo CD dashboard row, which sits at the top of the Argo
    /// section above that cluster's real Argo kinds. Unlike the Helm entry it is not standing
    /// in for something that isn't an API kind — Applications very much are, and they have
    /// their own row right underneath. It exists because the <em>dashboard</em> is not a list
    /// of one kind: it is every Application on the cluster, counted and ordered by what needs
    /// looking at, which is a different question from "show me the Applications in this
    /// namespace" and is the one Argo CD's own UI opens on.
    ///
    /// <para>
    /// The empty plural is deliberate: <see cref="SidebarKindViewModel.Pluralize"/> falls back
    /// to the Kind when there is nothing to re-case against, so the row reads "Argo CD" rather
    /// than a pluralized version of it. The group is real, so the sidebar filter finds this
    /// row by typing "argo" exactly as it finds the kinds below it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Declared cluster-scoped, and that is the one field here doing real work: the content
    /// header's namespace picker binds its <c>IsEnabled</c> to the selected kind's
    /// <c>Namespaced</c>, and the dashboard is cluster-wide (Applications live in one
    /// namespace while what they manage is spread across the rest). So the picker greys
    /// itself out with no extra binding, rather than offering a choice that changes nothing.
    /// </remarks>
    public static readonly ResourceDescriptor ArgoDashboardDescriptor =
        new(ArgoCd.Group, "v1alpha1", "Argo CD", "", "", Namespaced: false, ShortNames: [], Categories: []);

    /// <summary>
    /// Puts the Argo CD dashboard at the top of the Argo section, on a cluster that serves
    /// the Application kind. Gated on the kind rather than on the group, and that difference
    /// is the point: Argo Rollouts and Argo Workflows are also <c>argoproj.io</c>, so a
    /// cluster running one of those has an Argo section with no Argo CD in it — and a GitOps
    /// dashboard over a cluster with no Applications would be a row that opens on nothing.
    ///
    /// <para>
    /// No probe and no extra request: the discovery catalog has already been fetched for the
    /// sidebar, so this is the same evidence the metrics gate and the CRD printer columns use.
    /// A cluster where Argo CD is installed <em>after</em> the tab connected picks the row up
    /// on the next reconnect, exactly as the Helm section does.
    /// </para>
    /// </summary>
    public static void AddArgoDashboard(
        IEnumerable<SidebarSectionViewModel> sections, IEnumerable<ResourceDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (ArgoCd.ApplicationDescriptor(catalog) is null)
        {
            return;
        }

        if (sections.FirstOrDefault(s => s.Title == ArgoSection) is not { } section)
        {
            return;
        }

        section.Kinds.Insert(0, new SidebarKindViewModel(ArgoDashboardDescriptor, IconKeyFor(ArgoSection)));
    }

    /// <summary>
    /// Labels same-named kinds within a section with their API group. On a real
    /// cluster the CRDs section routinely holds several kinds sharing a name
    /// (Backup from velero.io and from postgresql.cnpg.io; Cluster from
    /// cluster.x-k8s.io and from postgresql.cnpg.io) — without this they render
    /// as identical rows that select different resources. Unambiguous kinds keep
    /// an empty label so the common case stays uncluttered.
    /// </summary>
    public static void LabelAmbiguousKinds(IEnumerable<SidebarSectionViewModel> sections)
    {
        foreach (var section in sections)
        {
            foreach (var duplicates in section.Kinds
                .GroupBy(k => k.Descriptor.Kind, StringComparer.Ordinal)
                .Where(g => g.Count() > 1))
            {
                foreach (var kind in duplicates)
                {
                    kind.GroupLabel = kind.Descriptor.Group.Length > 0 ? kind.Descriptor.Group : "core";
                }
            }
        }
    }

    public static string IconKeyFor(string section) => section switch
    {
        HelmSection => "LayersIconGeometry",
        ArgoSection => "SourceBranchIconGeometry",
        RecentSection => "ClockOutlineIconGeometry",
        "Workloads" => "CubeOutlineIconGeometry",
        "Network" => "LinkIconGeometry",
        "Config" => "TuneIconGeometry",
        "Storage" => "DatabaseIconGeometry",
        ClusterSection => "CogIconGeometry",
        "CRDs" => "PuzzleIconGeometry",
        _ => "TagIconGeometry",
    };

    /// <summary>
    /// Per-kind icon override for a handful of built-ins that read better with
    /// their own glyph than their section's generic one — core/v1 Event falls
    /// into the Config bucket by group but a bell reads as "events" far better
    /// than Config's generic tune icon. Everything else keeps the section icon.
    /// </summary>
    public static string IconKeyFor(ResourceDescriptor descriptor, string section) => descriptor switch
    {
        { Group: "", Kind: "Event" } => "BellIconGeometry",
        _ => IconKeyFor(section),
    };
}
