using System.Reflection;
using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Demo;

/// <summary>
/// The demo cluster's dataset: realistically-shaped Kubernetes objects that never
/// came from a cluster and never will. It exists so that someone with no kubeconfig
/// and no cluster — a Microsoft Store reviewer on a clean machine, or anyone
/// evaluating the app before wiring up credentials — can see kubeNimbus actually
/// work. See CLAUDE.md's "Demo cluster" section for the rules this dataset is under.
///
/// It is also what the headless screenshot harness renders
/// (<c>tools/Screenshot/FixtureData.cs</c> delegates here): one dataset, not two,
/// so a scenario and the shipping demo cluster cannot drift apart.
/// </summary>
/// <remarks>
/// Loading rules, all load-bearing:
/// <list type="bullet">
/// <item>The JSON is an <c>EmbeddedResource</c> with an explicit <c>LogicalName</c>
/// (see the csproj) and is read through <see cref="Assembly.GetManifestResourceStream(string)"/>.
/// The explicit name is what makes the lookup survive the assembly being called
/// <c>kubeNimbus</c> rather than <c>KubeNimbus.App</c> — the same reason
/// <c>Yaml-Mode.xshd</c> is declared that way. A <c>Fixtures/</c> directory next
/// to the exe would also break the single-file NativeAOT publish.</item>
/// <item>The <see cref="JsonDocument"/>s are kept alive for the process lifetime:
/// <see cref="DynamicResource"/> wraps <see cref="JsonElement"/>s that stay valid
/// only while their parent document is undisposed.</item>
/// <item><c>System.Text.Json</c> only, and only <c>JsonDocument</c> — no
/// reflection-based deserialization, because NativeAOT is the shipping build.</item>
/// </list>
/// </remarks>
public static class DemoData
{
    // Kept alive for the process lifetime: DynamicResource wraps JsonElements that
    // stay valid only as long as their parent JsonDocument is undisposed.
    private static readonly JsonDocument PodsDoc = Load("pods.json");
    private static readonly JsonDocument DeploymentsDoc = Load("deployments.json");
    private static readonly JsonDocument EventsDoc = Load("events.json");
    private static readonly JsonDocument PodMetricsDoc = Load("pod-metrics.json");
    private static readonly JsonDocument SecretDoc = Load("secret.json");
    private static readonly JsonDocument ConfigMapsDoc = Load("configmaps.json");
    private static readonly JsonDocument CrdCatalogDoc = Load("crd-catalog.json");
    private static readonly JsonDocument CrdsDoc = Load("crds.json");
    private static readonly JsonDocument CertificatesDoc = Load("certificates.json");
    private static readonly JsonDocument NodesDoc = Load("nodes.json");

