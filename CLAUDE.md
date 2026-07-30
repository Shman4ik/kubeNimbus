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
   deployment) are core, not afterthoughts — **shipped**: `ClusterClient.Metrics.cs`
   queries `metrics.k8s.io` with the version read from **discovery** (never
   hardcoded to `v1beta1`), raised as `MetricsUnavailableException` when the
   group is absent or registered-but-unhealthy, so a cluster without
   metrics-server degrades to no CPU/Mem column rather than an error.
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
6. **The sidebar filters and collapses, it doesn't just scroll.** A cluster's
   resource catalog (built-ins + CRDs) commonly runs past 100 kinds; the
   sidebar's filter box + collapsible sections (CRDs collapsed by default,
   `SidebarSectionViewModel.IsExpanded`) are load-bearing UX, not optional
   polish — any new sidebar content must stay filterable and collapsible.
   The filter matches display name, **API group and short names**
   (`SidebarKindViewModel.Matches`), because the group is the only thing
   telling two same-named CRD kinds apart and "svc"/"po" is how people think.
   A pinned **Recent** section (top, max 5, session-scoped) holds the kinds
   most recently selected.
7. **The inspector docks along the bottom (Lens-style), not in a side sidecar.**
   The resource list fills the content area's width; opening a resource docks a
   detail/logs/exec/YAML tab under it, full-width, so logs and terminals read on
   long lines instead of wrapping in a cramped column. A draggable `GridSplitter`
   resizes the dock and any inspector tab kind can be maximized to fill the
   content area (`ClusterTabViewModel.IsInspectorMaximized`). The three dock
   states (hidden / split / maximized) are driven from `ClusterTabView`'s
   code-behind `ApplyDockState` by mutating the content grid's row heights —
   a `GridSplitter` mutates `RowDefinition.Height` directly and would fight a
   one-way height binding, which is why this is code-behind, not XAML.
8. **Every list/panel state gets an explicit visual** — loading, empty,
   disconnected, conflict, delete-confirm — never a blank rectangle that
   looks like a bug. `ClusterTabViewModel.IsListLoading`/`IsListEmpty` is the
   pattern to extend for new list-backed views.

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

## Metrics (metrics.k8s.io)

`ClusterClient.Metrics.cs` reads the aggregated metrics API for pod (per
container) and node usage. Three things are deliberate:

- **The API version comes from discovery**, not a hardcoded `v1beta1` — same
  rule as everywhere else: nothing about the server's API surface is assumed.
- **Absence is a first-class outcome.** No metrics-server (group missing) and a
  registered-but-dead metrics API (503/404) both raise
  `MetricsUnavailableException`; the UI hides the CPU/Memory columns instead of
  showing an error or a column full of dashes.
- **This is the one thing the app polls** (15s). The metrics API is a
  point-in-time aggregate over a ~30s window with no watch endpoint, so there is
  nothing to stream; polling is scoped to the current list's `CancellationToken`
  so it dies with the watch when the kind/namespace changes.

Quantity strings (`"100m"`, `"128Mi"`, `"12345n"`, `"129e6"`) are parsed by
`Quantity.cs` — a small AOT-safe reader, since `ResourceQuantity` from the k8s
client only covers typed models and metrics/CRD objects arrive as raw JSON.
The CPU/Memory `DataGridColumn`s are shown/hidden from `ClusterTabView`
code-behind: a `DataGridColumn` isn't in the visual tree, so it never inherits
the DataContext and cannot bind its `IsVisible`.

### Usage over time (graphs)

A single usage number can't tell a spike from a steady state, so every polled
sample also lands in a rolling window and gets drawn:

- **`UsageHistory` (Core)** is a fixed-capacity ring of
  `UsageSample(At, CpuNanocores, MemoryBytes)` — 120 samples, i.e. 30 min at the
  15s cadence. It lives in Core because it's engine state with no UI dependency
  (a CLI would want the same window), and it is **deliberately bounded and never
  persisted**: `metrics.k8s.io` has no history endpoint, so anything shown
  over time is only what this session observed. A cluster-wide time series is
  Prometheus's job, not kubeNimbus's — do not grow this into a store.
