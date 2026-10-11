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
            : $"apis/{MetricsGroup}/{version}/namespaces/{ResourceDescriptor.PathSegment(@namespace, "namespace")}/pods";

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
        var path = $"apis/{MetricsGroup}/{version}/namespaces/{ResourceDescriptor.PathSegment(@namespace, "namespace")}/pods/{ResourceDescriptor.PathSegment(podName)}";

        using var response = await SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureMetricsSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await ClusterJson.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// One poll of node usage, classified the way the Nodes list's capacity strip words it:
    /// read, refused (a 403, with the server's own sentence) or unavailable (no metrics API,
    /// or one registered but not serving). Anything else — a timeout, a 500, throttling — is
    /// thrown, because it says nothing lasting about the cluster and the poll retries it.
    /// </summary>
    /// <remarks>
    /// The 403 is the case that matters (#206, the 0.7.0 QA finding M-4): the poll used to
    /// note it and then report the empty result as a successful read, so the strip said
    /// "no node has reported usage yet" — a verdict it did not have — instead of "usage not
    /// permitted". Classifying here makes the refusal a value the poll hands on, not a side
    /// effect its next line can overwrite, and lets a live test read the same value.
    /// </remarks>
    public async Task<NodeUsageRead> ReadNodeUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new NodeUsageRead(NodeUsageOutcome.Read, await GetNodeMetricsAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (MetricsUnavailableException ex)
        {
            return new NodeUsageRead(NodeUsageOutcome.Unavailable, [], ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new NodeUsageRead(NodeUsageOutcome.Refused, [], (ex as KubernetesApiException)?.ServerMessage ?? ex.Message);
        }
    }

    /// <summary>
    /// Usage for one node, or null when metrics-server has not scraped it yet (a node
    /// that just joined, or one whose kubelet stopped answering).
    /// </summary>
    public async Task<NodeMetrics?> GetNodeMetricsAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        var version = await RequireMetricsVersionAsync(cancellationToken).ConfigureAwait(false);
        var path = $"apis/{MetricsGroup}/{version}/nodes/{ResourceDescriptor.PathSegment(nodeName)}";

        using var response = await SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureMetricsSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await ClusterJson.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
        var usage = ReadUsage(doc.RootElement);
        return new NodeMetrics(ReadName(doc.RootElement), usage.Cpu, usage.Memory);
    }

    private async Task<string> RequireMetricsVersionAsync(CancellationToken ct) =>
        await GetMetricsApiVersionAsync(ct).ConfigureAwait(false)
        ?? throw new MetricsUnavailableException(
            "This cluster does not expose metrics.k8s.io — install metrics-server to see CPU/memory usage.");

    private async Task<JsonDocument> GetMetricsDocumentAsync(string path, CancellationToken ct)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await EnsureMetricsSuccessAsync(response, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ClusterJson.ParseAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A registered-but-broken metrics API answers 503 (no backing endpoints)
    /// — that's "unavailable", not a crash, so it maps to the same exception as
    /// a cluster with no metrics API at all. Any other failure is a
    /// <see cref="KubernetesApiException"/>, so a 403 carries the server's sentence
    /// ("… cannot list resource "nodes" in API group "metrics.k8s.io" …") rather than
    /// .NET's "Response status code does not indicate success".
    /// </summary>
    private static async Task EnsureMetricsSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.NotFound)
        {
            throw new MetricsUnavailableException(
                $"The metrics API is registered but not serving ({(int)response.StatusCode}); is metrics-server healthy?");
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
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

/// <summary>How one poll of node usage ended; see <see cref="ClusterClient.ReadNodeUsageAsync"/>.</summary>
public enum NodeUsageOutcome
{
    /// <summary>metrics.k8s.io answered; the list may still be empty (a node not yet scraped).</summary>
    Read,

    /// <summary>The metrics API is there and refused this user (403).</summary>
    Refused,

    /// <summary>No metrics API, or one registered and not serving.</summary>
    Unavailable,
}

/// <summary>One poll of node usage: what it measured, or why it measured nothing.</summary>
/// <param name="Reason">The server's sentence for a refusal, the app's for an absent API; null when read.</param>
public sealed record NodeUsageRead(NodeUsageOutcome Outcome, IReadOnlyList<NodeMetrics> Nodes, string? Reason);

/// <summary>Measured usage of one node.</summary>
public sealed record NodeMetrics(string Name, long? CpuNanocores, long? MemoryBytes);

/// <summary>
/// Raised when the cluster has no usable <c>metrics.k8s.io</c> — either not
/// registered at all, or registered with no healthy backend. Callers hide the
/// usage UI rather than treating it as an error.
/// </summary>
public sealed class MetricsUnavailableException(string message) : Exception(message);
