# The AOT watch/log implementation, discovery, apply, exec and port-forward

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "The AOT watch/log implementation" and "Discovery, server-side apply, events, exec, port-forward" sections keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## The AOT watch/log implementation (important, non-obvious)

`KubernetesClient.Aot` (unlike the reflection client) ships **no `WatchAsync`
helper and no `WatchEventType` enum**. So `ClusterClient` issues watch and
log-follow requests directly against the client's own `Kubernetes.HttpClient`
with `HttpCompletionOption.ResponseHeadersRead`:

- Auth is reused from the client — client-cert/TLS live on the handler chain;
  bearer/exec tokens are applied by calling `Kubernetes.Credentials
  .ProcessHttpRequestAsync` on our manual request. This is what makes exec-plugin
  auth work for watches.
- **A failing exec plugin must be reported by what it printed.** The library ignores
  the plugin's exit code, parses its (empty) stdout as JSON and throws
  `external exec failed due to failed deserialization process: System.Text.Json
  .JsonException…`, stack trace included — which is what a user with the VPN off saw
  instead of "could not reach the sign-in server". Both places a plugin runs (the config
  build in `Kubeconfig.BuildClientConfigAsync`, and the token refresh inside
  `SendRequestAsync`) go through `ExecCredentialCapture.RunAsync`, which collects the
  plugin's stderr through the library's static `ExecStdError` event, routed per connect
  by an `AsyncLocal` because restored tabs connect in parallel, and throws
  `ExecCredentialException`. It waits up to a second for stderr to reach its end,
  because the library's `WaitForExit(timeout)` returns before the stream drains, and
  the first cut lost the very line it existed to show. `GetServerVersionAsync` goes
  through `SendRequestAsync` rather than the generated client for the same reason:
  a VPN or proxy page answering 200 with HTML came out as `'<' is an invalid start of
  a value`. `ConnectFailureTests` pins all of it.
- Watch frames are line-delimited JSON, parsed with `System.Text.Json.JsonDocument`
  (AOT-safe) and materialized with source-generated `KubernetesJson.Deserialize`.
