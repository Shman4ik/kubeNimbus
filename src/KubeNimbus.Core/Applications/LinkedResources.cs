using System.Text.Json;

namespace KubeNimbus.Core.Applications;

/// <summary>One object an application uses, and why it is linked.</summary>
/// <param name="Group">API group of the linked object ("" for core).</param>
/// <param name="Via">The spec reference that links it, e.g. "selector matches pod labels", "env PAYMENT_URL".</param>
public sealed record LinkedResource(string Group, string Kind, string Namespace, string Name, string Via)
{
    public string Key => $"{Group}/{Kind}:{Namespace}/{Name}";
}

/// <summary>
/// The objects an application's workloads are wired to, found from <em>spec references</em>
/// rather than from labels: a Service because its selector matches the pod template's labels,
/// an Ingress or HTTPRoute because a backend names that Service, a ConfigMap or Secret because
/// the pod spec mounts or reads it, an HPA by its <c>scaleTargetRef</c>, a PDB by its selector,
/// a PVC from a volume.
/// </summary>
/// <remarks>
/// A shared label like <c>app.kubernetes.io/part-of</c> says what someone intended; a
/// reference says what is actually connected, and "the Service this Ingress sends traffic
/// to" is the question somebody debugging a 503 is asking. Everything here reads the objects
/// it is handed and nothing else — the page lists the candidates, one request per kind.
/// </remarks>
public static class LinkedResources
{
    public static IReadOnlyList<LinkedResource> Find(
        IReadOnlyList<DynamicResource> workloads,
        IReadOnlyList<DynamicResource> pods,
        IReadOnlyList<DynamicResource> candidates)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(pods);
        ArgumentNullException.ThrowIfNull(candidates);

        var result = new List<LinkedResource>();
        var templates = workloads
            .Select(w => (Workload: w, Template: TemplateOf(w)))
            .ToList();

        // Services: selector ⊆ pod template labels (and any live pod's labels).
        var services = new List<DynamicResource>();
        foreach (var service in candidates.Where(c => c.Kind == "Service" && GroupOf(c) == ""))
        {
            var selector = J.Obj(J.Obj(service.Raw, "spec"), "selector");
            if (selector.ValueKind != JsonValueKind.Object || !selector.EnumerateObject().Any())
            {
                continue;
            }

            var wanted = selector.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
            var matches = templates.Any(t => t.Workload.Namespace == service.Namespace && Subset(wanted, Labels(J.Obj(t.Template, "metadata"))))
                || pods.Any(p => p.Namespace == service.Namespace && Subset(wanted, p.Labels));
            if (matches)
            {
                services.Add(service);
                result.Add(new LinkedResource("", "Service", service.Namespace ?? "", service.Name, "selector matches the pod labels"));
            }
        }

        var serviceNames = services.Select(s => (s.Namespace, s.Name)).ToHashSet();

        foreach (var ingress in candidates.Where(c => c.Kind == "Ingress"))
        {
            var spec = J.Obj(ingress.Raw, "spec");
            var backends = new List<string>();
            if (J.Str(J.Obj(J.Obj(spec, "defaultBackend"), "service"), "name") is { Length: > 0 } d)
            {
                backends.Add(d);
            }

            foreach (var rule in J.Arr(spec, "rules"))
            {
                foreach (var path in J.Arr(J.Obj(rule, "http"), "paths"))
                {
                    if (J.Str(J.Obj(J.Obj(path, "backend"), "service"), "name") is { Length: > 0 } name)
                    {
                        backends.Add(name);
                    }
                }
            }

            if (backends.FirstOrDefault(b => serviceNames.Contains((ingress.Namespace, b))) is { } hit)
            {
                result.Add(new LinkedResource(GroupOf(ingress), "Ingress", ingress.Namespace ?? "", ingress.Name, $"backend Service {hit}"));
            }
        }

        foreach (var route in candidates.Where(c => c.Kind == "HTTPRoute"))
        {
            string? hit = null;
            foreach (var rule in J.Arr(J.Obj(route.Raw, "spec"), "rules"))
            {
                foreach (var backend in J.Arr(rule, "backendRefs"))
                {
                    var kind = J.Str(backend, "kind");
                    var ns = J.Str(backend, "namespace") is { Length: > 0 } n ? n : route.Namespace;
                    if ((kind.Length == 0 || kind == "Service") && serviceNames.Contains((ns, J.Str(backend, "name"))))
                    {
                        hit ??= J.Str(backend, "name");
                    }
                }
            }

            if (hit is not null)
            {
                result.Add(new LinkedResource(GroupOf(route), "HTTPRoute", route.Namespace ?? "", route.Name, $"backendRef Service {hit}"));
            }
        }

        foreach (var hpa in candidates.Where(c => c.Kind == "HorizontalPodAutoscaler"))
        {
            var target = J.Obj(J.Obj(hpa.Raw, "spec"), "scaleTargetRef");
            var kind = J.Str(target, "kind");
            var name = J.Str(target, "name");
            if (workloads.Any(w => w.Namespace == hpa.Namespace && w.Kind == kind && w.Name == name))
            {
                result.Add(new LinkedResource(GroupOf(hpa), "HorizontalPodAutoscaler", hpa.Namespace ?? "", hpa.Name, $"scaleTargetRef {kind} {name}"));
            }
        }

