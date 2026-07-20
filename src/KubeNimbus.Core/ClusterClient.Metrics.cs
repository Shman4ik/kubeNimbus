using System.Text.Json;
using k8s.Models;

namespace KubeNimbus.Core;

/// <summary>
/// metrics.k8s.io client (PodMetrics/NodeMetrics). This is a separate
/// aggregated API group only present when metrics-server (or a compatible
/// implementation) is installed, so every entry point here degrades
/// gracefully — an empty list / null / false — instead of throwing when the
/// group is absent, rather than crash a cluster that simply doesn't have it.
/// The metrics API is poll-only (no watch verb), so callers re-call this on a
/// timer instead of getting a live stream — see <c>ClusterTabViewModel</c>'s
/// and <c>PodDetailTabViewModel</c>'s metrics refresh timers in the App layer.
/// </summary>
public sealed partial class ClusterClient
{
    private bool? _metricsApiAvailable;
    private readonly SemaphoreSlim _metricsAvailabilityLock = new(1, 1);

    /// <summary>
    /// Whether metrics.k8s.io is registered on this cluster — checked via the
    /// same discovery catalog every other resource kind uses (so no extra
    /// round trip beyond the first call), cached for the life of the connection.
    /// </summary>
    public async Task<bool> IsMetricsApiAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_metricsApiAvailable is { } cached)
        {
            return cached;
        }

        await _metricsAvailabilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metricsApiAvailable is { } cached2)
            {
                return cached2;
            }

            var catalog = await GetResourceCatalogAsync(cancellationToken).ConfigureAwait(false);
            _metricsApiAvailable = catalog.Any(d => string.Equals(d.Group, ResourceDescriptor.PodMetrics.Group, StringComparison.Ordinal));
            return _metricsApiAvailable.Value;
        }
        finally
        {
            _metricsAvailabilityLock.Release();
        }
    }

    /// <summary>Current usage snapshot for every pod in a namespace (or all namespaces when null). Empty when metrics.k8s.io isn't present.</summary>
    public async Task<IReadOnlyList<DynamicResource>> GetPodMetricsAsync(string? @namespace = null, CancellationToken cancellationToken = default)
    {
        if (!await IsMetricsApiAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        try
        {
            return await ListResourceOnceAsync(ResourceDescriptor.PodMetrics, @namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The APIService can be registered but its backing metrics-server pod
            // down/unreachable; treat that the same as "not installed".
            return [];
        }
    }

    /// <summary>Current usage snapshot for one pod, or null when unavailable (no metrics.k8s.io, or the pod has none yet).</summary>
    public async Task<DynamicResource?> GetPodMetricsAsync(string @namespace, string name, CancellationToken cancellationToken = default)
    {
        if (!await IsMetricsApiAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return await ReadResourceAsync(ResourceDescriptor.PodMetrics, @namespace, name, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Current usage snapshot for every node. Empty when metrics.k8s.io isn't present.</summary>
    public async Task<IReadOnlyList<DynamicResource>> GetNodeMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsMetricsApiAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        try
        {
            return await ListResourceOnceAsync(ResourceDescriptor.NodeMetrics, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }
}

/// <summary>Field readers for PodMetrics/NodeMetrics objects fetched as <see cref="DynamicResource"/>.</summary>
public static class MetricsFields
{
    /// <summary>Per-container (name, cpu cores, memory bytes) usage, parsed with the official client's <see cref="ResourceQuantity"/> parser.</summary>
    public static IReadOnlyList<(string Name, double CpuCores, long MemoryBytes)> ContainerUsage(this DynamicResource podMetrics)
    {
        if (!podMetrics.Raw.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<(string, double, long)>();
        foreach (var c in containers.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var (cpu, mem) = ReadUsage(c);
            result.Add((name, cpu, mem));
        }

        return result;
    }

    /// <summary>Sum of every container's CPU usage, in cores.</summary>
    public static double TotalCpuCores(this DynamicResource podMetrics) => podMetrics.ContainerUsage().Sum(c => c.CpuCores);

    /// <summary>Sum of every container's memory usage, in bytes.</summary>
    public static long TotalMemoryBytes(this DynamicResource podMetrics) => podMetrics.ContainerUsage().Sum(c => c.MemoryBytes);

    /// <summary>Node-level (cpu cores, memory bytes) usage.</summary>
    public static (double CpuCores, long MemoryBytes) NodeUsage(this DynamicResource nodeMetrics) => ReadUsage(nodeMetrics.Raw);

    private static (double CpuCores, long MemoryBytes) ReadUsage(JsonElement withUsage)
    {
        if (!withUsage.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        var cpu = usage.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.String
            ? new ResourceQuantity(c.GetString()!).ToDouble()
            : 0;
        var memory = usage.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.String
            ? (long)new ResourceQuantity(m.GetString()!).ToDouble()
            : 0L;
        return (cpu, memory);
    }
}
