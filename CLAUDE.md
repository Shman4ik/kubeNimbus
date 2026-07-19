# kubeNimbus — Claude working notes

Keep this file current in **every** PR, same discipline as pgNimbus. It is the
contract for how this repo is built; if a rule below changes, change it here in
the same change that breaks it.

## Mission

A fast, open-source (MIT) Kubernetes desktop client — the Kubernetes sibling of
[pgNimbus](https://github.com/Shman4ik/pgNimbus). An alternative to Lens.

The 2026 Kubernetes GUI market has the same hole the PostgreSQL GUI market had:
Lens is subscription-only for commercial use (Mirantis moved exec/logs/shell
into proprietary code in 6.3) and a heavy Electron app; OpenLens is dead;
FreeLens (the surviving fork) is still Electron; Aptakube is fast and polished
but paid/closed; Headlamp is web-first; k9s is a keyboard TUI. Nobody ships
**truly fast + open source + modern native desktop UI**. kubeNimbus fills that
gap: Aptakube's polish, NativeAOT startup speed, MIT licensed, Kubernetes-first.

**Headline benchmark:** ~150 ms to first frame (vs Electron's seconds). NativeAOT
publish is the *shipping* configuration, not an afterthought — every dependency
choice must be AOT/trimming-compatible from day one.

## Tech stack

- **net10.0** everywhere. NativeAOT is the shipping config.
- **KubeNimbus.Core** — references ONLY the official Kubernetes client, via the
  **`KubernetesClient.Aot`** package (source-generated serialization). NEVER swap
  it for the reflection-based `KubernetesClient` — that one does not survive
  NativeAOT.
- **KubeNimbus.App** — Avalonia 12 (Fluent theme, Inter font, DataGrid,
  AvaloniaEdit for YAML), `CommunityToolkit.Mvvm` source generators
  (`[ObservableProperty]`/`[RelayCommand]`, no hand-written INPC).
  `AvaloniaUseCompiledBindingsByDefault=true`; no reflection bindings.
- **KubeNimbus.Core.Tests** — TUnit on Microsoft.Testing.Platform. **NEVER add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks discovery.** The
  runner is pinned in `global.json` (`test.runner = Microsoft.Testing.Platform`).
- Nullable enabled; async all the way (no `.Result`/`.Wait()`); DTOs are records.

## Hard architectural rules (non-negotiable)

1. **KubeNimbus.Core has ZERO Avalonia/UI dependencies.** The engine stays
   reusable for a future CLI/test harness. No `Avalonia.*` or
   `CommunityToolkit.Mvvm` types in Core.
2. **Streaming + cancellation everywhere.** Resource lists use list+watch
   (informer-style local cache) so the UI updates live without polling; large
   lists paginate via `continue` tokens and render incrementally via
   `IAsyncEnumerable`. Pod logs stream with follow-mode honoring
   `CancellationToken` mid-stream. Watch connections auto-reconnect with
   resourceVersion resume + relist on 410 Gone; connection loss is surfaced in
   the UI, never a silent hang.
3. **Kubernetes-native, not lowest-common-denominator.** CRDs are first-class
   browsable resources (discovery API, not a hardcoded list). YAML edits go
   through server-side apply with a field manager, showing conflicts. Events,
   `metrics.k8s.io`, and owner-reference navigation (pod → replicaset →
   deployment) are core, not afterthoughts.
4. **No credentials ever persisted by the app.** Kubeconfig is the single source
   of truth (all `$KUBECONFIG` entries + `~/.kube/config`); exec-plugin auth
   (`aws eks get-token`, `gke-gcloud-auth-plugin`, `azure kubelogin`) must work.
   Never copy tokens/certs into app storage; re-resolve through the kubeconfig
   chain at connect time.

## UI design rules

1. **Minimalist.** Every always-visible control must be justified; default answer
   is no. Secondary actions live in a command palette (Ctrl+K) or context menus.
2. **Double-click = default action** everywhere (pod → logs/describe, deployment
   → details, context → connect); Space = quick-peek.
3. **Multi-cluster via tabs** (like pgNimbus query tabs): each tab bound to a
   kubeconfig context; drag-reorder; workspace snapshot restores tabs.
4. **No hardcoded Ctrl gestures** — [`Hotkeys.cs`](src/KubeNimbus.App/Hotkeys.cs)
   resolves Ctrl vs Cmd per platform; palette labels and cheat sheet derive
   from it.
5. **Opening a resource/YAML never overwrites an active editor tab.**

## Repository layout

```
src/KubeNimbus.Core        Engine: kubeconfig, ClusterClient (watch/logs). No UI.
src/KubeNimbus.App         Avalonia 12 desktop shell.
tests/KubeNimbus.Core.Tests  TUnit integration tests against a live cluster.
```

## The AOT watch/log implementation (important, non-obvious)

`KubernetesClient.Aot` (unlike the reflection client) ships **no `WatchAsync`
helper and no `WatchEventType` enum**. So `ClusterClient` issues watch and
log-follow requests directly against the client's own `Kubernetes.HttpClient`
with `HttpCompletionOption.ResponseHeadersRead`:

- Auth is reused from the client — client-cert/TLS live on the handler chain;
  bearer/exec tokens are applied by calling `Kubernetes.Credentials
  .ProcessHttpRequestAsync` on our manual request. This is what makes exec-plugin
  auth work for watches.
- Watch frames are line-delimited JSON, parsed with `System.Text.Json.JsonDocument`
  (AOT-safe) and materialized with source-generated `KubernetesJson.Deserialize`.
- The informer loop lives in `ClusterClient.PumpAsync`/`StreamWatchAsync`:
  paginated initial list (Reset + Added per item) → resumable watch →
  relist on `ERROR` frame / 410 Gone → exponential backoff with
  `connectionLost` callback on transient failures.

If you add a new **typed** watched resource, reuse the generic `WatchAsync<T>`
core; only supply the list path, a paged lister, and a
`KubernetesJson.Deserialize<T>` delegate. For **any resource kind discovered at
runtime** (CRDs included — there's no compile-time type for those), use
`ClusterClient.WatchResourceAsync(ResourceDescriptor, ...)` instead: it runs
the same engine with `DynamicResource` (a JsonElement-backed wrapper, see
`DynamicResource.cs`) as `T`. The sidebar/list view always goes through this
generic path — pods included — so there's exactly one live-list code path in
the App layer.

## Discovery, server-side apply, events, exec, port-forward

- **Discovery** (`ClusterClient.Discovery.cs`) walks `/api` and `/apis` with
  raw `JsonDocument` parsing (same reasoning as watch frames — no source-gen
  model needed for a shape this simple) into `ResourceDescriptor` records.
  `SidebarGrouping` (App layer) buckets each descriptor into
  Workloads/Network/Config/Storage/CRDs by Kind — an unrecognized API group
  falls through to CRDs automatically, nothing is hardcoded.
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
  WebSocket-based rather than SPDY and needed no reflection-based transport.
- **Port-forward** (`ClusterClient.PortForward.cs`) has no equivalent helper,
  so it opens a raw `WebSocketNamespacedPodPortForwardAsync` websocket per
  accepted local TCP connection (matching kubectl's own approach — the k8s
  websocket port-forward channel framing doesn't support multiplexing several
  local clients over one upstream connection) and pumps bytes with the
  channel-byte-prefix framing by hand.

## Sandbox cluster bootstrap (how tests get a real cluster)

Integration tests run against a **real local Kubernetes cluster**, not mocks.
The suite auto-discovers `./.sandbox/kubeconfig.yaml` (git-ignored — it holds
cluster CA + client certs) or `$KUBENIMBUS_TEST_KUBECONFIG`. Tests **skip
cleanly** (return) when no cluster is reachable, so CI without one stays green.

**Recipe (k3s in Docker — used to develop this repo; Docker Desktop required):**

```bash
# 1. Start a single-node k3s cluster, API on localhost:6550.
docker run -d --name kubenimbus-sandbox --privileged -p 6550:6443 \
  rancher/k3s:v1.33.4-k3s1 server --tls-san 127.0.0.1

# 2. Export its kubeconfig into the repo (git-ignored) and point it at :6550.
mkdir -p .sandbox
docker exec kubenimbus-sandbox cat /etc/rancher/k3s/k3s.yaml > .sandbox/kubeconfig.yaml
#   then replace  https://127.0.0.1:6443  with  https://127.0.0.1:6550  in that file.

# 3. Verify.
KUBECONFIG=.sandbox/kubeconfig.yaml kubectl get pods -A
```

`kind` works equally well if you prefer it (`kind create cluster`, then
`kind get kubeconfig > .sandbox/kubeconfig.yaml`). Tear down k3s with
`docker rm -f kubenimbus-sandbox`.

## Verification workflow

```powershell
# Build everything.
dotnet build KubeNimbus.slnx

# Run Core tests against the sandbox cluster (skips if none).
dotnet test tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj

# Run the app against the sandbox during development.
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml"
dotnet run --project src/KubeNimbus.App

# NativeAOT publish — THE shipping build. Verify it end-to-end on every change
# that could affect trimming/AOT (new package, new reflection, new binding).
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app
```

### NativeAOT publish needs the MSVC toolchain (Windows)

The ILCompiler links with `link.exe` and locates it via `vswhere.exe`. On this
machine the raw `dotnet publish -p:PublishAot=true` fails with
`'vswhere.exe' is not recognized` unless run from a VC dev environment **with the
VS Installer dir on PATH**. Working invocation:

```bat
call "C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src\KubeNimbus.App\KubeNimbus.App.csproj -c Release -r win-x64 -p:PublishAot=true -o publish\app
```

Known AOT warnings today: `Avalonia.Controls.DataGrid` emits IL2104/IL3053 trim
warnings. The publish still succeeds and the app runs; revisit if DataGrid gets
an AOT-clean release. Do not let *new* trim/AOT warnings from our own code slip
in unnoticed.

### DevTools / visual inspection

`KubeNimbus.App` references `AvaloniaUI.DiagnosticsSupport` **Debug-only** and
calls `WithDeveloperTools()` under `#if DEBUG`, so the Avalonia DevTools MCP can
attach to a running Debug build and screenshot/inspect the tree. It never enters
the Release/AOT build.

## MVP scope (phase 1 — shipped, see Current status below)

- [x] Context picker from kubeconfig (exec-plugin auth working).
- [x] Live-updating pod list (watch) — proven end-to-end in the app.
- [x] Sidebar tree (Workloads/Network/Config/Storage/CRDs via discovery),
      namespace-scoped, live list views.
- [x] Pod detail: containers, status, live log streaming (follow, container
      picker, cancel), events.
- [x] YAML view/edit for any resource → server-side apply; delete with confirm.
- [x] Exec into a pod container (interactive terminal) and port-forward.
- [x] Command palette (Ctrl/Cmd+K); light/dark theme.
- [x] Multi-cluster context tabs (drag-reorder, workspace-restore).
- [x] Owner-reference navigation (pod → replicaset → deployment, etc.).
- [x] pgNimbus visual design system ported (Theme.axaml, two-tone shell,
      brand-blue accent, MDI icon vectors).

**Later phases (do NOT build now, but don't paint into a corner):** Helm release
browsing, resource metrics/graphs, RBAC inspection, multi-cluster aggregated
views.

**Non-goals forever:** cluster provisioning, in-cluster agents, telemetry.

## Current status

**Phase-1 MVP shipped.** Core `ClusterClient` covers kubeconfig load/connect,
typed pod list+watch, cancellable log streaming, discovery (`/api` + `/apis`
walk), a generic CRD-capable list+watch (`WatchResourceAsync`/`DynamicResource`),
server-side apply with conflict surfacing, generic delete, events-for-resource,
owner-reference resolution, interactive exec and port-forward — all proven by
12 TUnit integration tests against a live k3s cluster (12/12 passing).

The Avalonia shell wears pgNimbus's design system (Theme.axaml: brand-blue
accent, two-tone Mica/AcrylicBlur shell, card/layer/pill-nav/status-dot
classes) and now has: multi-cluster drag-reorderable context tabs with
workspace persistence; a discovery-driven sidebar (Workloads/Network/
Config/Storage/CRDs — verified against real cluster CRDs, not just built-ins);
a generic namespace-scoped/all-namespaces live list; a pod detail pane
(containers, live logs, events, owner-chip navigation); a YAML editor
(AvaloniaEdit) with apply/reload/two-step-delete; exec and port-forward panes;
and a Ctrl/Cmd+K command palette. Verified end-to-end running against the
sandbox (screenshotted via the Avalonia DevTools MCP) and via NativeAOT
publish (0 new warnings beyond the known DataGrid trim warnings).

Not yet built (see "Later phases" above): Helm release browsing, resource
metrics/graphs, RBAC inspection, multi-cluster aggregated views.