        foreach (var pdb in candidates.Where(c => c.Kind == "PodDisruptionBudget"))
        {
            if (LabelSelector.ForPodsOf(pdb) is not { } selector)
            {
                continue;
            }

            if (templates.Any(t => t.Workload.Namespace == pdb.Namespace && selector.Matches(Labels(J.Obj(t.Template, "metadata")))))
            {
                result.Add(new LinkedResource(GroupOf(pdb), "PodDisruptionBudget", pdb.Namespace ?? "", pdb.Name, "selector matches the pod labels"));
            }
        }

        // ConfigMaps, Secrets and PVCs come from the pod spec itself — no candidate list needed.
        foreach (var (workload, template) in templates)
        {
            AddSpecReferences(result, workload.Namespace ?? "", J.Obj(template, "spec"));
        }

        foreach (var pod in pods)
        {
            AddSpecReferences(result, pod.Namespace ?? "", J.Obj(pod.Raw, "spec"), claimsOnly: true);
        }

        return [.. result.DistinctBy(r => r.Key)];
    }

    private static void AddSpecReferences(List<LinkedResource> result, string @namespace, JsonElement spec, bool claimsOnly = false)
    {
        foreach (var volume in J.Arr(spec, "volumes"))
        {
            var volumeName = J.Str(volume, "name");
            if (J.Str(J.Obj(volume, "persistentVolumeClaim"), "claimName") is { Length: > 0 } claim)
            {
                result.Add(new LinkedResource("", "PersistentVolumeClaim", @namespace, claim, $"volume {volumeName}"));
            }

            if (claimsOnly)
            {
                continue;
            }

            if (J.Str(J.Obj(volume, "configMap"), "name") is { Length: > 0 } cm)
            {
                result.Add(new LinkedResource("", "ConfigMap", @namespace, cm, $"volume {volumeName}"));
            }

            if (J.Str(J.Obj(volume, "secret"), "secretName") is { Length: > 0 } secret)
            {
                result.Add(new LinkedResource("", "Secret", @namespace, secret, $"volume {volumeName}"));
            }

            foreach (var source in J.Arr(J.Obj(volume, "projected"), "sources"))
            {
                if (J.Str(J.Obj(source, "configMap"), "name") is { Length: > 0 } pcm)
                {
                    result.Add(new LinkedResource("", "ConfigMap", @namespace, pcm, $"projected volume {volumeName}"));
                }

                if (J.Str(J.Obj(source, "secret"), "name") is { Length: > 0 } psecret)
                {
                    result.Add(new LinkedResource("", "Secret", @namespace, psecret, $"projected volume {volumeName}"));
                }
            }
        }

        if (claimsOnly)
        {
            return;
        }

        foreach (var container in J.Arr(spec, "initContainers").Concat(J.Arr(spec, "containers")))
        {
            foreach (var source in J.Arr(container, "envFrom"))
            {
                if (J.Str(J.Obj(source, "configMapRef"), "name") is { Length: > 0 } cm)
                {
                    result.Add(new LinkedResource("", "ConfigMap", @namespace, cm, $"envFrom in {J.Str(container, "name")}"));
                }

                if (J.Str(J.Obj(source, "secretRef"), "name") is { Length: > 0 } secret)
                {
                    result.Add(new LinkedResource("", "Secret", @namespace, secret, $"envFrom in {J.Str(container, "name")}"));
                }
            }

            foreach (var variable in J.Arr(container, "env"))
            {
                var from = J.Obj(variable, "valueFrom");
                if (J.Str(J.Obj(from, "configMapKeyRef"), "name") is { Length: > 0 } cm)
                {
                    result.Add(new LinkedResource("", "ConfigMap", @namespace, cm, $"env {J.Str(variable, "name")}"));
                }

                if (J.Str(J.Obj(from, "secretKeyRef"), "name") is { Length: > 0 } secret)
                {
                    result.Add(new LinkedResource("", "Secret", @namespace, secret, $"env {J.Str(variable, "name")}"));
                }
            }
        }
    }

    /// <summary>The pod template of a workload (a CronJob's is one level deeper, under its jobTemplate).</summary>
    public static JsonElement TemplateOf(DynamicResource workload)
    {
        var spec = J.Obj(workload.Raw, "spec");
        return workload.Kind == "CronJob"
            ? J.Obj(J.Obj(J.Obj(spec, "jobTemplate"), "spec"), "template")
            : J.Obj(spec, "template");
    }

    private static Dictionary<string, string> Labels(JsonElement metadata)
    {
        var labels = J.Obj(metadata, "labels");
        return labels.ValueKind == JsonValueKind.Object
            ? labels.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static bool Subset(IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string> labels) =>
        wanted.All(w => labels.TryGetValue(w.Key, out var value) && value == w.Value);

    private static string GroupOf(DynamicResource resource)
    {
        var apiVersion = resource.ApiVersion;
        var slash = apiVersion.IndexOf('/');
        return slash < 0 ? "" : apiVersion[..slash];
    }
}
