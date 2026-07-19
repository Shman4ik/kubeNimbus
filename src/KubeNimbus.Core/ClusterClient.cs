using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using k8s;
using k8s.Models;

namespace KubeNimbus.Core;

/// <summary>
/// Connection to one cluster (one kubeconfig context). All list operations are
/// streaming: watches are informer-style (list + watch with resourceVersion
/// resume, relist on 410 Gone) and everything honors cancellation mid-stream.
/// </summary>
/// <remarks>
/// KubernetesClient.Aot ships no reflection-based <c>WatchAsync</c> helper, so
/// watches and log follows are issued directly against the client's own
/// <see cref="Kubernetes.HttpClient"/> — that reuses its fully configured auth
/// (client cert on the handler, bearer/exec token via <see cref="Kubernetes.Credentials"/>)
/// and TLS. Watch frames are line-delimited JSON parsed with <see cref="JsonDocument"/>
/// (AOT-safe) and materialized through source-generated <see cref="KubernetesJson"/>.
/// Discovery, generic (CRD-capable) watch, server-side apply, events, exec and
/// port-forward live in the other <c>ClusterClient.*.cs</c> partial-class files;
/// they all funnel through the same <see cref="SendRequestAsync"/>/<see cref="WatchAsync{T}"/>
/// primitives declared here.
/// </remarks>
public sealed partial class ClusterClient : IDisposable
{
    private const int ListPageSize = 500;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Kubernetes _client;

    public ClusterContext Context { get; }

    private ClusterClient(ClusterContext context, Kubernetes client)
    {
        Context = context;
        _client = client;
    }

    /// <summary>
    /// Builds a client for the given context. Credentials (including exec
    /// plugins) are resolved through the kubeconfig chain right now, never
    /// from app storage.
    /// </summary>
    public static ClusterClient Connect(ClusterContext context)
    {
        var config = Kubeconfig.BuildClientConfig(context);
        return new ClusterClient(context, new Kubernetes(config));
    }

    /// <summary>Raw generated client, for tests only. App code goes through typed methods.</summary>
    internal Kubernetes Api => _client;

    public async Task<VersionInfo> GetServerVersionAsync(CancellationToken cancellationToken = default) =>
        await _client.Version.GetCodeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Live pod stream for a namespace (or all namespaces when null).
    /// Starts with Reset + one Added per existing pod (paginated list), then
    /// follows the watch. Reconnects with resourceVersion resume on connection
    /// loss and relists (a new Reset) on 410 Gone. <paramref name="connectionLost"/>
    /// fires on every transient failure so the UI can surface it instead of
    /// silently hanging.
    /// </summary>
    public IAsyncEnumerable<ResourceEvent<V1Pod>> WatchPodsAsync(
        string? @namespace = null,
        Action<Exception>? connectionLost = null,
        CancellationToken cancellationToken = default)
    {
        var path = @namespace is null
            ? "api/v1/pods"
            : $"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/pods";

        return WatchAsync(
            listPath: path,
            listPage: (continueToken, ct) => ListPodPageAsync(@namespace, continueToken, ct),
            deserialize: static el => KubernetesJson.Deserialize<V1Pod>(el),
            resourceVersionOf: static pod => pod.Metadata?.ResourceVersion,
            connectionLost: connectionLost,
            cancellationToken: cancellationToken);
    }

    private async Task<(IList<V1Pod> Items, string? Continue, string? ResourceVersion)> ListPodPageAsync(
        string? @namespace, string? continueToken, CancellationToken ct)
    {
        var list = @namespace is null
            ? await _client.CoreV1.ListPodForAllNamespacesAsync(
                continueParameter: continueToken, limit: ListPageSize, cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedPodAsync(
                @namespace, continueParameter: continueToken, limit: ListPageSize, cancellationToken: ct).ConfigureAwait(false);
        return (list.Items, list.Metadata?.ContinueProperty, list.Metadata?.ResourceVersion);
    }

    /// <summary>
    /// Generic informer loop: paginated initial list (emitting Added per item
    /// after a Reset), then a resumable watch against <paramref name="listPath"/>.
    /// </summary>
    private async IAsyncEnumerable<ResourceEvent<T>> WatchAsync<T>(
        string listPath,
        Func<string?, CancellationToken, Task<(IList<T> Items, string? Continue, string? ResourceVersion)>> listPage,
        Func<JsonElement, T?> deserialize,
        Func<T, string?> resourceVersionOf,
        Action<Exception>? connectionLost,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<ResourceEvent<T>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var pump = PumpAsync(listPath, listPage, deserialize, resourceVersionOf, channel.Writer, connectionLost, linked.Token);
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        }
    }

