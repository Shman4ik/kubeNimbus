using System.Text.Json;

namespace KubeNimbus.Core.Applications;

/// <summary>One changed field of a pod template. Before/After are null for an addition/removal.</summary>
/// <param name="Field">What changed, in manifest terms: <c>image (worker)</c>, <c>env PAYMENT_URL (worker)</c>.</param>
public sealed record TemplateChange(string Field, string? Before, string? After)
{
    public bool IsAdded => Before is null && After is not null;

    public bool IsRemoved => Before is not null && After is null;
}

/// <summary>What changed between two ReplicaSets of one Deployment.</summary>
public sealed record TemplateDiffResult(
    DynamicResource Previous,
    DynamicResource Current,
    long PreviousRevision,
    long CurrentRevision,
    IReadOnlyList<TemplateChange> Changes);

/// <summary>
/// "What changed" without history of our own: a Deployment keeps its previous ReplicaSets
/// (up to <c>revisionHistoryLimit</c>), each carrying the pod template it was created from and
/// its <c>deployment.kubernetes.io/revision</c>. Comparing the newest two is the whole of it.
/// </summary>
/// <remarks>
/// <para>
/// The comparison covers what a deploy changes and a reader acts on: the image, resources,
/// command and args, which environment variables exist and where they come from, and which
/// ConfigMaps and Secrets are mounted. It is not a general YAML diff — the apply preview has
/// one — because this answers "what is different about the version that is failing".
/// </para>
/// <para>
/// <b>No value is ever printed from an env var or a Secret.</b> A literal <c>value</c> that
/// changed says "value changed", because the page is read on shared screens during incidents
/// and a template's env is where credentials leak in practice — the same stance as the Env
/// tab, which masks Secret values behind an explicit reveal. A <em>reference</em> (which
/// ConfigMap or Secret, which key) is not a value and is shown, because a renamed Secret is
/// exactly the kind of change that breaks a deploy.
/// </para>
/// </remarks>
public static class PodTemplateDiff
{
    public const string RevisionAnnotation = "deployment.kubernetes.io/revision";

    /// <summary>The current and previous ReplicaSets of <paramref name="deployment"/>, by revision; null with fewer than two.</summary>
    public static (DynamicResource Previous, DynamicResource Current)? PickReplicaSets(
        DynamicResource deployment, IEnumerable<DynamicResource> replicaSets)
    {
        var owned = replicaSets
            .Where(rs => ApplicationRules.IsOwnedBy(rs, deployment))
            .OrderByDescending(ApplicationRules.Revision)
            .Take(2)
            .ToList();
        return owned.Count < 2 ? null : (owned[1], owned[0]);
    }

    public static TemplateDiffResult Compare(DynamicResource previous, DynamicResource current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var changes = new List<TemplateChange>();
        var before = Template(previous);
        var after = Template(current);

        var restartedBefore = J.Obj(J.Obj(before, "metadata"), "annotations");
        var restartedAfter = J.Obj(J.Obj(after, "metadata"), "annotations");
        Add(changes, "restartedAt (rollout restart)",
            NullIfEmpty(J.Str(restartedBefore, WorkloadActions.RestartedAtAnnotation)),
            NullIfEmpty(J.Str(restartedAfter, WorkloadActions.RestartedAtAnnotation)));

        var specBefore = J.Obj(before, "spec");
        var specAfter = J.Obj(after, "spec");
        foreach (var array in (string[])["initContainers", "containers"])
        {
            var old = ByName(J.Arr(specBefore, array));
            var @new = ByName(J.Arr(specAfter, array));
            var prefix = array == "initContainers" ? "init container" : "container";
            foreach (var name in old.Keys.Union(@new.Keys))
            {
                if (!old.TryGetValue(name, out var a))
                {
                    changes.Add(new TemplateChange($"{prefix} {name}", null, "added"));
                    continue;
                }

                if (!@new.TryGetValue(name, out var b))
                {
                    changes.Add(new TemplateChange($"{prefix} {name}", "present", null));
                    continue;
                }

                CompareContainer(changes, name, a, b);
            }
        }

        CompareVolumes(changes, J.Arr(specBefore, "volumes"), J.Arr(specAfter, "volumes"));
        Add(changes, "serviceAccountName", NullIfEmpty(J.Str(specBefore, "serviceAccountName")), NullIfEmpty(J.Str(specAfter, "serviceAccountName")));

        return new TemplateDiffResult(previous, current, ApplicationRules.Revision(previous), ApplicationRules.Revision(current), changes);
    }

    private static JsonElement Template(DynamicResource replicaSet) => J.Obj(replicaSet.Raw, "spec", "template");