- **A missing reading is recorded as a gap, not a zero.** `ResourceRowViewModel
  .ClearUsage()` appends an all-null sample, and `Sparkline` breaks the line
  across nulls: a pod that stopped reporting must not read as a pod that went
  idle. `UsageHistoryTests` pins both that and the ring's wrap-around, because
  either bug draws a plausible-looking but wrong chart.
- **`Controls/Sparkline.cs`** is a hand-rolled `Control` (area + polyline via
  `DrawingContext`/`StreamGeometry`, auto-scaled to the series peak with 12%
  headroom). Hand-rolled on purpose: the Avalonia charting packages bring
  reflection-based binding/theming, which NativeAOT is exactly what this repo
  can't accept. No reflection, no templates.
- Series are re-published as fresh arrays per poll — a ring buffer mutated in
  place raises no change notification, and 120 doubles is cheaper than any
  observable-collection plumbing.
- Where it shows: a sparkline beside the number in the list's CPU/Memory cells,
  and pod detail's **Usage** tab (whole-pod CPU and memory charts plus a
  per-container pair). The tab is appended *after* Events so the existing
  `SelectedDetailTabIndex` values (Logs=0, Env=1, Events=2) stay stable.
- The Usage tab distinguishes its three states explicitly (UI rule 8):
  no metrics API on this cluster / samples not collected yet / charts. The
  first two look identical otherwise and lead to very different next steps.

## Helm release browsing (read-only)

`ClusterClient.Helm.cs` reads Helm 3 releases **straight off the cluster** — no
Helm binary, nothing shelled out. Helm stores each revision in a Secret of type
`helm.sh/release.v1`, whose `release` value is base64(gzip(JSON)) with
Kubernetes' own base64 on top: reading one means undoing two base64 layers and a
gzip (`TryReadReleaseRecord`). A record that doesn't unwrap is skipped, never
thrown — one broken release must not take out the list. The encoding is pinned
by `HelmReleaseTests` (no cluster needed), because getting a layer wrong fails
silently as "no releases".

