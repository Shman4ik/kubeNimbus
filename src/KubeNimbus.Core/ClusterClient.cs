using System.Net;
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

    /// <summary>
    /// The generated client every request goes through. Replaced, never mutated, by
    /// <see cref="RefreshCredentialsAsync"/> — which is how a tab re-resolves its
    /// credentials without a new <see cref="ClusterClient"/>: every inspector pane, fleet
    /// member and palette source holds <em>this</em> object, so swapping what is inside it
    /// reaches all of them at once, and none of them has to learn that a reconnect exists.
    /// </summary>
    private volatile Kubernetes _client;

    /// <summary>Clients replaced by a refresh, kept alive so streams already open on them are not cut.</summary>
    private readonly List<Kubernetes> _retired = [];

    private readonly Lock _gate = new();
    private Task? _refreshing;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// How many replaced clients are kept for the streams still open on them. A credential
    /// that stays broken is refreshed on every retry of every watch; past this the oldest
    /// is disposed, which ends whatever was still reading from it.
    /// </summary>
    private const int MaxRetiredClients = 4;

    /// <summary>
    /// Watches that hit a 401 at the same moment (the list, the metrics poll, the
    /// Applications mode's reads) share one plugin run rather than each starting their own.
    /// </summary>
    private static readonly TimeSpan RefreshCoalesceWindow = TimeSpan.FromSeconds(5);

    public ClusterContext Context { get; }

    /// <summary>The proxy from the cluster's <c>proxy-url</c>, or null when requests go direct. Tests read it.</summary>
    internal IWebProxy? Proxy { get; private set; }

    private ClusterClient(ClusterContext context, ClientSetup setup)
    {
        Context = context;
        _client = Create(setup);
        Proxy = setup.Proxy;
    }

    /// <summary>
    /// A generated client for <paramref name="setup"/>, with the proxy applied to the
    /// WebSocket transport as well: exec and port-forward open a <c>ClientWebSocket</c> of
    /// their own, which the HTTP handler's proxy never reaches.
    /// </summary>
    private static Kubernetes Create(ClientSetup setup)
    {
        var client = new Kubernetes(setup.Configuration);
        if (setup.Proxy is { } proxy)
        {
            client.CreateWebSocketBuilder = () =>
            {
                var builder = new WebSocketBuilder();
                builder.Options.Proxy = proxy;
                return builder;
            };
        }

        return client;
    }

    /// <summary>
    /// Re-reads the kubeconfig and re-runs its credential plugin, then routes every later
    /// request through a client built from the result. The answer to an expired session:
    /// after <c>aws sso login</c> in a terminal, this is what picks the new credential up
    /// without closing the tab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hard rule 4 is why this re-runs rather than remembers. Nothing the plugin returned
    /// is kept anywhere but inside the configuration it was built into — the same as the
    /// first connect.
    /// </para>
    /// <para>
    /// It also covers what the library does not: a plugin that returns a client
    /// <em>certificate</em> gets no refresh provider upstream (only token-returning plugins
    /// do), so before this such a connection went stale for the life of the tab.
    /// </para>
    /// <para>
    /// Single-flight, and without <paramref name="force"/> a refresh that finished within
    /// the last few seconds counts: several watches failing together must not start one
    /// SSO prompt each. Streams already open on the old client are left to finish; it is
    /// disposed with this object.
    /// </para>
    /// </remarks>
    public Task RefreshCredentialsAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_refreshing is { IsCompleted: false } running)
            {
                return running.WaitAsync(cancellationToken);
            }

            if (!force && DateTimeOffset.UtcNow - _lastRefresh < RefreshCoalesceWindow)
            {
                return _refreshing ?? Task.CompletedTask;
            }

            _refreshing = RefreshCoreAsync();
            return _refreshing.WaitAsync(cancellationToken);
        }
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var setup = await Kubeconfig.BuildClientSetupAsync(Context).ConfigureAwait(false);
            var fresh = Create(setup);
            lock (_gate)
            {
                if (_disposed)
                {
                    fresh.Dispose();
                    return;
                }

                _retired.Add(_client);
                _client = fresh;
                Proxy = setup.Proxy;
                while (_retired.Count > MaxRetiredClients)
                {
                    _retired[0].Dispose();
                    _retired.RemoveAt(0);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _lastRefresh = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the API server refusing the credentials —
    /// a 401 from either the hand-rolled requests or the generated client.
    /// </summary>
    public static bool IsUnauthorized(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => true,
        k8s.Autorest.HttpOperationException { Response.StatusCode: HttpStatusCode.Unauthorized } => true,
        _ => false,
    };

    /// <summary>
    /// Builds a client for the given context. Credentials (including exec
    /// plugins) are resolved through the kubeconfig chain right now, never
    /// from app storage.
    /// </summary>
    /// <remarks>
    /// Blocks the calling thread while the kubeconfig is read and any exec plugin
    /// runs, so it is for tests and tooling only. Never call it on the UI thread:
    /// that is what hung the NativeAOT build at startup — see
    /// <see cref="Kubeconfig.BuildClientConfigAsync"/>. The app uses
    /// <see cref="ConnectAsync"/>.
    /// </remarks>
    public static ClusterClient Connect(ClusterContext context) =>
        new(context, Kubeconfig.BuildClientSetup(context));

    /// <summary>
    /// <see cref="Connect"/> without blocking the caller: the kubeconfig read,
    /// certificate loading and any exec credential plugin all run on the thread pool.
    /// </summary>
    public static async Task<ClusterClient> ConnectAsync(
        ClusterContext context,
        CancellationToken cancellationToken = default)
    {
        var setup = await Kubeconfig.BuildClientSetupAsync(context, cancellationToken).ConfigureAwait(false);
        return new ClusterClient(context, setup);
    }

    /// <summary>Raw generated client, for tests only. App code goes through typed methods.</summary>
    internal Kubernetes Api => _client;

    private string? _serverVersion;

    /// <summary>
    /// <c>GET /version</c> — the connect path's reachability check.
    /// </summary>
    /// <remarks>
    /// Through <see cref="SendRequestAsync"/> rather than the generated
    /// <c>Version.GetCodeAsync</c>, because this is the first request a connect makes
    /// and therefore the one that meets whatever sits between the app and a cluster it
    /// cannot reach. The generated call reported a VPN or proxy sign-in page answering
    /// 200 with HTML as <c>'&lt;' is an invalid start of a value</c>, and a 502 as
    /// <c>Operation returned an invalid status code 'BadGateway'</c>; both now say what
    /// answered instead.
    /// </remarks>
    public async Task<VersionInfo> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, "version", content: null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        VersionInfo? version;
        try
        {
            version = KubernetesJson.Deserialize<VersionInfo>(body);
        }
        catch (JsonException)
        {
            version = null;
        }

        if (version is not { GitVersion.Length: > 0 })
        {
            throw NotAnApiServer(response, body);
        }

        if (_serverVersion != version.GitVersion) _resourceCatalog = null;
        _serverVersion = version.GitVersion;
        return version;
    }

    /// <summary>
    /// Something answered <c>/version</c> successfully, but not with a Kubernetes
    /// version — a VPN portal, a corporate proxy's sign-in page, or the wrong port.
    /// </summary>
    private HttpRequestException NotAnApiServer(HttpResponseMessage response, string body)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var head = body.Trim();
        head = head.Length == 0 ? "an empty response" : head.Length > 120 ? $"\"{head[..120]}…\"" : $"\"{head}\"";
        return new HttpRequestException(
            $"{_client.BaseUri.GetLeftPart(UriPartial.Authority)} answered, but not as a Kubernetes API server "
            + $"({(contentType is null ? "" : contentType + ": ")}{head}). A VPN, proxy or sign-in page may be in the way.");
    }

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
    /// <paramref name="extraQuery"/> is appended verbatim to the watch request's own
    /// query string (it must already start with <c>&amp;</c> and be escaped); the
    /// paginated list applies the same narrowing through <paramref name="listPage"/>.
    /// </summary>
    private async IAsyncEnumerable<ResourceEvent<T>> WatchAsync<T>(
        string listPath,
        Func<string?, CancellationToken, Task<(IList<T> Items, string? Continue, string? ResourceVersion)>> listPage,
        Func<JsonElement, T?> deserialize,
        Func<T, string?> resourceVersionOf,
        Action<Exception>? connectionLost,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string extraQuery = "")
        where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<ResourceEvent<T>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var pump = PumpAsync(listPath, listPage, deserialize, resourceVersionOf, channel.Writer, connectionLost, extraQuery, linked.Token);
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
        string extraQuery,
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

                        // The initial list is complete. Reset was the *start* of it, so
                        // this is the only frame that can honestly end a "loading" state:
                        // an empty namespace produces a Reset and no Added at all, so a
                        // consumer with nothing else to wait for is left choosing between
                        // a spinner that never stops and an empty list rendered while the
                        // list request is still in flight (UI rule 18).
                        await writer.WriteAsync(ResourceEvent<T>.Synced, ct).ConfigureAwait(false);
                        needRelist = false;
                        backoff = InitialBackoff;
                    }

                    var relist = await StreamWatchAsync(
                        listPath, resourceVersion!, deserialize, resourceVersionOf, writer,
                        rv => resourceVersion = rv, extraQuery, connectionLost, ct).ConfigureAwait(false);
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
                catch (WatchFrameTooLargeException ex)
                {
                    // Said in its own words rather than as a lost connection, and relisted after
                    // the backoff rather than at once: a server that sends the same frame again
                    // must not turn this into a tight loop.
                    connectionLost?.Invoke(new UnreadableObjectException(ex.Message));
                    needRelist = true;
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                }
                catch (Exception ex) when (IsUnauthorized(ex))
                {
                    // A 401 is not a transient fault: retrying with the same credential
                    // fails the same way for ever, which is what this loop used to do. It
                    // means the credential has expired or been revoked, so the kubeconfig is
                    // read again and its plugin re-run before the retry — and a sign-in done
                    // in a terminal meanwhile is picked up by the next attempt, with no
                    // reconnect and no closed tab. Relisting afterwards, because whatever
                    // happened while the watch was refused was not seen.
                    string outcome;
                    try
                    {
                        await RefreshCredentialsAsync(force: false, ct).ConfigureAwait(false);
                        outcome = "re-read the kubeconfig";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception refresh)
                    {
                        outcome = $"re-reading the kubeconfig failed: {Describe(refresh)}";
                    }

                    connectionLost?.Invoke(new WatchConnectionException(
                        $"The cluster rejected the credentials (401) — they have probably expired. kubeNimbus {outcome}; retrying in {backoff.TotalSeconds:0}s. Sign in again and the retry picks it up.",
                        ex,
                        credentialsRejected: true));
                    needRelist = true;
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                }
                catch (Exception ex)
                {
                    connectionLost?.Invoke(new WatchConnectionException(
                        $"Watch connection lost ({Describe(ex)}); retrying in {backoff.TotalSeconds:0}s.", ex));
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

    /// <summary>
    /// What to put in the disconnected banner. The API server's own sentence
    /// when we have one ("pods is forbidden: …"); a bare exception type name
    /// otherwise, which is all a transport failure can honestly offer.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        KubernetesApiException { ServerMessage: { } serverMessage } => serverMessage,

        // A token refresh that ran the plugin mid-session and failed: its own sentence
        // ("could not reach login.example.com") is the diagnosis, and the bare type name
        // this used to print was the one thing in the banner nobody could act on.
        ExecCredentialException or KubeconfigSetupException => ex.Message,
        _ => ex.GetType().Name,
    };

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
        string extraQuery,
        Action<Exception>? connectionLost,
        CancellationToken ct)
        where T : class
    {
        var query = $"?watch=true&allowWatchBookmarks=true&resourceVersion={Uri.EscapeDataString(resourceVersion)}{extraQuery}";
        using var response = await SendStreamingGetAsync(listPath + query, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            return true;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var reader = new BoundedLineReader(stream, MaxWatchFrameBytes, discardOverflow: false);

        while (true)
        {
            if (await reader.ReadLineAsync(ct).ConfigureAwait(false) is not { } line)
            {
                return false; // stream ended cleanly
            }

            if (line.Truncated)
            {
                // One frame is one object, and no object an API server stores comes near the
                // cap. Reading on would mean holding an unbounded line in memory; reading past
                // it would mean resuming mid-frame. Neither, so the stream ends here and the
                // pump relists after its backoff.
                throw new WatchFrameTooLargeException(
                    $"A watch frame was larger than {MaxWatchFrameBytes / (1024 * 1024)} MiB"
                    + $" ({ClusterJson.NameOf(line.Bytes.Span)}); kubeNimbus does not read frames that large.");
            }

            if (line.Bytes.IsEmpty)
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = ClusterJson.Parse(line.Bytes);
            }
            catch (JsonException ex)
            {
                // One object the app cannot read — nested past the depth limit, or not JSON —
                // costs that object, not the watch. It is said, and the stream goes on.
                connectionLost?.Invoke(new UnreadableObjectException(
                    $"Skipped {ClusterJson.Describe(line.Bytes.Span, ex)}: kubeNimbus could not read it."));
                continue;
            }

            using var owned = doc;
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || !root.TryGetProperty("object", out var objEl))
            {
                continue;
            }

            var typeName = typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() : null;
            if (string.Equals(typeName, "ERROR", StringComparison.Ordinal))
            {
                // Expired resourceVersion or similar: caller must relist.
                return true;
            }

            T? resource;
            try
            {
                resource = deserialize(objEl);
            }
            catch (JsonException ex)
            {
                connectionLost?.Invoke(new UnreadableObjectException(
                    $"Skipped {ClusterJson.Describe(line.Bytes.Span, ex)}: kubeNimbus could not read it."));
                continue;
            }

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
    /// is honored mid-stream, not just between lines. <paramref name="previous"/>
    /// fetches the prior (crashed/restarted) container instance's logs instead
    /// of the current one — the API server rejects follow=true with previous=true,
    /// so callers should pass follow=false alongside it. <paramref name="sinceSeconds"/>
    /// requests lines since the given number of seconds ago; omit both range arguments
    /// to request everything the kubelet still retains. <paramref name="timestamps"/>
    /// asks the server to prefix each line with an RFC3339 timestamp; the caller
    /// decides whether to display it (a client-side toggle can strip the prefix
    /// without needing to re-stream). <paramref name="responseReady"/> fires after
    /// successful HTTP headers arrive, even when a follow has not yielded a line.
    /// </summary>
    public async IAsyncEnumerable<string> StreamPodLogsAsync(
        string @namespace,
        string podName,
        string? container = null,
        bool follow = true,
        int? tailLines = null,
        int? sinceSeconds = null,
        bool previous = false,
        bool timestamps = false,
        Action? responseReady = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/namespaces/{ResourceDescriptor.PathSegment(@namespace, "namespace")}/pods/{ResourceDescriptor.PathSegment(podName)}/log";
        var query = $"?follow={(follow ? "true" : "false")}";
        if (container is not null)
        {
            query += $"&container={Uri.EscapeDataString(container)}";
        }

        if (tailLines is { } tail)
        {
            query += $"&tailLines={tail}";
        }

        if (sinceSeconds is { } since)
        {
            query += $"&sinceSeconds={since}";
        }

        if (previous)
        {
            query += "&previous=true";
        }

        if (timestamps)
        {
            query += "&timestamps=true";
        }

        using var response = await SendStreamingGetAsync(path + query, cancellationToken).ConfigureAwait(false);

        // The two most common log failures on call — "previous terminated
        // container \"app\" in pod \"x\" not found" and "container \"app\" is
        // waiting to start: ContainerCreating" — are both plain 400s whose only
        // distinguishing content is the Status body. EnsureSuccessStatusCode
        // throws before reading it and leaves the user with "400 (Bad Request)".
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        responseReady?.Invoke();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var reader = new BoundedLineReader(stream, MaxLogLineBytes);

        while (true)
        {
            if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not { } line)
            {
                yield break;
            }

            yield return line.Truncated ? TruncatedLogLine(line.Bytes.Span) : line.Text;
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
        HttpMethod method, string relativePath, HttpContent? content, HttpCompletionOption completion, CancellationToken ct,
        string? accept = null)
    {
        // Read once: RefreshCredentialsAsync can replace the generated client between any
        // two reads, and one request should carry one client's credentials to that same
        // client's handler.
        var client = _client;
        var request = new HttpRequestMessage(method, new Uri(client.BaseUri, relativePath)) { Content = content };
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        if (client.Credentials is { } credentials)
        {
            // An expired exec token is refreshed here, by running the plugin again —
            // so a plugin failing mid-session (VPN dropped) is translated the same way
            // as one failing at connect.
            await ExecCredentialCapture.RunAsync(() => credentials.ProcessHttpRequestAsync(request, ct)).ConfigureAwait(false);
        }

        return await client.HttpClient.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    /// <summary>Buffered GET returning a parsed JSON document — caller disposes.</summary>
    internal async Task<JsonDocument> GetJsonDocumentAsync(string relativePath, CancellationToken ct)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get, relativePath, content: null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ClusterJson.ParseAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="KubernetesApiException"/> carrying the API server's own
    /// <c>message</c> when <paramref name="response"/> failed.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> throws *before*
    /// the body is read, so everything the server said about why is discarded:
    /// the user gets "Response status code does not indicate success: 403
    /// (Forbidden)" instead of <c>secrets "db-creds" is forbidden: User "x"
    /// cannot get resource "secrets" in namespace "y"</c>, which names the
    /// object, the subject and the fix. Every failure the API server produces
    /// carries a <c>Status</c> body; it is parsed with <see cref="JsonDocument"/>
    /// for the same reason watch frames are — AOT-safe, and the shape is one
    /// field deep. Success is left untouched: the body may be a stream this
    /// method must not consume.
    /// </remarks>
    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable error body must not replace the status code with a
            // different, less useful exception.
            body = "";
        }

        throw KubernetesApiException.From(response.StatusCode, response.ReasonPhrase, body);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var retired in _retired)
            {
                retired.Dispose();
            }

            _retired.Clear();
            _client.Dispose();
        }
    }
}

