using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// Static fixture data for the headless screenshot harness — this environment has
/// no Docker daemon, so scenarios are built from checked-in JSON (real-shaped
/// Kubernetes objects) instead of a live k3s sandbox. See CLAUDE.md for how to run
/// this against a real cluster once Docker is available locally.
/// </summary>
internal static class FixtureData
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // Kept alive for the process lifetime: DynamicResource wraps JsonElements that
    // stay valid only as long as their parent JsonDocument is undisposed.
    private static readonly JsonDocument PodsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "pods.json")));
    private static readonly JsonDocument DeploymentsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "deployments.json")));
    private static readonly JsonDocument EventsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "events.json")));

    public static IReadOnlyList<DynamicResource> Pods { get; } =
        [.. PodsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    public static IReadOnlyList<DynamicResource> Deployments { get; } =
        [.. DeploymentsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    public static IReadOnlyList<DynamicResource> Events { get; } =
        [.. EventsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    public static readonly string[] Namespaces = ["default", "kube-system", "monitoring", "payments"];

    /// <summary>
    /// Helm releases as they'd come back from decoding release Secrets — built as
    /// records rather than fixture JSON, since the decoding itself is covered by
    /// unit tests and what this feeds is only the list rendering.
    /// </summary>
    public static IReadOnlyList<HelmRelease> HelmReleases { get; } =
    [
        new("checkout", "payments", 7, "deployed", "checkout", "1.4.2", "2.14.3",
            new DateTimeOffset(2026, 7, 20, 8, 41, 2, TimeSpan.Zero), "Upgrade complete"),
        new("payments-db", "payments", 3, "deployed", "postgresql", "15.5.1", "16.2.0",
            new DateTimeOffset(2026, 7, 14, 17, 3, 44, TimeSpan.Zero), "Upgrade complete"),
        new("ingress-nginx", "payments", 2, "failed", "ingress-nginx", "4.11.0", "1.11.1",
            new DateTimeOffset(2026, 7, 11, 9, 22, 10, TimeSpan.Zero), "timed out waiting for the condition"),
        new("kube-prometheus-stack", "payments", 12, "pending-upgrade", "kube-prometheus-stack", "62.3.0", "0.75.1",
            new DateTimeOffset(2026, 7, 9, 12, 0, 5, TimeSpan.Zero), "Preparing upgrade"),
        new("cert-manager", "payments", 4, "superseded", "cert-manager", "1.15.2", "1.15.2",
            new DateTimeOffset(2026, 6, 28, 6, 15, 41, TimeSpan.Zero), "Superseded by revision 5"),
    ];

    /// <summary>A realistic full catalog: built-ins across all four core sections plus ~70 CRD kinds.</summary>
    public static IReadOnlyList<ResourceDescriptor> BuildCatalog()
    {
        var result = new List<ResourceDescriptor>
        {
            ResourceDescriptor.Pods,
            Descriptor("", "v1", "Service", "services", true),
            Descriptor("apps", "v1", "Deployment", "deployments", true),
            Descriptor("apps", "v1", "ReplicaSet", "replicasets", true),
            Descriptor("apps", "v1", "StatefulSet", "statefulsets", true),
            Descriptor("apps", "v1", "DaemonSet", "daemonsets", true),
            Descriptor("batch", "v1", "Job", "jobs", true),
            Descriptor("batch", "v1", "CronJob", "cronjobs", true),
            Descriptor("", "v1", "Endpoints", "endpoints", true),
            Descriptor("networking.k8s.io", "v1", "Ingress", "ingresses", true),
            Descriptor("networking.k8s.io", "v1", "NetworkPolicy", "networkpolicies", true),
            Descriptor("", "v1", "ConfigMap", "configmaps", true),
            Descriptor("", "v1", "Secret", "secrets", true),
            Descriptor("", "v1", "ServiceAccount", "serviceaccounts", true),
            Descriptor("rbac.authorization.k8s.io", "v1", "Role", "roles", true),
            Descriptor("rbac.authorization.k8s.io", "v1", "RoleBinding", "rolebindings", true),
            Descriptor("", "v1", "PersistentVolumeClaim", "persistentvolumeclaims", true),
            Descriptor("", "v1", "PersistentVolume", "persistentvolumes", false),
            Descriptor("storage.k8s.io", "v1", "StorageClass", "storageclasses", false),
        };

        foreach (var crd in LoadCrdCatalog())
        {
            result.Add(crd);
        }

        return result;
    }

    private static IEnumerable<ResourceDescriptor> LoadCrdCatalog()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "crd-catalog.json")));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var group = entry.GetProperty("group").GetString() ?? "";
            var kind = entry.GetProperty("kind").GetString() ?? "";
            var plural = entry.GetProperty("plural").GetString() ?? "";
            var namespaced = !kind.StartsWith("Cluster", StringComparison.Ordinal) && kind != "GatewayClass" && kind != "StorageClass";
            yield return Descriptor(group, "v1", kind, plural, namespaced);
        }
    }

    private static ResourceDescriptor Descriptor(string group, string version, string kind, string plural, bool namespaced) =>
        new(group, version, kind, plural, kind.ToLowerInvariant(), namespaced, [], []);

    /// <summary>
    /// Offline ClusterClient: points at an unreachable local port so construction
    /// never touches the network, but real objects still exist to satisfy
    /// ViewModel constructors that expect a live ClusterClient. Background calls
    /// that do fire (event refresh, exec connect) fail fast and are swallowed by
    /// the same error handling the app already has for a lost connection.
    /// </summary>
    public static ClusterClient CreateOfflineClient()
    {
        var kubeconfigPath = Path.Combine(FixturesDir, "kubeconfig-fake.yaml");
        var context = new ClusterContext("fixture-cluster", "fake-cluster", "payments", "fake-user", kubeconfigPath);
        return ClusterClient.Connect(context);
    }

    public static SidebarSectionViewModel[] BuildSidebarSections(IReadOnlyList<ResourceDescriptor> catalog)
    {
        var sections = new Dictionary<string, SidebarSectionViewModel>(StringComparer.Ordinal);
        foreach (var title in SidebarGrouping.SectionOrder)
        {
            sections[title] = new SidebarSectionViewModel(title);
        }

        foreach (var descriptor in catalog.OrderBy(d => d.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var title = SidebarGrouping.SectionFor(descriptor);
            sections[title].Kinds.Add(new SidebarKindViewModel(descriptor, SidebarGrouping.IconKeyFor(title)));
        }

        return [.. SidebarGrouping.SectionOrder.Select(t => sections[t]).Where(s => s.Kinds.Count > 0)];
    }
}
