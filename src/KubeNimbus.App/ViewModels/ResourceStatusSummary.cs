using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Best-effort one-line status + health for the generic list view — every
/// built-in kind (and most CRDs, which tend to follow the same conventions)
/// shapes its status a little differently, so this reads whichever of the
/// common patterns is present rather than hardcoding a table per Kind.
/// </summary>
public static class ResourceStatusSummary
{
    public static (string Text, string Health) Summarize(DynamicResource resource)
    {
        // core/v1 Event: Type/Reason/Count live at the top level, not under status —
        // "Warning" events read as warn (so they visually stand out in the sidebar's
        // Events view the same way pod-detail's Events tab already colors them).
        if (resource.Kind == "Event" && resource.ApiVersion == "v1")
        {
            var reason = resource.Reason();
            if (reason.Length == 0)
            {
                return ("", "idle");
            }

            var count = resource.Count();
            var text = count > 1 ? $"{reason} ×{count}" : reason;
            var health = string.Equals(resource.Type(), "Warning", StringComparison.OrdinalIgnoreCase) ? "warn" : "ok";
            return (text, health);
        }

        if (!resource.Raw.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return ("", "idle");
        }

        // Pod: phase + ready containers + restarts.
        if (resource.Kind == "Pod" && status.TryGetProperty("phase", out var phaseEl))
        {
            var phase = phaseEl.GetString() ?? "Unknown";
            var (ready, total, restarts) = PodContainerCounts(status);
            var text = total > 0 ? $"{phase} ({ready}/{total})" : phase;
            if (restarts > 0)
            {
                text += $" · {restarts} restart{(restarts == 1 ? "" : "s")}";
            }

            var health = phase switch
            {
                "Running" when ready == total => "ok",
                "Succeeded" => "ok",
                "Failed" => "error",
                "Pending" => "warn",
                _ when total > 0 && ready == 0 => "error", // e.g. CrashLoopBackOff: fully down, not just degraded
                _ => ready < total ? "warn" : "ok",
            };
            return (text, health);
        }

        // Workload controllers (Deployment/ReplicaSet/StatefulSet/DaemonSet): ready/desired replicas.
        if (TryReplicaCounts(status, out var readyReplicas, out var desiredReplicas))
        {
            var text = $"{readyReplicas}/{desiredReplicas} ready";
            var health = readyReplicas >= desiredReplicas && desiredReplicas > 0 ? "ok" : desiredReplicas == 0 ? "idle" : "warn";
            return (text, health);
        }

        // Anything with a standard "conditions" array: surface the most relevant one.
        if (status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conditions.EnumerateArray())
            {
                var type = c.TryGetProperty("type", out var t) ? t.GetString() : null;
                var conditionStatus = c.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (type is "Ready" or "Available")
                {
                    var ok = conditionStatus == "True";
                    return ($"{type}: {conditionStatus}", ok ? "ok" : "warn");
                }
            }
        }

        return ("", "idle");
    }

    private static (int Ready, int Total, int Restarts) PodContainerCounts(JsonElement status)
    {
        if (!status.TryGetProperty("containerStatuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, 0);
        }

        var total = 0;
        var ready = 0;
        var restarts = 0;
        foreach (var cs in statuses.EnumerateArray())
        {
            total++;
            if (cs.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True)
            {
                ready++;
            }

            if (cs.TryGetProperty("restartCount", out var rc) && rc.TryGetInt32(out var count))
            {
                restarts += count;
            }
        }

        return (ready, total, restarts);
    }

    private static bool TryReplicaCounts(JsonElement status, out int ready, out int desired)
    {
        ready = 0;
        desired = 0;
        var hasReplicas = status.TryGetProperty("replicas", out var replicasEl) && replicasEl.TryGetInt32(out desired);
        var readyProp = status.TryGetProperty("readyReplicas", out var r) && r.TryGetInt32(out ready);
        var numberReady = status.TryGetProperty("numberReady", out var nr) && nr.TryGetInt32(out ready);

        return hasReplicas && (readyProp || numberReady || desired == 0);
    }
}