In the App layer the Helm entry is a **synthetic sidebar kind**
(`SidebarGrouping.HelmReleaseDescriptor`, group `helm.sh` — no server serves
that, so it can't collide with a discovered kind). Selecting it stops the watch
and swaps the content area to the release list (`ClusterTabViewModel.IsHelmView`)
rather than starting a watch, since releases aren't an API kind. The section is
added at connect time **only when the cluster actually stores releases** (UI rule
1) — a release installed later in the session appears after a reconnect. Opening
a release docks a tab with its values, rendered manifest, notes and revision
history; double-clicking a history row loads that revision. Everything is
read-only: install/upgrade/rollback stays Helm's job.

## RBAC access review

`ClusterClient.Rbac.cs` answers two different questions two different ways, and
the split matters:

- **"What may I do here?"** goes to the API server's own
  `SelfSubjectRulesReview`. Never re-implement RBAC evaluation locally — a local
  evaluator silently disagrees with the server as soon as webhook authorizers,
  aggregation or impersonation are in play. When the server reports
  `incomplete`, the UI says so; a permissions list quietly missing entries is
  worse than no list.
- **"Where does this subject's access come from?"** has no server endpoint, so
  it's assembled from (Cluster)RoleBindings whose subjects match, each binding's
  role resolved to its rules. That's provenance, not an authorization decision —
  and a binding whose role is gone is still listed, since a dangling binding is
  exactly what you open this view to find.

Entry points are command-palette only (UI rule 1): "Access review — my
permissions" always, plus a subject review when the selected row is a
ServiceAccount (the only RBAC subject that exists as an object — Users and
Groups are just strings inside a binding).

## Multi-cluster aggregated (fleet) views

`ClusterFleet.cs` + `AsyncMerge.cs` (Core) fan one resource query out across every
connected cluster and interleave the results. Four things are load-bearing:

- **Each cluster resolves its own descriptor.** `ClusterFleet.ResolveAsync` looks
  the requested `(group, kind)` up in *that member's* discovery catalog — the
  same CRD kind is routinely served at `v1beta1` on one cluster and `v1` on
  another, and reusing one cluster's descriptor elsewhere would query a path
  that doesn't exist there.
- **A `Reset` is scoped to the cluster that sent it.** Watches relist on 410
  Gone, and `ClusterTabViewModel.ApplyFleet` therefore clears only that
  cluster's rows. Treating a fleet Reset like a single-cluster one would wipe
  four healthy clusters because the fifth reconnected.
- **Partial is normal, and is always stated.** A kind missing from a cluster, or
  a cluster that can't be reached, never fails the view: the header shows
  "n of m clusters serve X" and unreachable members surface in the inline
  warning. `AsyncMerge` reports per-source failures and keeps the rest flowing
  for the same reason.
- **Rows, tab keys and metrics keys are all cluster-qualified.** The same
  namespace/name exists on every cluster in a fleet, so
  `ResourceRowViewModel.KeyFor`, `PodDetailTabViewModel.KeyFor` and
  `YamlEditorTabViewModel.KeyFor` all fold the cluster name in — otherwise the
  second cluster's pod silently reuses the first one's row and inspector tab.
  Opening a row uses **its own** cluster's client and descriptor
  (`ClusterTabViewModel.ClientFor`/`DescriptorFor`), or a YAML apply would land
  on the wrong cluster; owner-chain navigation stays pinned to the same cluster.

Why a channel-based merge (`AsyncMerge`): the sources are long-lived watch
streams that each block indefinitely, so a sequential `await foreach` over them
would starve every cluster but the first. `AsyncMergeTests` pins exactly that,
plus per-source failure isolation and teardown-on-abandon.

UI-wise this is a **toggle on the existing list**, not a new view: the sidebar,
namespace picker, filter and inspector are all unchanged, the list gains a
Cluster column (shown/hidden from code-behind, same DataGridColumn reason as the
usage columns), and the toggle only appears with more than one cluster connected
— a fleet of one is the tab you are already looking at (UI rule 1). The command
palette carries the same toggle. `MainWindowViewModel` owns the member list and
makes cluster names unique (two tabs on one context would otherwise merge into
one apparent cluster) and re-fans active aggregated watches when a tab opens or
closes, so no tab keeps watching a disposed client.

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

# Headless visual check (no display, e.g. Claude Code Cloud) — see below.
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots

# NativeAOT publish — THE shipping build. Verify it end-to-end on every change
# that could affect trimming/AOT (new package, new reflection, new binding).
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app
```

On a machine without the Windows/MSVC toolchain (e.g. this repo's Linux dev
containers, Claude Code Cloud), `dotnet publish src/KubeNimbus.App -c Release
-r linux-x64 -p:PublishAot=true -o publish/app` exercises the same
IL-trimming/AOT analysis and catches the same class of problems (new
reflection, a non-trim-safe binding) even though it isn't the shipping
binary — run it after any change that could plausibly affect trimming, and
call out in the PR that the authoritative win-x64 publish still needs a
local Windows pass.

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

### Headless screenshot harness (`tools/Screenshot`)

For environments with no display and no DevTools MCP (Claude Code Cloud
sessions, CI) — renders real Views bound to fixture ViewModels via
`Avalonia.Headless` (Skia software rendering, `UseHeadlessDrawing = false`)
and dumps PNGs. Not part of the shipping app; excluded from the App's
NativeAOT publish.

```bash
dotnet run --project tools/Screenshot -- <outputDir> [scenario-name-substring]
```

Writes one `<scenario>.<light|dark>.png` per scenario × theme to `outputDir`
(pass a scratch dir — nothing under it is committed). Omit the filter to
render every scenario in `Program.cs`'s `scenarios` array.

Key structural point: a `ClusterTabView` (or any inspector tab view) screenshot
must be hosted inside a real `MainWindow`, not a bare wrapper — `ContentControl`'s
implicit `DataTemplate` lookup only resolves `PodDetailView`/`YamlEditorView`/etc
by walking the visual tree to `MainWindow.axaml`'s `Window.DataTemplates`; a
bare `Border`/`Window` wrapper falls back to a `ToString()`-in-a-TextBlock
placeholder instead of the real view. See `HostInMainWindow` in `Program.cs`.

Fixture data (`tools/Screenshot/Fixtures/*.json` — pods, deployments, events,
a 72-kind CRD catalog spanning cert-manager/argoproj/istio/velero/keda/flux/etc
to stress-test sidebar scaling realistically) is loaded by `FixtureData.cs`
into real `DynamicResource`/`ResourceDescriptor` instances. `ClusterTabScenarios.cs`
builds fully-populated `ClusterTabViewModel`s by setting the same public
properties `ConnectAsync`/`RestartWatch`/`Apply` would, using an **offline
`ClusterClient`** (`FixtureData.CreateOfflineClient()`, pointed at
`Fixtures/kubeconfig-fake.yaml` → `https://127.0.0.1:1`, an address nothing
listens on) so ViewModel constructors that require a live `ClusterClient`
(pod detail's event refresh, exec's connect) still work — those calls just
fail fast in the background and are swallowed by the same error handling a
real lost connection already has.

Gotcha already hit once: setting `SelectedNamespace` on a fixture
`ClusterTabViewModel` fires the real `OnSelectedNamespaceChanged` → `RestartWatch()`
hook. With no `Client` wired up that only touches `IsListLoading`/`IsListEmpty`,
but if you set `SelectedNamespace` *before* manually populating `Rows`, the
empty-state flag latches `true` and never gets recomputed (production code
never hits this ordering — there, `RestartWatch`'s background pump is what
populates `Rows`). `ClusterTabScenarios.BaseTab()` recomputes `IsListEmpty`
after populating rows for exactly this reason; follow the same pattern for
new scenarios that set view-model properties directly.

When Docker is available (unlike this session — `docker version` succeeds but
`dockerd` isn't running here), prefer driving the harness against a real
k3s sandbox (see below) instead of fixtures for a final verification pass;
note in the PR which screenshots were fixture-only.

## MVP scope (phase 1 — shipped, see Current status below)

- [x] Context picker from kubeconfig (exec-plugin auth working).
- [x] Live-updating pod list (watch) — proven end-to-end in the app.
- [x] Sidebar tree (Workloads/Network/Config/Storage/CRDs via discovery),
      namespace-scoped, live list views.
- [x] Pod detail: containers, status, live log streaming (follow, container
      picker, cancel, previous-container, search/filter, ERROR/WARN/INFO
      coloring, timestamps/wrap toggles, copy/download), environment variables
      (literal + Secret/ConfigMap refs with on-demand reveal), live CPU/Mem
      usage (metrics.k8s.io, when present), events.
- [x] YAML view/edit for any resource → server-side apply; delete with confirm.
- [x] Exec into a pod container (interactive terminal) and port-forward.
- [x] Command palette (Ctrl/Cmd+K); light/dark theme.
- [x] Multi-cluster context tabs (drag-reorder, workspace-restore).
- [x] Owner-reference navigation (pod → replicaset → deployment, etc.).
- [x] pgNimbus visual design system ported (Theme.axaml, two-tone shell,
      brand-blue accent, MDI icon vectors).

**Later phases:** all shipped. Resource metrics, session-window usage graphs,
read-only Helm release browsing, RBAC access review and multi-cluster aggregated
views each have a section above. Still open: "who can do X across the cluster"
(the cluster-wide direction of the RBAC review). Long-range metrics history is a
**non-goal**, see "Usage over time" above.

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

**UX polish pass (post-MVP, layout redesigned from scratch — see UI design
rules 6-8 above):** PR #2's shell mechanically ported pgNimbus's SQL-client
layout; this pass kept the visual language (color/type/iconography/materials)
but reworked the structure for a resource browser rather than a query tool:
- Sidebar gained a live filter box and collapsible sections (CRDs collapsed
  by default) — verified against a 72-kind synthetic CRD catalog
  (`tools/Screenshot/Fixtures/crd-catalog.json`) spanning cert-manager,
  argoproj, istio, velero, keda, flux, and others, since a handful of
  built-in kinds doesn't expose how the sidebar behaves on a real cluster.
- Resource list: Name/Namespace/Status trim with an ellipsis + tooltip
  instead of hard-clipping; Status renders as a color-coded pill; a pod with
  0 ready containers (CrashLoopBackOff) now reads as error, not the same
  warn as a merely-Pending pod; explicit loading/empty states
  (`IsListLoading`/`IsListEmpty`) and an inline disconnected-watch banner
  replace what used to be an undifferentiated blank rectangle.
- Inspector panel was reworked from a cramped right-side sidecar into a
  Lens-style **bottom dock**: the resource list spans the full content width and
  detail/logs/exec/YAML tabs dock beneath it, so logs and the exec terminal read
  on full-width lines instead of a narrow column. A draggable `GridSplitter`
  resizes the dock (floored so it can't collapse to a sliver) and the maximize
  toggle still fills the whole content area. Row heights for the hidden/split/
  maximized states live in `ClusterTabView.ApplyDockState` (code-behind, since a
  `GridSplitter` fights a one-way height binding). Dock tab headers show an
  active-tab highlight (`InspectorTabViewModelBase.IsActive`); Fluent's oversized
  24px `TabItem` headers were pulled down to body scale in `Theme.axaml`.
- YAML editor gained syntax highlighting (hand-written `.xshd`, AvaloniaEdit
  ships none for YAML) — see `Editing/YamlSyntaxHighlighting.cs`.
- A keyboard-shortcuts cheat sheet (F1 / the command bar's `?` button)
  surfaces Space/Enter/double-click/drag-tab, none of which had any
  discoverability before.
- Pod logs now actually auto-scroll while "Following" (that toggle only
  controlled the stream before, not the ScrollViewer); the exec terminal
  strips ANSI escape codes per chunk and caps scrollback at 200k chars
  (mirrors the existing 4000-line cap on pod logs).
- New: `tools/Screenshot`, a headless Avalonia visual-verification harness
  for environments with no display (see "Headless screenshot harness"
  above) — this pass's screenshots were fixture-driven (no Docker daemon in
  this session's environment); a live-cluster pass locally is still worth
  doing before/soon after merge to catch anything fixture data wouldn't
  surface (real CRD status shapes, real watch reconnect behavior under the
  new empty/loading states, actual terminal ANSI output from a real shell).

**Logs/events/telemetry/env-secrets pass:** closed the gaps
called out at the end of the UX polish pass — logs, events, and telemetry
were half-built or missing entirely; this pass filled them in and added
Kubernetes' other classic on-call surface (env vars/secrets):
- **Logs** (`PodDetailTabViewModel`, `LogLineViewModel`): in-buffer search/filter
  (matches against the message, not the raw line, so filtering doesn't fight
  the timestamp toggle), ERROR/WARN/INFO color coding via a lightweight text
  heuristic, a timestamps toggle (`StreamPodLogsAsync` now always requests
  `timestamps=true`; the toggle is a pure display concern — no re-stream
  needed), a wrap toggle, copy/download (Avalonia clipboard/`IStorageProvider`,
  reached via the desktop `IClassicDesktopStyleApplicationLifetime`), and a
  previous-container toggle (`StreamPodLogsAsync(..., previous: true,
  follow: false)`, a one-shot fetch, not a follow).
- **Events**: `ResourceStatusSummary` special-cases core/v1 Event so the
  generic list shows Reason/Count with Warning/Normal-driven pill color
  instead of a meaningless Status column; `SidebarGrouping.IconKeyFor` gives
  Event its own bell icon within the Config section (no new top-level
  section — the sidebar stays the five fixed sections) rather than an
  unlabeled group of the same Config icon everything else uses; double-click
  on an Event row now navigates to its `involvedObject` (via the same
  `OwnerRef`-typed resolve-and-open path owner-chip navigation already used)
  instead of opening the event's own not-very-useful YAML; pod-detail's
  Events tab gained the same Type color coding and an "open involved object"
  chevron per row.
- **Telemetry** (`ClusterClient.Metrics.cs`, new): queries `metrics.k8s.io`
  PodMetrics/NodeMetrics through the same generic `ResourceDescriptor`/
  `ListResourceOnceAsync`/`ReadResourceAsync` path every other resource kind
  uses — no bespoke parsing code. `IsMetricsApiAvailableAsync` checks the
  discovery catalog (already fetched for the sidebar) for the `metrics.k8s.io`
  group, so a cluster without metrics-server shows no CPU/Mem column/readout
  instead of erroring. The metrics API doesn't support watch, so
  `ClusterTabViewModel` and `PodDetailTabViewModel` each run their own
  20-second `DispatcherTimer` poll rather than a new watch path — CPU/Mem
  shows in the pod list (a column, metrics-gated) and pod detail (per-container
  readout next to Ready/RestartCount).
- **Env vars & Secrets**: pod detail gained an Environment tab
  (`spec.containers[].env`/`envFrom`) — literal values show inline;
  `secretKeyRef`/`configMapKeyRef` show only the reference (`Secret/name ·
  key=x`) until an explicit per-row "Reveal" fetches and decodes on demand
  (cached per Secret/ConfigMap name within the tab so revealing several keys
  from the same object doesn't refetch; RBAC/network failures surface inline,
  never crash the tab); `envFrom` sources are reference-only (no per-key
  reveal — the pod spec doesn't declare individual keys for those). The YAML
  editor gained a Secret-only "Reveal values" toggle: `data` stays base64 in
  the editable text (matching kubectl), the toggle only adds a separate
  read-only decoded-values panel computed from whatever the editor currently
  holds via the existing `YamlJson` YAML→JSON conversion — masked by default,
  nothing decoded until asked.
- **Pod-detail layout redesign** (mid-session correction, screenshot-driven):
  the first pass kept `PodDetailView`'s original fixed-width left CONTAINERS
  column and a DataGrid for Events, and at the panel's default (non-maximized)
  width that was unusable — Type/Reason/Message/Count/LastSeen had no room in
  a DataGrid, and Logs/Env/Events tab headers wrapped onto separate lines. Fix:
  the container picker moved from a fixed side column into a horizontal
  `WrapPanel`-backed `ListBox` strip above the tabs (chips: status dot, name,
  restart count, usage — Exec/port-forward buttons alongside it), which alone
  frees most of the panel's width for the tabs; Events became a card feed
  (`ItemsControl` of Border "card"s: color pill + reason, wrapped message,
  count/timestamp, "open involved object" chevron) instead of a DataGrid,
  since five columns were never going to fit an inspector-width panel and a
  scannable feed reads better for events anyway; the Environment tab's env-var
  rows are a vertical stack (name, then value/reference+Reveal button, then
  revealed value) rather than a fixed-column grid, for the same reason.
- Fixture-only this session (see below): `tools/Screenshot/Fixtures/pod-metrics.json`
  (obviously-fake usage numbers), `secret.json` (obviously-fake base64,
  flagged in the file itself), and `events.json` gained `involvedObject` on
  every entry. `pods.json`'s report-generator container gained a realistic
  `env`/`envFrom` block to exercise the new tab.
- **Not live-verified this session**: this environment's Docker daemon could
  be started (unlike the prior session), but pulling `rancher/k3s` from
  Docker Hub was blocked by this session's egress policy (confirmed via the
  agent-proxy status endpoint — a `production.cloudfront.docker.com` CONNECT
  was denied), so the sandbox recipe below still couldn't run here. Everything
  in this pass was verified via `tools/Screenshot` (both themes) plus the
  linux-x64 NativeAOT publish check; the metrics-API-*absent* degradation path
  (`IsMetricsApiAvailableAsync` returning false, hiding the CPU/Mem UI
  entirely) is exercised by construction (fixtures never set
  `IsMetricsAvailable`/never populate metrics on the default scenarios) but
  not against a real cluster either with or without metrics-server installed.
  A real-cluster pass — ideally once with metrics-server, once without — is
  still worth doing before/soon after merge.

**Usage-graphs pass:** closed the "usage graphs over time" item — see
"Usage over time (graphs)" above for the design rules. New in this pass:
`UsageHistory` (Core, bounded session-only ring + `UsageHistoryTests`),
`Controls/Sparkline.cs` (hand-rolled AOT-safe area/line chart), a sparkline
beside the number in the list's CPU/Memory cells, and pod detail's **Usage**
tab (pod-total CPU/memory charts + per-container pair, with explicit
no-metrics-server and still-collecting states). The screenshot fixtures now
replay 24 stamped poll ticks through the *real* `ApplyUsage`/`ApplyMetrics`
entry points (`ClusterTabScenarios.SeedUsage`/`SeedPodUsage`) rather than
setting chart state directly, so what renders offline is what a real poll
produces; `ApplyUsage`/`ApplyMetrics` take an optional sample timestamp for
exactly that reason (production passes none and uses now).
**Not verified this session at all:** the container had no .NET SDK and this
session's egress policy blocks every .NET install host
(`builds.dotnet.microsoft.com`, `aka.ms`, `download.visualstudio.microsoft.com`
all answer 403 through the agent proxy; only nuget.org and github.com are
reachable), so `dotnet build`, the TUnit suite, `tools/Screenshot` and the
linux-x64 NativeAOT check could none of them run. Everything in this pass is
code-reviewed only — a build + test + screenshot pass is the first thing to do
on a machine with an SDK.

**Fleet pass:** closed the last "later phase" item — multi-cluster aggregated
views, see "Multi-cluster aggregated (fleet) views" above for the rules. New:
`ClusterFleet.cs` and `AsyncMerge.cs` in Core (+ `AsyncMergeTests`), an
"All clusters" toggle and Cluster column on the existing list, cluster-qualified
row/tab/metrics keys, per-row client+descriptor resolution so an apply can't land
on the wrong cluster, and `MainWindowViewModel` ownership of the member list
(unique cluster names, re-fan on tab open/close). Screenshot scenarios
`cluster-tab-fleet-list` and `-partial` populate rows directly, since a real
aggregated watch needs several live clusters. Same verification gap as the
usage-graphs pass: no SDK in that session either, so CI (build + TUnit +
linux-x64 AOT publish) is the only thing that has looked at it.

The UX pass and the logs/events/telemetry/
env-secrets pass are both not exhaustive — there's no finish line here, just
diminishing returns; candidates for a follow-up iteration: coalescing
transition/hover animation polish, a proper win-x64 NativeAOT pass (still
only linux-x64 has ever been verified), a live k3s pass, and node-level
CPU/Mem (only pod-level shipped by the logs/events/telemetry pass;
node-level was added separately by the Helm/RBAC/metrics pass above — see
"Live CPU/memory from metrics.k8s.io").

**Sidebar navigation pass** (small, alongside the fleet pass): the two
sidebar follow-ups are closed, though not the way they were originally
phrased. *Coalescing* same-named CRD kinds into one row was rejected —
nesting rows inside a section that is already 100+ kinds deep costs more
than it buys, and the group label added earlier already tells `Backup`
(velero.io) from `Backup` (postgresql.cnpg.io). What was actually missing
is that you could not **filter** by the thing the row displays:
`SidebarKindViewModel.Matches` now matches the API group and the server's
short names as well as the display name, so "velero" or "svc" find what
you would expect. And a pinned **Recent** section (top of the sidebar, max
5, `ClusterTabViewModel.RecordRecentKind`) holds second instances of the
kinds most recently selected — session-scoped and reset on reconnect,
since the entries hold descriptor instances from the catalog being
replaced. Persisting it across restarts would need a `WorkspaceSettings`
schema change and is deliberately not done yet.