- **Every parse of cluster JSON goes through `ClusterJson`** (depth 256, where
  `JsonDocument`'s default is 64 and the API server accepts about 10,000). One Argo
  Application with a `valuesObject` 65 levels deep used to make every list and watch of the
  kind throw, reported as a lost connection and retried for ever, with every Argo app gone
  from the Applications mode. **One unreadable object does not end a watch**: a list page
  that will not parse whole is read item by item (`ClusterJson.ReadListItems`), and a watch
  frame that will not parse is skipped; either is named through `connectionLost` as an
  `UnreadableObjectException`, which is not a `WatchConnectionException` — nothing was lost,
  and the list does not offer a reconnect for it.
- **Both streams have a line cap** (`BoundedLineReader`, `ClusterClient.Limits.cs`), because
  `ReadLineAsync` holds a line with no newline in memory for as long as it keeps coming. A
  watch frame past 32 MiB ends the stream with a stated error and a relist after the
  backoff; a log line past 1 MiB arrives cut with a marker and the rest of it is dropped
  (see [log-pane-reading](log-pane-reading.md)).
- **A name from another object never builds a path unchecked.** `ResourceDescriptor`'s path
  builders throw for an empty, `.` or `..` segment or one containing `/` or `%` (the API
  server's own rule for names), because `new Uri(base, relative)` collapses dot segments and
  `EscapeDataString` leaves dots alone — an owner reference naming `..` was a GET of the
  namespace. `ReadResourceAsync` answers such a name with null and sends nothing; every write
  refuses an invalid name before building anything; `ResolveOwnerAsync` and the named-logs
  path return the object only when its apiVersion, kind, name and UID match the reference.
  Discovery drops a group, version or plural that could not be a path segment. See
  [events-list](events-list.md).
- The informer loop lives in `ClusterClient.PumpAsync`/`StreamWatchAsync`:
  paginated initial list (Reset + Added per item) → resumable watch →
  relist on `ERROR` frame / 410 Gone → exponential backoff with
  `connectionLost` callback on transient failures.
- **A 401 is not a transient failure.** It means the credential expired or was revoked,
  and retrying with it fails the same way for ever — which the loop used to do. On a 401
  it calls `ClusterClient.RefreshCredentialsAsync` (re-read the kubeconfig, re-run the
  plugin, swap the generated client inside the same `ClusterClient`), reports a
  `WatchConnectionException` with `CredentialsRejected`, relists and retries. The swap is
  why a reconnect reaches every pane without any of them holding a new object; the
  replaced client is retired rather than disposed so open streams survive it. Never cache
  what the plugin returned instead — hard rule 4. See
  [connecting](connecting.md).
- **`_client` is replaced, so read it once per operation** when an operation touches it
  more than once. Mixing two generated clients for the same server within one request is
  harmless; a new file that holds on to `_client` across awaits for its own lifetime is not.

If you add a new **typed** watched resource, reuse the generic `WatchAsync<T>`
core; only supply the list path, a paged lister, and a
`KubernetesJson.Deserialize<T>` delegate. For **any resource kind discovered at
runtime** (CRDs included — there's no compile-time type for those), use
`ClusterClient.WatchResourceAsync(ResourceDescriptor, ...)` instead: it runs
the same engine with `DynamicResource` (a JsonElement-backed wrapper, see
`DynamicResource.cs`) as `T`. The sidebar/list view always goes through this
generic path — pods included — so there's exactly one live-list code path in
the App layer.

**A watch reaches the UI thread in batches, never one hop per event.** Every live list
reads its watch through `AsyncBatching.InBatches` (Core) and applies each batch inside one
`Dispatcher.UIThread.InvokeAsync`. An initial list is one event per object, back to back,
and the per-event hop made 5,000 pods 5,000 serial dispatcher jobs, each followed by a
layout of the grid it had just changed; the Applications list posted its events unawaited
and queued 15,000 jobs ahead of input and rendering. The batch is whatever has already
arrived, so a lone Modified is still delivered at once, and the source's error comes after
every event before it. `ClusterTabViewModel.ApplyBatch` appends a batch's new rows in one
notification and has exactly the effect of the same events applied one at a time
(`ClusterTabBatchApplyTests` pins that, through a filter and a sort).

## Discovery, server-side apply, events, exec, port-forward

- **Discovery** (`ClusterClient.Discovery.cs`) negotiates aggregated discovery at
  `/api` and `/apis`, preferring `apidiscovery.k8s.io/v2`, then `v2beta1`.
  Current aggregated responses supply the catalog in two requests. Legacy or stale
  responses use the bounded per-group fallback (16 requests at once).
  Descriptors preserve verbs, subresources, short names and namespace scope.
  A kind is listed when its `verbs` name `list` or are absent; present without `list`
  — `"verbs": []` included — is the server saying no. Both parses share
  `ClusterClient.IsListable`, and `DiscoveryVerbsTests` pins it (ENG-8): an absent
  array means "did not say", which every capability check already reads as "offer it".
  The first advertised version is the preferred version. Raw `JsonDocument`
  parsing keeps this path compatible with NativeAOT.
  `DiscoveryCache` stores only descriptors in the local app-data directory.
  Its key hashes the server URL, context, user name and kubeconfig path.
  Entries expire after six hours or a server-version change. Corrupt files are
  cache misses. Partial discovery results never replace the disk cache.
  The sidebar context menu offers **Refresh resource catalog**, which bypasses
  the cache. `DiscoveryHttpTests` covers negotiation, fallback concurrency,
  warm connections and version invalidation.
  `ConnectAsync` first reads the version to resolve exec credentials once.
  It then starts discovery, namespaces and the metrics probe together.
  After namespace resolution, the initial Pods watch starts with the known
  core/v1 descriptor. Discovery replaces the temporary sidebar entry without
  restarting the watch or clearing rows. Saved non-Pod kinds wait for discovery.
  Discovery errors leave an early Pods watch connected and show a warning.
  Restored cluster tabs also connect in parallel.
  Discovery says nothing about how a kind should be *printed*, which is why a CRD's
  own columns come from a separate GET of the CustomResourceDefinition — see "CRD
  printer columns" below.
- **Server-side apply** (`ClusterClient.Dynamic.cs`) PATCHes with
  `Content-Type: application/apply-patch+yaml`; the body is JSON (valid JSON
  is valid YAML, so the API server's apply decoder accepts it) produced by
  `YamlJson.cs`. That file uses YamlDotNet's **structural** `RepresentationModel`
  (`YamlNode`/`YamlStream`) to convert YAML ⇄ JSON — never YamlDotNet's
  attribute/reflection-based (de)serializer, which is not AOT/trim-safe and
  can't handle arbitrary CRD shapes anyway. A 409 conflict raises
  `ServerSideApplyConflictException` for the UI to offer a force-apply retry.
- **Exec** (`ClusterClient.Exec.cs`) uses `Kubernetes.MuxedStreamNamespacedPodExecAsync`
  — the one exec helper `KubernetesClient.Aot` *does* ship, because it's
  WebSocket-based rather than SPDY and needed no reflection-based transport. What the
  App layer does with those bytes is a VT emulator now; see "The exec terminal".
- **Port-forward** (`ClusterClient.PortForward.cs`) has no equivalent helper,
  so it opens a raw `WebSocketNamespacedPodPortForwardAsync` websocket per
  accepted local TCP connection (matching kubectl's own approach — the k8s
  websocket port-forward channel framing doesn't support multiplexing several
  local clients over one upstream connection) and pumps bytes with the
  channel-byte-prefix framing by hand.
