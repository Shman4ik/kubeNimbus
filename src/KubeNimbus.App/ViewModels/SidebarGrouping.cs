using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Buckets a discovered <see cref="ResourceDescriptor"/> into one of the five
/// fixed sidebar sections (CLAUDE.md UI rules — Workloads/Network/Config/
/// Storage/CRDs). Discovery still drives WHICH kinds exist; this only decides
/// which section a given kind's icon/row lands in, so CRDs are never hardcoded
/// — anything from a group this classifier doesn't recognize as built-in falls
/// through to CRDs automatically.
/// </summary>
public static class SidebarGrouping
{
    private static readonly HashSet<string> BuiltInGroups =
    [
        "", "apps", "batch", "autoscaling", "policy",
        "networking.k8s.io", "storage.k8s.io", "rbac.authorization.k8s.io",
        "coordination.k8s.io", "node.k8s.io", "scheduling.k8s.io",
        "discovery.k8s.io", "events.k8s.io", "certificates.k8s.io",
        "admissionregistration.k8s.io", "apiregistration.k8s.io",
        "apiextensions.k8s.io", "authentication.k8s.io", "authorization.k8s.io",
        "flowcontrol.apiserver.k8s.io",
    ];

    private static readonly HashSet<string> WorkloadKinds =
        ["Pod", "Deployment", "ReplicaSet", "StatefulSet", "DaemonSet", "Job", "CronJob", "ReplicationController"];

    private static readonly HashSet<string> NetworkKinds =
        ["Service", "Endpoints", "EndpointSlice", "Ingress", "IngressClass", "NetworkPolicy"];

    private static readonly HashSet<string> StorageKinds =
        ["PersistentVolume", "PersistentVolumeClaim", "StorageClass", "VolumeAttachment", "CSIDriver", "CSINode"];

    public static string SectionFor(ResourceDescriptor descriptor)
    {
        if (WorkloadKinds.Contains(descriptor.Kind))
        {
            return "Workloads";
        }

        if (NetworkKinds.Contains(descriptor.Kind))
        {
            return "Network";
        }

        if (StorageKinds.Contains(descriptor.Kind))
        {
            return "Storage";
        }

        return BuiltInGroups.Contains(descriptor.Group) ? "Config" : "CRDs";
    }

    public static readonly string[] SectionOrder = ["Workloads", "Network", "Config", "Storage", "CRDs"];

    /// <summary>Sidebar section for Helm releases — appended after the discovery-driven ones.</summary>
    public const string HelmSection = "Helm";

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
        "Workloads" => "CubeOutlineIconGeometry",
        "Network" => "LinkIconGeometry",
        "Config" => "TuneIconGeometry",
        "Storage" => "DatabaseIconGeometry",
        "CRDs" => "PuzzleIconGeometry",
        _ => "TagIconGeometry",
    };
}