    private async Task PumpAsync<T>(
        string listPath,
        Func<string?, CancellationToken, Task<(IList<T> Items, string? Continue, string? ResourceVersion)>> listPage,
        Func<JsonElement, T?> deserialize,
        Func<T, string?> resourceVersionOf,
        ChannelWriter<ResourceEvent<T>> writer,
        Action<Exception>? connectionLost,
        CancellationToken ct)
        where T : class
    {
        try
        {
            var backoff = InitialBackoff;
            string? resourceVersion = null;
            var needRelist = true;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (needRelist)
                    {
                        await writer.WriteAsync(ResourceEvent<T>.Reset, ct).ConfigureAwait(false);
                        resourceVersion = await ListAndEmitAsync(listPage, writer, ct).ConfigureAwait(false);
                        needRelist = false;
                        backoff = InitialBackoff;
                    }

                    var relist = await StreamWatchAsync(
                        listPath, resourceVersion!, deserialize, resourceVersionOf, writer,
                        rv => resourceVersion = rv, ct).ConfigureAwait(false);
                    if (relist)
                    {
                        needRelist = true;
                    }
                    // Otherwise the watch closed cleanly (server timeout): loop resumes
                    // from the last observed resourceVersion with no relist.
                    backoff = InitialBackoff;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
                {
                    needRelist = true;
                }
                catch (Exception ex)
                {
                    connectionLost?.Invoke(new WatchConnectionException(
                        $"Watch connection lost ({ex.GetType().Name}); retrying in {backoff.TotalSeconds:0}s.", ex));
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                }
            }

            writer.Complete();
        }
        catch (OperationCanceledException)
        {
            writer.Complete();
            throw;
        }
        catch (Exception ex)
        {
            writer.Complete(ex);
        }
    }

    private static async Task<string?> ListAndEmitAsync<T>(
        Func<string?, CancellationToken, Task<(IList<T> Items, string? Continue, string? ResourceVersion)>> listPage,
        ChannelWriter<ResourceEvent<T>> writer,
        CancellationToken ct)
        where T : class
    {
        string? continueToken = null;
        string? resourceVersion = null;
        do
        {
            var (items, next, rv) = await listPage(continueToken, ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                await writer.WriteAsync(new ResourceEvent<T>(ResourceEventType.Added, item), ct).ConfigureAwait(false);
            }

            resourceVersion = rv ?? resourceVersion;
            continueToken = next;
        } while (!string.IsNullOrEmpty(continueToken));

        return resourceVersion;
    }

    /// <summary>
    /// Opens one watch connection and pumps events until it closes. Returns true
    /// when the server signalled that a full relist is required (410 Gone / Error
    /// frame), false when the watch simply ended (idle timeout).
    /// </summary>
    private async Task<bool> StreamWatchAsync<T>(
        string listPath,
        string resourceVersion,
        Func<JsonElement, T?> deserialize,
        Func<T, string?> resourceVersionOf,
        ChannelWriter<ResourceEvent<T>> writer,
        Action<string?> updateResourceVersion,
        CancellationToken ct)
        where T : class
    {
        var query = $"?watch=true&allowWatchBookmarks=true&resourceVersion={Uri.EscapeDataString(resourceVersion)}";
        using var response = await SendStreamingGetAsync(listPath + query, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            return true;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return false; // stream ended cleanly
            }

            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || !root.TryGetProperty("object", out var objEl))
            {
                continue;
            }

            var typeName = typeEl.GetString();
            if (string.Equals(typeName, "ERROR", StringComparison.Ordinal))
            {
                // Expired resourceVersion or similar: caller must relist.
                return true;
            }

            var resource = deserialize(objEl);
            if (resource is null)
            {
                continue;
            }

            updateResourceVersion(resourceVersionOf(resource));

            if (string.Equals(typeName, "BOOKMARK", StringComparison.Ordinal))
            {
                continue; // bookmark only advances resourceVersion
            }

            var mapped = typeName switch
            {
                "ADDED" => ResourceEventType.Added,
                "MODIFIED" => ResourceEventType.Modified,
                "DELETED" => ResourceEventType.Deleted,
                _ => (ResourceEventType?)null,
            };
            if (mapped is { } m)
            {
                await writer.WriteAsync(new ResourceEvent<T>(m, resource), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Streams pod log lines. With follow=true the stream stays open until the
    /// pod goes away or <paramref name="cancellationToken"/> fires — cancellation
    /// is honored mid-stream, not just between lines.
    /// </summary>
    public async IAsyncEnumerable<string> StreamPodLogsAsync(
        string @namespace,
        string podName,
        string? container = null,
        bool follow = true,
        int? tailLines = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/pods/{Uri.EscapeDataString(podName)}/log";
        var query = $"?follow={(follow ? "true" : "false")}";
        if (container is not null)
        {
            query += $"&container={Uri.EscapeDataString(container)}";
        }

        if (tailLines is { } tail)
        {
            query += $"&tailLines={tail}";
        }

        using var response = await SendStreamingGetAsync(path + query, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    /// <summary>
    /// Issues a streaming GET against the client's own HttpClient with
    /// ResponseHeadersRead, applying the client's credentials so exec/token auth
    /// is honored. TLS and client-cert auth already live on the handler chain.
    /// </summary>
    private Task<HttpResponseMessage> SendStreamingGetAsync(string relativePath, CancellationToken ct) =>
        SendRequestAsync(HttpMethod.Get, relativePath, content: null, HttpCompletionOption.ResponseHeadersRead, ct);

    /// <summary>
    /// General-purpose request against the client's own HttpClient/credentials —
    /// the same auth path <see cref="SendStreamingGetAsync"/> uses, generalized
    /// with method/body/completion-option so discovery, generic get/apply/delete
    /// and events (the other <c>ClusterClient.*.cs</c> files) don't need their own
    /// copy of the credential-injection dance.
    /// </summary>
    internal async Task<HttpResponseMessage> SendRequestAsync(
        HttpMethod method, string relativePath, HttpContent? content, HttpCompletionOption completion, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, new Uri(_client.BaseUri, relativePath)) { Content = content };
        if (_client.Credentials is not null)
        {
            await _client.Credentials.ProcessHttpRequestAsync(request, ct).ConfigureAwait(false);
        }

        return await _client.HttpClient.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    /// <summary>Buffered GET returning a parsed JSON document — caller disposes.</summary>
    internal async Task<JsonDocument> GetJsonDocumentAsync(string relativePath, CancellationToken ct)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, relativePath, content: null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