/// <summary>
/// A non-success response from the API server, carrying the <c>message</c> out
/// of its <c>Status</c> body in <see cref="Exception.Message"/> and
/// <see cref="ServerMessage"/>.
/// </summary>
/// <remarks>
/// It derives from <see cref="HttpRequestException"/> deliberately: the informer
/// loop's 410-Gone relist branch and discovery's "this group vanished mid-walk"
/// guard both key off that type and its <see cref="HttpRequestException.StatusCode"/>,
/// and so do the App layer's generic catches — this must be strictly more
/// informative than what it replaced, never a new type they miss.
/// </remarks>
public sealed class KubernetesApiException : HttpRequestException
{
    private KubernetesApiException(
        string message, System.Net.HttpStatusCode statusCode, string? serverMessage, string body)
        : base(message, inner: null, statusCode)
    {
        ServerMessage = serverMessage;
        Body = body;
    }

    /// <summary>The server's own explanation, or null when the body wasn't a Status.</summary>
    public string? ServerMessage { get; }

    /// <summary>The raw response body, for a details view. Empty when it couldn't be read.</summary>
    public string Body { get; }

    internal static KubernetesApiException From(System.Net.HttpStatusCode statusCode, string? reasonPhrase, string body)
    {
        var serverMessage = ReadStatusMessage(body);
        var statusText = $"{(int)statusCode} {reasonPhrase ?? statusCode.ToString()}";

        // Server message first: it is the sentence a user acts on, and the code
        // is context, not the headline.
        return new KubernetesApiException(
            serverMessage is null ? statusText : $"{serverMessage} ({statusText})",
            statusCode,
            serverMessage,
            body);
    }

    /// <summary>
    /// Pulls <c>message</c> out of a Kubernetes <c>Status</c> body. A body that
    /// isn't JSON (a proxy's HTML error page, say) is still worth showing, but
    /// only its head — an unbounded blob in an exception message is unreadable.
    /// </summary>
    internal static string? ReadStatusMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = ClusterJson.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString())
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            var trimmed = body.Trim();
            return trimmed.Length <= MaxNonJsonBodyChars ? trimmed : trimmed[..MaxNonJsonBodyChars] + "…";
        }
    }

    private const int MaxNonJsonBodyChars = 500;
}
