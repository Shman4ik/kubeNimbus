using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Live CPU/memory usage from the <c>metrics.k8s.io</c> aggregated API
/// (metrics-server / OpenShift metrics). Reported per container for pods and
/// per node, in whatever quantity strings the API server sends — parsed with
/// <see cref="Quantity"/>.
/// </summary>
/// <remarks>
/// The metrics API is optional: plenty of clusters run without metrics-server,
/// and the aggregated API can also be registered but unavailable (backing
/// deployment down). Both cases surface as
/// <see cref="MetricsUnavailableException"/> rather than a hard failure, so the
/// UI can hide the columns instead of showing an error. The group's version is
/// taken from discovery instead of being hardcoded to v1beta1 — same principle
/// as the rest of the app: nothing about the server's API surface is assumed.
/// Responses are read with raw <see cref="JsonDocument"/> (as with discovery and
/// watch frames); the shape is three fields deep and needs no source-gen model.
/// </remarks>
public sealed partial class ClusterClient
{
    private const string MetricsGroup = "metrics.k8s.io";

    /// <summary>
    /// Discovery-reported version of the metrics API, or null when the cluster
    /// has none. Cached with the catalog, so this is one cheap lookup after the
    /// first call.
    /// </summary>
    public async Task<string?> GetMetricsApiVersionAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await GetResourceCatalogAsync(cancellationToken).ConfigureAwait(false);
        return catalog.FirstOrDefault(d => string.Equals(d.Group, MetricsGroup, StringComparison.Ordinal))?.Version;
    }

    /// <summary>True when the cluster exposes <c>metrics.k8s.io</c> at all.</summary>
    public async Task<bool> IsMetricsApiAvailableAsync(CancellationToken cancellationToken = default) =>
        await GetMetricsApiVersionAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Per-container usage for every pod in a namespace (or all namespaces when
    /// <paramref name="namespace"/> is null).
    /// </summary>
    public async Task<IReadOnlyList<PodMetrics>> GetPodMetricsAsync(
        string? @namespace = null, CancellationToken cancellationToken = default)
    {
        var version = await RequireMetricsVersionAsync(cancellationToken).ConfigureAwait(false);
        var path = @namespace is null
            ? $"apis/{MetricsGroup}/{version}/pods"
            : $"apis/{MetricsGroup}/{version}/namespaces/{Uri.EscapeDataString(@namespace)}/pods";

        using var doc = await GetMetricsDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        var result = new List<PodMetrics>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                result.Add(ReadPodMetrics(item));
            }
        }

        return result;
    }

    /// <summary>Usage for one pod, or null when metrics for it aren't available yet (just-started pods).</summary>
    public async Task<PodMetrics?> GetPodMetricsAsync(
        string @namespace, string podName, CancellationToken cancellationToken = default)
    {
        var version = await RequireMetricsVersionAsync(cancellationToken).ConfigureAwait(false);
        var path = $"apis/{MetricsGroup}/{version}/namespaces/{Uri.EscapeDataString(@namespace)}/pods/{Uri.EscapeDataString(podName)}";

        using var response = await SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureMetricsSuccess(response);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadPodMetrics(doc.RootElement);
    }

    /// <summary>Usage for every node.</summary>
    public async Task<IReadOnlyList<NodeMetrics>> GetNodeMetricsAsync(CancellationToken cancellationToken = default)
    {
        var version = await RequireMetricsVersionAsync(cancellationToken).ConfigureAwait(false);

        using var doc = await GetMetricsDocumentAsync($"apis/{MetricsGroup}/{version}/nodes", cancellationToken).ConfigureAwait(false);
        var result = new List<NodeMetrics>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var usage = ReadUsage(item);
                result.Add(new NodeMetrics(ReadName(item), usage.Cpu, usage.Memory));
            }
        }

        return result;
    }

    private async Task<string> RequireMetricsVersionAsync(CancellationToken ct) =>
        await GetMetricsApiVersionAsync(ct).ConfigureAwait(false)
        ?? throw new MetricsUnavailableException(
            "This cluster does not expose metrics.k8s.io — install metrics-server to see CPU/memory usage.");

    private async Task<JsonDocument> GetMetricsDocumentAsync(string path, CancellationToken ct)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        EnsureMetricsSuccess(response);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A registered-but-broken metrics API answers 503 (no backing endpoints)
    /// — that's "unavailable", not a crash, so it maps to the same exception as
    /// a cluster with no metrics API at all.
    /// </summary>
    private static void EnsureMetricsSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.NotFound)
        {
            throw new MetricsUnavailableException(
                $"The metrics API is registered but not serving ({(int)response.StatusCode}); is metrics-server healthy?");
        }

        response.EnsureSuccessStatusCode();
    }

    private static PodMetrics ReadPodMetrics(JsonElement item)
    {
        var containers = new List<ContainerMetrics>();
        if (item.TryGetProperty("containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
            {
                var usage = ReadUsage(c);
                containers.Add(new ContainerMetrics(ReadName(c), usage.Cpu, usage.Memory));
            }
        }

        return new PodMetrics(ReadNamespace(item), ReadName(item), containers);
    }

    private static string ReadName(JsonElement item) =>
        item.TryGetProperty("name", out var direct) && direct.ValueKind == JsonValueKind.String
            ? direct.GetString() ?? ""
            : item.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("name", out var n)
                ? n.GetString() ?? ""
                : "";

    private static string? ReadNamespace(JsonElement item) =>
        item.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("namespace", out var ns)
            ? ns.GetString()
            : null;

    private static (long? Cpu, long? Memory) ReadUsage(JsonElement owner)
    {
        if (!owner.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var cpu = usage.TryGetProperty("cpu", out var c) ? Quantity.ParseCpuNanocores(c.GetString()) : null;
        var memory = usage.TryGetProperty("memory", out var m) ? Quantity.ParseBytes(m.GetString()) : null;
        return (cpu, memory);
    }
}

/// <summary>Measured usage of one container, in nanocores and bytes (null when the API omitted it).</summary>
public sealed record ContainerMetrics(string Name, long? CpuNanocores, long? MemoryBytes);

/// <summary>Measured usage of one pod — the API reports per container, the pod total is the sum.</summary>
public sealed record PodMetrics(string? Namespace, string Name, IReadOnlyList<ContainerMetrics> Containers)
{
    public string Key => $"{Namespace}/{Name}";

    /// <summary>Sum across containers, null only when no container reported a value.</summary>
    public long? CpuNanocores => Sum(c => c.CpuNanocores);

    public long? MemoryBytes => Sum(c => c.MemoryBytes);

    private long? Sum(Func<ContainerMetrics, long?> select)
    {
        long total = 0;
        var any = false;
        foreach (var container in Containers)
        {
            if (select(container) is { } value)
            {
                total += value;
                any = true;
            }
        }

        return any ? total : null;
    }
}

/// <summary>Measured usage of one node.</summary>
public sealed record NodeMetrics(string Name, long? CpuNanocores, long? MemoryBytes);

/// <summary>
/// Raised when the cluster has no usable <c>metrics.k8s.io</c> — either not
/// registered at all, or registered with no healthy backend. Callers hide the
/// usage UI rather than treating it as an error.
/// </summary>
public sealed class MetricsUnavailableException(string message) : Exception(message);