    private static void CompareContainer(List<TemplateChange> changes, string name, JsonElement a, JsonElement b)
    {
        Add(changes, $"image ({name})", NullIfEmpty(J.Str(a, "image")), NullIfEmpty(J.Str(b, "image")));

        foreach (var kind in (string[])["requests", "limits"])
        {
            var ra = J.Obj(J.Obj(a, "resources"), kind);
            var rb = J.Obj(J.Obj(b, "resources"), kind);
            foreach (var resource in Keys(ra).Union(Keys(rb)))
            {
                Add(changes, $"{kind}.{resource} ({name})", Scalar(ra, resource), Scalar(rb, resource));
            }
        }

        Add(changes, $"command ({name})", Argv(a, "command"), Argv(b, "command"));
        Add(changes, $"args ({name})", Argv(a, "args"), Argv(b, "args"));

        var envA = Env(a);
        var envB = Env(b);
        foreach (var variable in envA.Keys.Union(envB.Keys))
        {
            envA.TryGetValue(variable, out var va);
            envB.TryGetValue(variable, out var vb);
            if (va == vb)
            {
                continue;
            }

            // Two literals that differ: the fact of the change, never either value.
            if (va is { IsLiteral: true } && vb is { IsLiteral: true })
            {
                changes.Add(new TemplateChange($"env {variable} ({name})", "value", "value changed"));
                continue;
            }

            changes.Add(new TemplateChange($"env {variable} ({name})", va?.Describe, vb?.Describe));
        }

        var fromA = EnvFrom(a);
        var fromB = EnvFrom(b);
        foreach (var removed in fromA.Except(fromB))
        {
            changes.Add(new TemplateChange($"envFrom ({name})", removed, null));
        }

        foreach (var added in fromB.Except(fromA))
        {
            changes.Add(new TemplateChange($"envFrom ({name})", null, added));
        }
    }

    private static void CompareVolumes(List<TemplateChange> changes, IReadOnlyList<JsonElement> a, IReadOnlyList<JsonElement> b)
    {
        var va = a.Select(VolumeSource).Where(s => s is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var vb = b.Select(VolumeSource).Where(s => s is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var removed in va.Except(vb).Order(StringComparer.Ordinal))
        {
            changes.Add(new TemplateChange("volume", removed, null));
        }

        foreach (var added in vb.Except(va).Order(StringComparer.Ordinal))
        {
            changes.Add(new TemplateChange("volume", null, added));
        }
    }

    /// <summary>A volume's ConfigMap/Secret/PVC reference, the part a deploy can break; null for anything else.</summary>
    private static string? VolumeSource(JsonElement volume)
    {
        var name = J.Str(volume, "name");
        if (J.Str(J.Obj(volume, "configMap"), "name") is { Length: > 0 } cm)
        {
            return $"{name}: ConfigMap {cm}";
        }

        if (J.Str(J.Obj(volume, "secret"), "secretName") is { Length: > 0 } secret)
        {
            return $"{name}: Secret {secret}";
        }

        if (J.Str(J.Obj(volume, "persistentVolumeClaim"), "claimName") is { Length: > 0 } claim)
        {
            return $"{name}: PersistentVolumeClaim {claim}";
        }

        return null;
    }

    private sealed record EnvSource(bool IsLiteral, string Describe, string Identity);

    private static Dictionary<string, EnvSource> Env(JsonElement container)
    {
        var result = new Dictionary<string, EnvSource>(StringComparer.Ordinal);
        foreach (var variable in J.Arr(container, "env"))
        {
            var name = J.Str(variable, "name");
            var from = J.Obj(variable, "valueFrom");
            EnvSource source;
            if (J.Obj(from, "configMapKeyRef") is { ValueKind: JsonValueKind.Object } cm)
            {
                var text = $"from ConfigMap {J.Str(cm, "name")} key {J.Str(cm, "key")}";
                source = new EnvSource(false, text, text);
            }
            else if (J.Obj(from, "secretKeyRef") is { ValueKind: JsonValueKind.Object } secret)
            {
                var text = $"from Secret {J.Str(secret, "name")} key {J.Str(secret, "key")}";
                source = new EnvSource(false, text, text);
            }
            else if (J.Obj(from, "fieldRef") is { ValueKind: JsonValueKind.Object } field)
            {
                var text = $"from field {J.Str(field, "fieldPath")}";
                source = new EnvSource(false, text, text);
            }
            else if (J.Obj(from, "resourceFieldRef") is { ValueKind: JsonValueKind.Object } resource)
            {
                var text = $"from resource {J.Str(resource, "resource")}";
                source = new EnvSource(false, text, text);
            }
            else
            {
                // The identity carries the value so a changed literal is detected; Describe
                // never does, so the value can never reach the page.
                source = new EnvSource(true, "set", "literal:" + J.Str(variable, "value"));
            }

            result[name] = source;
        }

        return result;
    }

    private static HashSet<string> EnvFrom(JsonElement container)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in J.Arr(container, "envFrom"))
        {
            var prefix = J.Str(source, "prefix");
            if (J.Str(J.Obj(source, "configMapRef"), "name") is { Length: > 0 } cm)
            {
                result.Add($"ConfigMap {cm}" + (prefix.Length > 0 ? $" (prefix {prefix})" : ""));
            }

            if (J.Str(J.Obj(source, "secretRef"), "name") is { Length: > 0 } secret)
            {
                result.Add($"Secret {secret}" + (prefix.Length > 0 ? $" (prefix {prefix})" : ""));
            }
        }

        return result;
    }

    private static Dictionary<string, JsonElement> ByName(IReadOnlyList<JsonElement> containers)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var container in containers)
        {
            result[J.Str(container, "name")] = container;
        }

        return result;
    }

    private static IEnumerable<string> Keys(JsonElement map) =>
        map.ValueKind == JsonValueKind.Object ? map.EnumerateObject().Select(p => p.Name) : [];

    private static string? Scalar(JsonElement map, string key) =>
        map.ValueKind == JsonValueKind.Object && map.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static string? Argv(JsonElement container, string name)
    {
        var items = J.Arr(container, name);
        return items.Count == 0 ? null : string.Join(' ', items.Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText()));
    }

    private static void Add(List<TemplateChange> changes, string field, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add(new TemplateChange(field, before, after));
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
