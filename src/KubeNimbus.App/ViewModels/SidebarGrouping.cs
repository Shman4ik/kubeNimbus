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

    public static string IconKeyFor(string section) => section switch
    {
        "Workloads" => "CubeOutlineIconGeometry",
        "Network" => "LinkIconGeometry",
        "Config" => "TuneIconGeometry",
        "Storage" => "DatabaseIconGeometry",
        "CRDs" => "PuzzleIconGeometry",
        _ => "TagIconGeometry",
    };
}