    private static JsonDocument Load(string fileName)
    {
        var resourceName = $"Demo.{fileName}";
        using var stream = typeof(DemoData).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Demo dataset resource \"{resourceName}\" is missing from the assembly. "
                + "It is declared in KubeNimbus.App.csproj with an explicit LogicalName; check that first.");
        return JsonDocument.Parse(stream);
    }

    public static IReadOnlyList<DynamicResource> Pods { get; } =
        [.. PodsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    public static IReadOnlyList<DynamicResource> Deployments { get; } =
        [.. DeploymentsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    public static IReadOnlyList<DynamicResource> Events { get; } =
        [.. EventsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    /// <summary>
    /// The demo cluster's three nodes: a tainted control plane, a healthy worker, and a
    /// worker that is cordoned and reporting disk pressure. The third one exists because
    /// it is the state the node surface is for — something is wrong with the machine,
    /// scheduling has been stopped on it, and the question is whether its pods can be
    /// moved. They also carry capacity and allocatable, which is what makes the
    /// allocatable-vs-requested arithmetic in the detail pane show anything at all.
    /// </summary>
    public static IReadOnlyList<DynamicResource> Nodes { get; } =
        [.. NodesDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    /// <summary>
    /// metrics.k8s.io NodeMetrics for the three demo nodes. Built as records rather
    /// than fixture JSON for the same reason <see cref="HelmReleases"/> is: the parsing
    /// is covered by unit tests and what this feeds is only the rendering. Without it a
    /// demo node list in the advanced view would be a CPU/Memory column of nothing but
    /// gaps, which reads as a broken metrics-server rather than as a demo.
    /// </summary>
    public static IReadOnlyList<NodeMetrics> NodeUsage { get; } =
    [
        new("demo-cp-1", 412_000_000L, 2_147_483_648L),
        new("demo-worker-1", 1_930_000_000L, 6_442_450_944L),
        new("demo-worker-2", 640_000_000L, 3_221_225_472L),
    ];

    /// <summary>metrics.k8s.io PodMetrics — obviously-fake usage numbers, one entry per running pod in <see cref="Pods"/>.</summary>
    public static IReadOnlyList<DynamicResource> PodMetrics { get; } =
        [.. PodMetricsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    /// <summary>
    /// A Secret with obviously-fake base64 placeholder data (see the file's own
    /// comment). Backs both the YAML editor's reveal-values panel and the demo
    /// Environment tab's <c>secretKeyRef</c> reveal, which resolves against this
    /// object instead of an API server.
    /// </summary>
    public static DynamicResource Secret { get; } = new(SecretDoc.RootElement[0]);

    /// <summary>
    /// The ConfigMaps the demo pods reference. Pod detail resolves
    /// <c>configMapKeyRef</c>s on open, so without these the Environment tab's most
    /// visible behaviour would land on a per-row error in the one place a reviewer is
    /// most likely to look.
    /// </summary>
    public static IReadOnlyList<DynamicResource> ConfigMaps { get; } =
        [.. ConfigMapsDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    /// <summary>
    /// cert-manager Certificates — the demo cluster's custom resources, and the one
    /// kind here whose list columns come from a CRD rather than from
    /// <c>ResourceStatusSummary</c>. They exist so the demo shows what every CRD-heavy
    /// real cluster shows: READY / SECRET (and, in the advanced view, the CRD's own
    /// <c>priority: 1</c> ISSUER and STATUS), exactly as <c>kubectl get certificates</c>
    /// prints them.
    /// </summary>
    public static IReadOnlyList<DynamicResource> Certificates { get; } =
        [.. CertificatesDoc.RootElement.EnumerateArray().Select(e => new DynamicResource(e))];

    /// <summary>
    /// The demo cluster's stand-in for <c>ClusterClient.GetPrinterColumnsAsync</c>: it
    /// has no API server to GET a CustomResourceDefinition from, so the CRD ships in
    /// the dataset instead — and is read by the same <see cref="PrinterColumns.Parse"/>
    /// a live cluster's response goes through. Empty for every kind the dataset has no
    /// CRD for, which is the same answer a real server gives for a built-in.
    /// </summary>
    public static IReadOnlyList<PrinterColumn> PrinterColumnsFor(ResourceDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Group))
        {
            return [];
        }

        foreach (var crd in CrdsDoc.RootElement.EnumerateArray())
        {
            if (!crd.TryGetProperty("spec", out var spec))
            {
                continue;
            }

            var group = spec.TryGetProperty("group", out var g) ? g.GetString() : null;
            var plural = spec.TryGetProperty("names", out var names) && names.TryGetProperty("plural", out var p)
                ? p.GetString()
                : null;

            if (string.Equals(group, descriptor.Group, StringComparison.Ordinal)
                && string.Equals(plural, descriptor.Plural, StringComparison.Ordinal))
            {
                return PrinterColumns.Parse(crd, descriptor.Version);
            }
        }

        return [];
    }

    public static readonly string[] Namespaces = ["default", "kube-system", "monitoring", "payments"];

    /// <summary>
    /// Stands in for a GET against the API server. Used by the demo Environment tab's
    /// Secret/ConfigMap resolution, which otherwise goes through
    /// <c>ClusterClient.ReadResourceAsync</c> — the demo tab has no client, so this is
    /// the whole of its "read one object" surface. Null for anything the dataset does
    /// not carry, which the caller already renders as a per-row "not found".
    /// </summary>
    public static DynamicResource? ReadObject(string kind, string? @namespace, string name)
    {
        var candidates = kind switch
        {
            "Secret" => (IReadOnlyList<DynamicResource>)[Secret],
            "ConfigMap" => ConfigMaps,
            _ => [],
        };

        return candidates.FirstOrDefault(r =>
            string.Equals(r.Name, name, StringComparison.Ordinal)
            && (@namespace is null || string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)));
    }

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

    /// <summary>
    /// One release's values / rendered manifest / notes, standing in for
    /// <c>ClusterClient.GetHelmReleaseAsync</c>. Generic where it can be and specific
    /// where it matters: the checkout release is the one the sidebar opens on, so it
    /// is the one written out in full.
    /// </summary>
    public static (string ValuesYaml, string Manifest, string Notes) HelmDetail(string releaseName)
    {
        var release = HelmReleases.FirstOrDefault(r => r.Name == releaseName) ?? HelmReleases[0];
        var values = releaseName == "checkout"
            ? """
              replicaCount: 3
              image:
                repository: registry.internal/payments/checkout
                tag: "9.0.1"
              resources:
                requests:
                  cpu: 250m
                  memory: 256Mi
              ingress:
                enabled: true
                host: checkout.payments.internal
              """
            : $"""
               # {release.Name} was installed with no user-supplied overrides beyond these.
               replicaCount: 2
               image:
                 tag: "{release.AppVersion}"
               """;

        var manifest = $"""
            # Source: {release.ChartName}/templates/deployment.yaml
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {release.Name}
              namespace: {release.Namespace}
              labels:
                app.kubernetes.io/managed-by: Helm
                helm.sh/chart: {release.ChartName}-{release.ChartVersion}
            spec:
              replicas: 3
              template:
                spec:
                  containers:
                    - name: {release.Name}
                      image: registry.internal/payments/{release.Name}:{release.AppVersion}
            """;

        var notes = $"""
            {release.Name} has been installed.

            Get the application URL:
              kubectl -n {release.Namespace} port-forward svc/{release.Name} 8080:80
            """;

        return (values, manifest, notes);
    }

    /// <summary>
    /// A release's revision history, standing in for
    /// <c>ClusterClient.GetHelmReleaseHistoryAsync</c>. Synthesized down from the
    /// current revision rather than listed by hand, so the history is consistent with
    /// whatever <see cref="HelmReleases"/> says the release is at — and so the pane's
    /// "click an older revision" gesture has something to land on.
    /// </summary>
    public static IReadOnlyList<HelmRelease> HelmHistory(string releaseName)
    {
        var current = HelmReleases.FirstOrDefault(r => r.Name == releaseName) ?? HelmReleases[0];
        var history = new List<HelmRelease>();
        for (var revision = current.Revision; revision > 0 && history.Count < 5; revision--)
        {
            history.Add(revision == current.Revision
                ? current
                : current with
                {
                    Revision = revision,
                    Status = "superseded",
                    Updated = current.Updated?.AddDays(-(current.Revision - revision) * 6),
                    Description = $"Superseded by revision {revision + 1}",
                });
        }

        return history;
    }

    /// <summary>
    /// What the demo cluster's "discovery" returns. A realistic full catalog: built-ins
    /// across every discovery-driven section plus ~70 CRD kinds. The cluster-administration
    /// half is as complete as a bare cluster's really is — that is the whole point. A
    /// handful of tidy built-ins hid the fact that <c>SidebarGrouping</c> was dumping
    /// APIServices, CSRs, flowcontrol and admission webhooks into Config, which is what a
    /// live k3s (Workloads 8, Network 6, Config 33) made obvious the moment it was counted.
    /// </summary>
    public static IReadOnlyList<ResourceDescriptor> BuildCatalog()
    {
        var result = new List<ResourceDescriptor>
        {
            // The Pod descriptor carries `eviction` for the same reason the three
            // scalable kinds below carry `scale`: capability comes from discovery, so
            // without it the demo cluster would hide Drain outright and teach that
            // kubeNimbus cannot drain a node, rather than that *this* cluster cannot.
            ResourceDescriptor.Pods with { Subresources = ["eviction", "log", "exec", "status"] },
            Descriptor("", "v1", "Service", "services", true),
            // The three kinds a real server declares a `scale` subresource for, carried
            // here too: capability comes from discovery (see WorkloadActions), so
            // without it the demo cluster would hide the Scale action outright instead
            // of offering it and explaining — in place — that there is no API server
            // behind it. Same argument as the exec and port-forward panes.
            Descriptor("apps", "v1", "Deployment", "deployments", true, scalable: true),
            Descriptor("apps", "v1", "ReplicaSet", "replicasets", true, scalable: true),
            Descriptor("apps", "v1", "StatefulSet", "statefulsets", true, scalable: true),
            Descriptor("apps", "v1", "DaemonSet", "daemonsets", true),
            Descriptor("batch", "v1", "Job", "jobs", true),
            Descriptor("batch", "v1", "CronJob", "cronjobs", true),
            Descriptor("autoscaling", "v2", "HorizontalPodAutoscaler", "horizontalpodautoscalers", true),
            Descriptor("", "v1", "Endpoints", "endpoints", true),
            Descriptor("discovery.k8s.io", "v1", "EndpointSlice", "endpointslices", true),
            Descriptor("networking.k8s.io", "v1", "Ingress", "ingresses", true),
            Descriptor("networking.k8s.io", "v1", "IngressClass", "ingressclasses", false),
            Descriptor("networking.k8s.io", "v1", "NetworkPolicy", "networkpolicies", true),
            Descriptor("", "v1", "ConfigMap", "configmaps", true),
            Descriptor("", "v1", "Secret", "secrets", true),
            Descriptor("", "v1", "Event", "events", true),
            Descriptor("", "v1", "ServiceAccount", "serviceaccounts", true),
            Descriptor("", "v1", "LimitRange", "limitranges", true),
            Descriptor("", "v1", "ResourceQuota", "resourcequotas", true),
            Descriptor("policy", "v1", "PodDisruptionBudget", "poddisruptionbudgets", true),
            Descriptor("", "v1", "PersistentVolumeClaim", "persistentvolumeclaims", true),
            Descriptor("", "v1", "PersistentVolume", "persistentvolumes", false),
            Descriptor("storage.k8s.io", "v1", "StorageClass", "storageclasses", false),
            Descriptor("storage.k8s.io", "v1", "CSIDriver", "csidrivers", false),
            Descriptor("storage.k8s.io", "v1", "VolumeAttachment", "volumeattachments", false),

            // Cluster administration — the section that exists so Config doesn't
            // have to hold this.
            Descriptor("", "v1", "Node", "nodes", false),
            Descriptor("", "v1", "Namespace", "namespaces", false),
            Descriptor("rbac.authorization.k8s.io", "v1", "Role", "roles", true),
            Descriptor("rbac.authorization.k8s.io", "v1", "RoleBinding", "rolebindings", true),
            Descriptor("rbac.authorization.k8s.io", "v1", "ClusterRole", "clusterroles", false),
            Descriptor("rbac.authorization.k8s.io", "v1", "ClusterRoleBinding", "clusterrolebindings", false),
            Descriptor("apiregistration.k8s.io", "v1", "APIService", "apiservices", false),
            Descriptor("apiextensions.k8s.io", "v1", "CustomResourceDefinition", "customresourcedefinitions", false),
            Descriptor("admissionregistration.k8s.io", "v1", "ValidatingWebhookConfiguration", "validatingwebhookconfigurations", false),
            Descriptor("admissionregistration.k8s.io", "v1", "MutatingWebhookConfiguration", "mutatingwebhookconfigurations", false),
            Descriptor("admissionregistration.k8s.io", "v1", "ValidatingAdmissionPolicy", "validatingadmissionpolicies", false),
            Descriptor("certificates.k8s.io", "v1", "CertificateSigningRequest", "certificatesigningrequests", false),
            Descriptor("coordination.k8s.io", "v1", "Lease", "leases", true),
            Descriptor("flowcontrol.apiserver.k8s.io", "v1", "FlowSchema", "flowschemas", false),
            Descriptor("flowcontrol.apiserver.k8s.io", "v1", "PriorityLevelConfiguration", "prioritylevelconfigurations", false),
            Descriptor("scheduling.k8s.io", "v1", "PriorityClass", "priorityclasses", false),
            Descriptor("node.k8s.io", "v1", "RuntimeClass", "runtimeclasses", false),
        };

        foreach (var crd in LoadCrdCatalog())
        {
            result.Add(crd);
        }

        return result;
    }

    private static IEnumerable<ResourceDescriptor> LoadCrdCatalog()
    {
        foreach (var entry in CrdCatalogDoc.RootElement.EnumerateArray())
        {
            var group = entry.GetProperty("group").GetString() ?? "";
            var kind = entry.GetProperty("kind").GetString() ?? "";
            var plural = entry.GetProperty("plural").GetString() ?? "";
            var namespaced = !kind.StartsWith("Cluster", StringComparison.Ordinal) && kind != "GatewayClass" && kind != "StorageClass";
            yield return Descriptor(group, "v1", kind, plural, namespaced);
        }
    }

    private static ResourceDescriptor Descriptor(
        string group, string version, string kind, string plural, bool namespaced, bool scalable = false) =>
        new(group, version, kind, plural, kind.ToLowerInvariant(), namespaced, [], [])
        {
            Subresources = scalable ? ["scale", "status"] : [],
        };

    /// <summary>
    /// Builds the sidebar the demo cluster shows, through the same
    /// <see cref="SidebarGrouping"/> calls <c>ClusterTabViewModel.BuildSidebarAsync</c>
    /// makes after real discovery — including <c>LabelAmbiguousKinds</c>, since the CRD
    /// catalog deliberately holds same-named kinds from different API groups.
    /// </summary>
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
            sections[title].Kinds.Add(new SidebarKindViewModel(descriptor, SidebarGrouping.IconKeyFor(descriptor, title)));
        }

        var result = SidebarGrouping.SectionOrder.Select(t => sections[t]).Where(s => s.Kinds.Count > 0).ToArray();
        SidebarGrouping.LabelAmbiguousKinds(result);
        return result;
    }

    /// <summary>
    /// The demo objects of one kind, in one namespace. Anything the dataset has no
    /// objects for comes back empty on purpose — a kind with nothing behind it has to
    /// land on the list's real empty state, not on a crash (UI rule 9).
    /// </summary>
    public static IReadOnlyList<DynamicResource> ResourcesFor(ResourceDescriptor descriptor, string? @namespace)
    {
        IReadOnlyList<DynamicResource> all = descriptor switch
        {
            { Group: "", Kind: "Pod" } => Pods,
            { Group: "apps", Kind: "Deployment" } => Deployments,
            { Group: "", Kind: "Event" } => Events,
            { Group: "", Kind: "Secret" } => [Secret],
            { Group: "", Kind: "ConfigMap" } => ConfigMaps,
            { Group: "", Kind: "Node" } => Nodes,
            { Group: "cert-manager.io", Kind: "Certificate" } => Certificates,
            _ => [],
        };

        return @namespace is null
            ? all
            : [.. all.Where(r => string.Equals(r.Namespace, @namespace, StringComparison.Ordinal))];
    }
}
