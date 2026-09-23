# kubeNimbus status history

> Moved out of `CLAUDE.md`: the MVP checklist and the per-pass log of what each pass shipped, verified and left unverified. Record new passes at the end.

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

**Later phases:** all shipped, including the cluster-wide "who can do X" direction
of the RBAC review. Resource metrics, session-window usage graphs, read-only Helm
release browsing, RBAC access review and multi-cluster aggregated views each have
a section above. Long-range metrics history is a **non-goal**, see "Usage over
time" above.

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

**"Who can do X" pass:** the last open roadmap item is closed — the cluster-wide
direction of the RBAC review, see "RBAC access review" above for the rules. New:
`ClusterClient.WhoCan.cs` in Core (`WhoCanAsync` rule scan, `CheckAccessAsync`
SubjectAccessReview, `AccessQuery`/`WhoCanResult`/`SubjectAccess`/`AccessDecision`)
plus `WhoCanMatchingTests` pinning the API server's matching semantics and three
sandbox-gated integration tests; a "Who can…" section in the existing access-review
pane (verb picker, kubectl-style resource box resolved through the discovery
catalog, optional object name, all-namespaces toggle, per-subject Verify) and a
palette entry that opens straight onto it; `WhoCanRowViewModel` in the App layer;
`cluster-tab-rbac-who-can{,-empty}` screenshot scenarios; and two sandbox RBAC
shapes nothing else produced (a `resourceNames`-narrowed rule, a ClusterRole bound
by a RoleBinding). Also fixed in passing: a failed `SelfSubjectRulesReview` used to
blank the whole access-review pane, and now renders inside "My permissions" only.
**Verified this session** (build, 80/80 TUnit, both-theme screenshots, linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid ones). Getting an
SDK took a detour worth writing down, since the last two passes gave up at this
point: **`builds.dotnet.microsoft.com`, `aka.ms`, `dot.net` and the Launchpad PPAs
are all 403 through the agent proxy, but `archive.ubuntu.com` is not** — and Ubuntu
24.04's own `noble-updates/main` carries `dotnet-sdk-10.0` (plus
`dotnet-sdk-aot-10.0`, which is what makes the AOT publish work). So:

```bash
apt-get update && apt-get install -y dotnet-sdk-10.0 dotnet-sdk-aot-10.0
```

Blocked PPAs in `/etc/apt/sources.list.d/` (deadsnakes, ondrej, docker) fail the
`apt-get update` — move them aside first. Do **not** reach for the dotnet-install
script in this environment; it only ever hits the blocked hosts.

Still unverified: the live-cluster half. Docker's daemon starts here, but pulling
`rancher/k3s` still dies on a policy denial for Docker Hub's blob CDN
(`production.cloudfront.docker.com`, 403 on the layer fetch after the manifest
succeeds), so the sandbox can't come up and the RBAC integration tests — including
the three new who-can ones — skipped rather than ran. A real-cluster pass remains
the outstanding item.

**Public-release prep pass (v0.1.0):** the repository is now shaped for a public
audience and a tagged release. See "Releasing" above for the design rules. New:

- **Release plumbing.** `Directory.Build.props` carries the single
  `<VersionPrefix>` plus product/author/copyright/repo metadata;
  `.github/workflows/release.yml` publishes NativeAOT for win-x64, linux-x64,
  linux-arm64 and osx-arm64 on a `v*.*.*` tag, archives each with LICENSE/
  README/CHANGELOG, emits `SHA256SUMS.txt`, and creates the release with the
  matching `CHANGELOG.md` section as its body. `CHANGELOG.md` itself is new
  (Keep a Changelog, 0.1.0 covering everything shipped to date).
- **The shipped executable is now `kubeNimbus`**, not `KubeNimbus.App` — see
  the three coupled places under "Releasing" above.
- **Community health files**: `CONTRIBUTING.md`, `SECURITY.md` (which states
  the security model, not just a reporting address), `CODE_OF_CONDUCT.md`,
  issue templates that ask for cluster distribution and sandbox-reproducibility
  because those are what make a Kubernetes-client bug tractable, a PR template
  whose checklist is this file's rules, and `dependabot.yml` with Avalonia
  grouped so a bump arrives as one buildable PR rather than six.
- **README rewritten for someone deciding whether to download it**: badges,
  a screenshot gallery from `design/screenshots/` (generated — see
  `design/screenshots/README.md`), per-platform install including the unsigned-
  binary workarounds, and an explicit known-limitations section.
- **Two real bugs fixed in passing**, both of which had been quietly wrong:
  - **CI never ran the tests.** `dotnet test <csproj>` positionally is a no-op
    under the .NET 10 MTP runner — it prints a hint and exits 0. Every "green"
    CI run since the workflow landed tested nothing. Now `--project`, and
    called out in the Verification workflow section above so it can't recur.
  - The screenshot harness rendered every scenario with "No kubeconfig
    contexts" in the command bar, which reads as a failed connection in a
    README image; `SeedContexts` fixes it, and `cluster-tab-pod-detail` renders
    at 1000px so the log pane isn't clipped by the window edge.
- **CI also renders the screenshot harness now** as a XAML smoke test, and
  uploads the PNGs as an artifact — the assembly rename above is exactly the
  class of change that compiles cleanly and dies at startup.
- **Verified this session**: build, **80/80 TUnit** (with `--project`; they
  skip the cluster-gated ones — no sandbox here), all 58 screenshots in both
  themes, and the linux-x64 NativeAOT publish. Audited tree *and* git history
  for credentials — clean; every hit is obviously-synthetic fixture or sandbox
  data.
- **Still unverified**, and the first things to do on a real machine: the
  win-x64 NativeAOT publish (only linux-x64 has ever run), the macOS and Linux
  release binaries actually launching, and the live-cluster half — the sandbox
  still can't come up here (Docker Hub blob CDN blocked by egress policy).

**Core-scenario + Advanced-view pass:** the first pass driven by hand-testing
against a live cluster rather than by fixtures, and it found that the app's
central on-call scenario — open a pod, read logs, exec in, port-forward, look
at env/secrets — was partly broken end to end. See the Advanced view section
and UI rule 8b above for the two rules it added. What was wrong, and is not
any more:

- **Pod logs never streamed.** Two independent causes: the `ToggleButton`
  `IsChecked`+`Command` double-toggle (UI rule 8b) made Follow a guaranteed
  no-op and made `LoadPreviousLogs` unreachable, and `StartLogs()` was never
  called on open — so double-clicking a pod landed on a blank card with no
  message at all. Logs now start on open, the stream follows the container
  picker (it used to keep streaming the old container under the new one's
  name), and the pane has explicit states for streaming-but-silent, stopped,
  ended-with-a-reason and filter-matched-nothing-of-*n*-buffered.
- **`LogSeverityToBrushConverter` returned `null` for the default case**, which
  writes a *local* null `Foreground` that beats inheritance — and Avalonia's
  glyph-run draw early-returns on a null brush, so every line without a
  severity keyword rendered **invisible**. That is most lines: nginx access
  logs, Go `log.Print`, anything JSON. It returned `AvaloniaProperty.UnsetValue`
  after this pass. It was never caught because every fixture log line contains a
  keyword. **`UnsetValue` turned out to be the same bug one size smaller, and the
  converter is gone** — see "Log severity is three classes, not a brush binding"
  below for what actually fixed it and how it was measured.
- The severity heuristic was substring, not token, so `GET /api/v1/errors`
  coloured red; it matches whole words now.
- **Throughput**: the pump awaited one dispatcher round-trip *per line* and did
  an O(n) `ObservableCollection.Remove` per line past the 4000 cap. Lines are
  now batched on a 100 ms tick and trimmed with one `RemoveRange`. Auto-scroll
  is posted (it used to run inside `CollectionChanged`, one line behind) and
  has a scroll lock.
- Logs are horizontally scrollable and selectable; Copy/Download write the raw
  lines *with* timestamps (they wrote `DisplayText`, so a log saved with the
  timestamp toggle off had none).
- **Init and ephemeral containers were invisible entirely** — a failing init
  container could not be inspected at all. All three lists are read now, the
  chip carries the role and the live state (`CrashLoopBackOff`), and the
  default selection is the first *app* container, as `kubectl logs` does.
- `RefreshEnvironment()` ran on every watch tick and cleared `EnvironmentVars`,
  so a **revealed secret value vanished seconds later**; it is now keyed on a
  signature of the container's own env block. `fieldRef` resolves against the
  pod object we already hold, `optional: true` refs read dim rather than as
  errors, and each `envFrom` line opens the object it names.
- **Exec**: `/bin/sh` was hardcoded, so a bash-only or distroless image gave a
  connected-looking blank terminal. It now tries bash → sh → ash, decided by
  the API server's **error channel** (channel 3 — the only place a missing
  shell is reported; neither stdout nor stderr carries it). Ctrl+C / Ctrl+D /
  Tab reach the remote shell, the input box takes focus on open, and
  `ResizeAsync` finally has a caller so the PTY isn't stuck at 80×24.
  **Gotcha worth keeping**: `StreamDemuxer`'s per-channel streams do **not**
  observe a `CancellationToken`, so the shell probe times out via
  `Task.WhenAny`, not `CancelAfter` — the first live run hung on "Connecting…"
  forever because of exactly that.
- **Port-forward** offers the pod's declared ports with their names (they were
  collected and then discarded for a hardcoded 8080), copies/opens the local
  URL, locks its inputs while running, titles the tab with the port, and shows
  the kubelet's own refusal text. A forward whose last connection failed reads
  warn, not ok — the listener really is still accepting, so "stopped" would be
  a lie.
- **The list gained kubectl's columns** — Ready / Restarts (with "(43m ago)") /
  Age / a kind-specific Details — gated per kind by `ResourceStatusSummary`
  from `ClusterTabView.ApplySummaryColumns`. Age ticks off one shared timer per
  list, since no watch event makes wall-clock change.
- **Right-clicking a resource did nothing**; there is now a row `ContextFlyout`
  (Logs / Previous logs / Exec / Port-forward / Edit YAML / Delete) with the
  same six actions mirrored as palette entries, and a `PointerPressed` handler
  so the menu acts on the row it opened over rather than on the previous
  selection — which matters when the last item is Delete.
- Sidebar kinds are labelled from the server's own plural, so `Endpoints` stops
  rendering as "Endpointses" (and no CRD Kind that is already plural will).

**Verified this session, against the live k3s sandbox**: build (0 warnings),
**137/137 TUnit with 0 skipped** (so the cluster-gated tests really ran), both
byte-level repro scripts (`pftest.cs` → `HTTP/1.1 200 OK` with no junk prefix;
`yamltest.cs` → all string scalars survive), all 32 screenshot scenarios in both
themes, and a DevTools-driven pass over the running app: the Status/Ready/
Restarts/Age columns match `kubectl get pods -A` row for row across every
`demo-*` pod (`bad-image` → ImagePullBackOff, `crashloop` → CrashLoopBackOff
151 (2m ago), a finished Job pod → Completed and *not* coloured as an error),
logs stream on open with no click, switching container switches the stream,
Previous works on `demo-broken/crashloop`, a filter matching nothing says so
with the buffered count, and exec connects after correctly skipping `/bin/bash`.

**Not verified this session**, in rough priority order: the port-forward pane's
new UI end to end (Core is proven by `pftest.cs`, the pane is not), the env/
Secret reveal path against a real Secret, the row context menu and the new
palette entries by actual mouse/keyboard (they were verified by construction,
not driven), Advanced-view off/on in the *running* app rather than in the
harness, and the win-x64 NativeAOT publish — still the one build that has never
run anywhere. **Closed 2026-08-04** except win-x64 NativeAOT — see "Live-cluster
validation pass" below.

**Inspector density pass:** driven by a screenshot of the running app whose
complaint was, in three parts, "tabs too big, too much nesting, too little room
for content" — and measuring the dock proved it: pod detail spent ~200px of a
~300px dock on chrome. See UI rule 10 above for the rule this pass added; the
mechanics are `ListBox.segmented` + `TabControl.headerless` +
`Rectangle.toolSeparator` in `Theme.axaml` and the new
`Converters/IndexEqualsConverter.cs`. What changed:

- **Pod detail: four chrome rows → two.** Owner chips moved onto the container
  row (the "Owned by" label went — the chips say `ReplicaSet/x` themselves, and
  they gained a border so they read as clickable, UI rule 8); the container
  picker became a horizontally-scrolling strip rather than a `WrapPanel`, so
  eight containers cost the same height as one; the log filter box, the
  Follow/Previous/timestamp toggles, Events' refresh and Usage's window caption
  all moved onto the tab strip's row; Env's `ENV — <container>` header went
  (the strip two rows up *is* its selector).
- **Env and Events got denser inside their tabs too.** Env is name-beside-value
  on a fixed 200px name column — Auto per row would start every value at a
  different x, since Avalonia has no shared-size scope — and Reveal sits next
  to its reference instead of flung to the right edge. An event card is two
  lines, not three (count/timestamp ride the reason). Both roughly double what
  fits.
- **Helm and RBAC lost their title rows** and gained the same one-row strip.
  Their titles duplicated the dock tab (`Helm/checkout`, `Access/payments`)
  exactly. `HelmReleaseView` binds its TabControl to the strip's `SelectedIndex`
  by element reference (`#HelmTabStrip`), since that view model has no tab-index
  property; `RbacView` keeps binding both to `SelectedTabIndex`, which
  `WhoCanTabIndex` deep-links to.
- **`cluster-tab-helm-release-detail` is a new screenshot scenario**, because
  `HelmReleaseView` was the one inspector view the harness never rendered — and
  the harness is CI's only check that a view's XAML still loads. Its fixture
  drains the offline load before writing its text: the failed load's
  continuation lands on the same `RunJobs()` the capture pumps, so anything set
  before it is overwritten by "Connection refused" (which is exactly what the
  first run of that scenario rendered).

**Verified this session**: build (0 new warnings), **137/137 TUnit, 0 skipped**,
all 66 screenshots (33 scenarios × both themes), and the linux-x64 NativeAOT
publish with no new warnings beyond the known DataGrid IL2104/IL3053. The seven
generated README screenshots under `design/screenshots/` were regenerated.
**Not verified**: no live cluster here (Docker Hub's blob CDN is still blocked
by this session's egress policy), so this pass — like the layout it replaces —
has only been seen in the harness. The row heights it frees up are worth a look
in the running app, particularly a pod with many containers (the strip now
scrolls rather than wrapping) and a filter box narrowed by a small window.
And win-x64 NativeAOT remains the build that has never run anywhere.
**Closed 2026-08-04** for the running-app half — see "Live-cluster validation
pass" below; win-x64 NativeAOT is still outstanding.

**Live-cluster validation pass (2026-08-04):** a hand-driven pass against the
sandbox to close the "not verified" gaps the two passes above both flagged —
the port-forward pane, the Secret reveal path, the row context menu, and the
Advanced-view toggle had each only ever been exercised by construction or in
the headless harness, never actually clicked. Docker's daemon and the
`rancher/k3s` pull both worked this time (the egress block on prior sessions
was environment-specific, not a standing constraint), so `docker start
kubenimbus-sandbox` plus a ~45s wait for kubelet to reconcile after the
container restart was enough — no `-Recreate` needed. Method: the Avalonia
DevTools MCP attached to the running Debug build for node-targeted clicks
(`input` with `Click`/`Text`/`KeyDown` — reliable for buttons/tabs/ListBoxItems,
but the DataGrid row's double-click-to-open gesture doesn't answer to a
synthetic `Click` twice in a row, since ClickCount tracking lives in Avalonia's
input manager, not the diagnostics bridge), with computer-use driving real
mouse/keyboard for the gestures DevTools can't send (double-click, right-click)
and for resizing the window. The window was resized from the physical
3840×1600 panel down to **1920×1080** via a direct Win32 `SetWindowPos` (found
by `EnumWindows` — the title is `"kubeNimbus"` but window-name lookup by exact
string missed it, so enumerate-and-grep was the reliable path) — reviewing UI
at native 4K-wide would have hidden the layout problems a majority of users
would actually see at Full HD. None turned up at 1920×1080: sidebar, dock and
tab strip all stayed within bounds with no clipping or wrapping.

Confirmed working, each against real cluster state (not fixtures):

- **Logs start on open, no click.** Double-clicking `demo-broken/crashloop`
  populated the Logs tab immediately with its real ERROR line.
- **Container switch switches the stream.** Selecting `access-log-tailer` on
  `demo-shop/shop-web-*` replaced the `web` container's nginx access log with
  the tailer's own heartbeat/WARN/ERROR lines — not the old container's log
  under the new label.
- **Non-keyword log lines are visible**, not invisible-by-null-brush: the
  `web` container's plain nginx `[notice]`/access lines rendered in normal
  text color alongside the tailer's colour-coded severity lines.
- **Env tab, live.** `shop-web`'s `web` container showed a literal
  (`SERVICE_NAME`), a ConfigMap ref, a Secret ref, and a `fieldRef:
  status.podIP` resolved to the pod's real IP; clicking **Reveal** on the
  Secret ref decoded a real value (`sandbox-token-0000`) from the live
  `shop-credentials` Secret.
- **Exec, end to end.** Connecting to `shop-web`'s `web` container (an nginx
  image, no bash) reported "Connected to web (/bin/sh)" — the bash→sh→ash
  probe correctly skipped bash — the input box had focus without an extra
  click, and `echo hello_from_kubenimbus` round-tripped through the real
  WebSocket exec channel.
- **Port-forward, end to end** — the one surface Core-only (`pftest.cs`) had
  ever exercised. Declared port 80 offered by name/number from the pod spec,
  Start produced `Forwarding 127.0.0.1:50337 → shop-web-...:80`, and `curl
  http://127.0.0.1:50337/` returned a real `HTTP 200` from the pod's nginx.
  Stop cleanly reverted to a "Stopped." state with inputs re-enabled.
- **Row context menu** opens on the row under the cursor (not the previous
  selection) with all six actions; **Edit YAML** from it opened a real pod
  manifest with live `managedFields`, syntax highlighting, and working
  Apply/Delete.
- **Advanced view toggle**, live: CPU/Memory columns and per-container
  sparklines appeared with real polled values from metrics-server (e.g.
  `cache-0` at `1m`/`5.0 MiB`), and sidebar kind-count badges appeared next to
  every section.
- **Helm release detail**, live: `Helm/checkout` opened straight to the
  Values/Manifest/Notes/History strip with no repeated title row (the
  density-pass fix), decoded real chart values, and History showed the
  sandbox's synthetic 3-revision release (`checkout-0.2.1`/`-0.2.0`/`-0.1.0`,
  rev 3 `deployed`, 1–2 `superseded`) with real timestamps.

Build: 0 warnings. **TUnit: 137/137 passed, 0 skipped**, run via the test
`.exe` directly (`dotnet test --project` still reports "Zero tests ran" on
this SDK — see below) — 0 skipped confirms the cluster-gated tests really ran
against a live server, not just the unit-only subset.

**Not covered this pass**: RBAC access review / Who-can (unchanged since the
last pass that verified it), init/ephemeral container visibility, and the
win-x64 NativeAOT publish, which has still never run on any machine this
project has touched.

**Fluent form/state pass:** two surfaces reworked against [Fluent basics][fluent-basics],
adding UI rule 11 and the ConfigMap/Secret section above.

- **Port forward.** Was a six-row stack under a `PORT FORWARD` title the dock tab
  already carried, with beside-the-input labels ("Local port" touching its own
  box), a Start/Stop pair one half of which is always dead, a `0` that silently
  meant "pick one for me", and a bare status dot. Now: no title row, one field
  row reading local → pod with labels above (`TextBlock.fieldLabel`), the
  declared-port picker beside the pod-port box rather than on a row of its own,
  one button that swaps on `IsRunning`, an empty box under an `auto` placeholder
  (`LocalPortInput`, where null *is* the wire value 0), and a `Border.infoBar`
  carrying the local URL itself — selectable, copyable, openable — since reaching
  the thing you forwarded is the whole point of forwarding it. The running
  `StatusMessage` sentence is gone (the bar and the tab header said all of it);
  `StatusIsError` distinguishes "Stopped." from "Local port 8080 is already in
  use", which used to render identically.
- **Env tab.** ConfigMap refs resolve on open, Secret refs mask behind an eye —
  see the section above for the rules and the reasons.
- New screenshot scenario `cluster-tab-port-forward-idle` (the state the tab
  actually opens in, and the one nothing rendered before), and
  `cluster-tab-pod-detail-environment` now drains the auto-resolve before writing
  its fixture values, same as `HelmReleaseDetail` — the offline client's
  "connection refused" otherwise lands on the capture's own `RunJobs()`.

**Verified this session**: build (0 warnings), **137/137 TUnit, 0 skipped**
(sandbox up, so the cluster-gated tests really ran), and all 34 scenarios × both
themes rendered. **Not verified**: neither surface has been driven by hand in the
running app this session — the eye toggle against a real Secret and a real
forward's start/stop are the two worth clicking. win-x64 NativeAOT remains the
build that has never run anywhere.

**Store-readiness pass:** the app was unusable for the audience it was about to be
submitted to. A reviewer on a clean Windows machine — the Microsoft Store's own
certification scenario — landed on an empty state whose only instruction was to run
a script from a repository they do not have, with no way to reach a cluster from
inside the app and nothing to look at without one. Two halves:

- **Kubeconfig discoverability.** `Kubeconfig.CandidatePaths`/`DiscoverPaths`/
  `LoadContextsAsync` take user-supplied extra paths, reported with a `picked`
  source label; "Open kubeconfig file…" writes one through `IStorageProvider` and
  persists the **path only** in `WorkspaceSettings.KubeconfigPaths`. A pick that
  yields no contexts is deliberately not remembered — otherwise a mis-pick poisons
  every subsequent start with Rescan re-running the same failure. The empty state's
  prose leads with the picker; the `scripts/sandbox-up` hint is gone (it lives in
  CONTRIBUTING.md and the README, where a contributor is already looking).
- **The demo cluster** — see the "Demo cluster" section above for the design rules.

**Verified this session** (no live cluster; Docker's daemon isn't running here):
build with 0 new warnings, **145/145 TUnit, 0 skipped** — the cluster-gated tests
returned early rather than running, so that count is the unit-only subset — all 38
screenshot scenarios × both themes, the linux-x64 NativeAOT publish with no new
warnings beyond the known DataGrid IL2104/IL3053, and **a hand-driven pass over the
real app under Xvfb** (Xvfb + xdotool + ImageMagick `import` substitute for the
DevTools MCP on a machine with no display; this works well and is worth reaching for
again). Clicked, with no kubeconfig and `$KUBECONFIG` unset: Explore demo cluster →
pod list; double-click a pod → logs streaming with plain (keyword-free) lines
visibly rendering; Env → Secret eye toggle decoding the demo Secret, ConfigMap and
`fieldRef` resolved in place; Events; the row context menu → Port-forward and Edit
YAML, both landing on their stated demo states; a kind with no demo data → the real
empty state; Helm → release list and detail; the advanced-view toggle → usage
columns with sparklines and gap-only rows; the switcher, both with the demo tab open
(under "Open") and closed (its own "Demo" group); a restart restoring the demo tab
from the sentinel path; and the file picker itself — picking a real kubeconfig
opened its context, and deleting that file and restarting degraded to the empty
state with the path listed `missing … (picked)`, no exception.

**Found and not fixed: the Linux and macOS release binaries cannot start.** A
NativeAOT-published `kubeNimbus` dies immediately with
`FileNotFoundException: The resource /Assets/app.ico could not be found` out of
`Avalonia.Platform.StandardAssetLoader`, from `MainWindow`'s `Icon`. It is
**pre-existing** — an AOT publish of the parent commit fails identically — and it is
not a missing resource: `!AvaloniaResources` and `app.ico` are both present in the
published managed assembly, and neither normalizing the csproj glob to forward
slashes nor fully qualifying the URI as `avares://kubeNimbus/Assets/app.ico` changes
anything, so it is Avalonia's asset registration under NativeAOT rather than
anything in this repo's item groups. Both experiments were reverted. Removing the
`Icon` attribute makes the same binary start and run correctly, which is how the
demo cluster was verified under AOT (embedded `Demo.*.json` read through
`GetManifestResourceStream` survives trimming intact — sidebar, list, logs, usage
and Helm all render from the single-file binary). Worth an upstream Avalonia issue;
until then `.github/workflows/release.yml` ships three RIDs that cannot launch.
win-x64 NativeAOT still cannot be built here (`Cross-OS native compilation is not
supported`) and remains the build that has never run anywhere.

**One-bar chrome pass:** the top of the window carried two bars — the OS title bar
and our 44px command bar — where every comparable app (VS Code, Chrome, Explorer,
Lens, Aptakube) carries one. See UI rule 12 above for the rules; the change itself
is small: `ExtendClientAreaToDecorationsHint` on Windows/macOS, the `TitleBar`
decoration role on `CommandBar` with `User` on everything clickable inside it,
`OffScreenMargin` honored on the root layout, the bar down from 44px to 40px, and
the wordmark deleted. Net ~36px of vertical chrome back, which is ~12% of the
inspector dock's 300px default — roughly two more log lines, at the top of every
window, permanently.

**Verified this session**: build (0 warnings), **145/145 TUnit, 0 skipped** (no
sandbox here, so that is the unit-only subset — the cluster-gated tests returned
early), all 38 screenshot scenarios × both themes, the linux-x64 NativeAOT publish
with no new warnings beyond the known DataGrid IL2104/IL3053, and the app running
under Xvfb on the **Linux** path, which is the path this change deliberately leaves
alone. The seven generated README screenshots were regenerated.

**The caption buttons turned out to be ours to draw.** The first cut of this pass
assumed Windows would keep drawing them, the way pre-12 `PreferSystemChrome` did.
Reading Avalonia 12's Win32 backend says otherwise — an extended client area reports
`RequestedDrawnDecorations = TitleBar` and calls `DisableCloseButton` on the HWND —
so without a decorations template the window would have had **no way to close**, and
with Fluent's stock one it would have had a second title bar and the window title
painted over the command bar. `CommandBarWindowDecorations` in Theme.axaml is the
answer; see UI rule 12.

**Verified for the drawn decorations**, since neither the harness nor a plain Linux
run builds them: with `X11PlatformOptions.EnableDrawnDecorations` on and the platform
gate forced open (both reverted), the app renders the three buttons at the right of
the 40px bar, sized and hovering correctly, with the command bar's controls stopping
exactly where the caption strip starts — and no Fluent title bar or title text over
either. The wiring itself is platform-independent (`AttachCaptionButtons` looks the
`PART_*` names up and subscribes `Click`).

**macOS needs no drawn decorations, and one thing beyond that.** `Avalonia.Native`
reports `NeedsManagedDecorations = false` and `RequestedDrawnDecorations = None`, so
AppKit keeps the traffic lights and the theme above is never built there; the height
hint is forwarded to the native window (`SetExtendTitleBarHeight`), which is what
lines the lights up with a 40px bar rather than a 30px one. What it *did* need is the
full-screen case: its backend zeroes `ExtendedMargins` in full screen and the lights
go away, so a reserve fixed at construction leaves a dead 78px hole — and on macOS
the green light is the ordinary way in. `ApplyCaptionReserve` now recomputes off
`WindowDecorationMargin`, which fixes the same hole on Windows (where full screen
strips every drawn part) for free.

**Still not verified, and it is the half that matters**: no Windows or macOS machine
has run this. First things to check on a Windows box, in order: that the buttons
appear and work (close especially — the X11 experimental path hovered but did not
activate, which may be its own quirk or may not), that 3 × 45 DIPs is the right
reserve at 100% *and* 150% scaling, that dragging/double-click/Snap Layouts work from
the empty tab strip, that a maximized window isn't clipped at the top, and that Mica
still renders now that the bar is inside the extended area. On macOS: that the
switcher button clears the traffic lights at 78 DIP, that the lights sit centred in
the 40px bar, and that entering full screen with the green button collapses the
reserve rather than leaving a gap. The full-screen path is the one piece that could
not be driven even under X11 — Xvfb has no window manager, so `WindowState` changes
have nothing to honour them.

**Windows validation pass (2026-08-04):** the half above that mattered — a real
Windows box — finally ran this. Sandbox up (`docker start kubenimbus-sandbox`, no
`-Recreate` needed), build 0 warnings, **145/145 TUnit, 0 skipped** (run via the
test `.exe` directly — `dotnet test --project` is still broken on this SDK, see
below), app launched against the live cluster via the Avalonia DevTools MCP (the
dev-run process isn't Start-Menu-registered, so `computer-use` couldn't attach for
real mouse drag/Snap-Layouts gestures — that specific gap remains). Confirmed via
DevTools, structurally and functionally:

- `ExtendClientAreaToDecorationsHint=True`, `WindowDecorationMargin=0,40,0,0` — one
  40px bar, no second OS title bar underneath.
- `PART_MinimizeButton`/`PART_MaximizeButton`/`PART_CloseButton` exist at
  `Bounds 1145,0,135,40` in a 1280-wide window — exactly `3 × CaptionButtonWidth`
  (45 DIP) from the right edge, confirming the reserve math.
- **All three buttons are functionally real**, not just present: clicking Minimize
  set `WindowState=Minimized` (`IsActive` false); clicking Maximize grew `ClientSize`
  to the full physical panel (`3792×1600`) with `WindowDecorationMargin` unchanged;
  clicking Close ended the process cleanly (confirmed via `tasklist`, and the
  DevTools call itself timed out mid-request as the connection died — expected). This
  is the scenario UI rule 12 warns about directly: Windows disables the *native*
  close button under an extended client area, so a non-functional custom one would
  have shipped a window with no way to close.
- **Close → relaunch → workspace-restore** round-tripped correctly: relaunching
  reconnected to the live cluster, restored the `kubenimbus-sandbox` tab, and kept
  the Advanced-view setting — `WorkspaceSettings` persistence holds up with the new
  chrome.
- **Advanced-view toggle** (UI rule 8b — the double-toggle class of bug that shipped
  broken three times already) — one click cleanly hid CPU/Memory columns, sparklines
  and sidebar kind-count badges together; one click restored them; `IsChecked`
  landed correctly each time. No regression.
- No exceptions or errors in either session's app log.

**Not covered by this pass**: real mouse drag-to-move, double-click-to-maximize and
Win11 Snap Layouts on the caption strip (needs actual OS-level drag, which neither
DevTools synthetic input nor `computer-use` could reach for this process), 150%
DPI scaling, and multi-monitor. Also hit, and worth naming so it isn't mistaken for
an app bug: DevTools' synthetic `Click` reliably drives `Button`/`ToggleButton`
controls (used above) but returned `handled:false` against `DataGridRow` and
`ComboBoxItem` in this session, and the live-watch pod list recycles virtualized
`DataGridRow` node IDs across ticks — so a hands-on click-through of row
selection/double-click-to-open, the namespace picker, and post-redesign
port-forward/exec is still owed on a real mouse. Port-forward and exec's *last*
full live-cluster verification predates the Fluent form/state pass's visual
redesign of the port-forward pane (see the 2026-08-04 "Live-cluster validation
pass" above).

**List search + column gutter pass:** two complaints from the running app, both about
the list. See UI rules 13 and 14 above for the rules they added.

- **There was no way to search the list by name.** The sidebar's filter box narrows
  kinds, and people reasonably read it as *the* filter; nothing narrowed the objects.
  New: `RowFilter`/`VisibleRows` on `ClusterTabViewModel`, a search box in the list
  header (same shape as the sidebar's, with a "12 of 87" beside it), Ctrl/Cmd+F on the
  window, Esc/Enter in the box, `ResourceRowViewModel.Matches`, an `IsFilterEmpty`
  no-match state, and `cluster-tab-list-filtered{,-empty}` screenshot scenarios.
- **Columns ran into each other.** `48 MiB16d`, and — worse — a `—` placeholder in the
  Memory column abutting Age, which reads as a *negative age* and was reported as one.
  Root cause was Fluent's left-only `DataGridCell` padding, not the values. The gutter
  itself landed in 6a48547 at 12px; this pass cut it to 10px and re-cut the column
  minimums with it, because 12px on nine columns pushed **Age off the right edge** at
  the 1280px the harness renders (UI rule 14). Both grids in `ClusterTabView` get it,
  and so does every other DataGrid in the app.
  The fleet list still clips its rightmost headers at 1280px — ten columns do not fit
  in ~910px and horizontal scroll is the answer — but that is unchanged from before
  the gutter, checked by rendering the scenario with Fluent's padding put back.

**Verified this session**: build with 0 new warnings, **145/145 TUnit, 0 skipped**,
and all 40 scenarios × both themes rendered (the harness is the XAML smoke test).
**Not verified**: nothing has been driven by hand in the running app —
Ctrl/Cmd+F focusing the box, Esc handing focus back to the grid, and the filter
surviving a live watch tick (a Modified event on a filtered-out row must not make it
reappear) are the three worth clicking.

**Design-parity pass (settings, help system, one design language):** the complaint was
that kubeNimbus looked visibly worse than pgNimbus *even after* the shared design
system was extracted, that the two top bars did not match, and that kubeNimbus had no
settings and no help system. Four halves, and the first one explains the other three:

- **The shared library was in sync; the extraction was incomplete.** `shared/nimbusUi`
  was byte-identical in both repos (modulo CRLF) — but what had been pulled up was only
  the *shell* vocabulary (tokens, `card`/`layer`/`chip`/`searchpill`/`toolbar`/`accent`,
  scrollbars, `statusBar`, `sectionHeader`). pgNimbus's ~350 lines of **Fluent control
  retheming** stayed behind in its own `Theme.axaml`, so kubeNimbus rendered every
  `TextBox`, `ComboBox`, `NumericUpDown`, `ListBox`, `TreeView` and `DataGrid`, and had
  no `.soft`/`.danger` button family at all, as **stock Fluent** beside pgNimbus's toned
  versions. That is the whole of "looks worse", and it was invisible from inside either
  app — you only see it with the two windows side by side. Now
  `shared/nimbusUi/Theme/Controls.axaml`, consumed by both. `TabItem` stays per-app as
  before, and `TabControl.segmented` newly joins it on DESIGN.md's not-shared list:
  kubeNimbus does that job with `ListBox.segmented` + `TabControl.headerless` on
  purpose (UI rule 10).
- **The command bar now reads the same left to right as pgNimbus's** — see UI rule 15.
  New: the `☰` app menu and the sidebar toggle at the left, `⚙` preferences on the
  right, and the right cluster reordered to pgNimbus's order. The sidebar toggle is a
  real feature, not just a matching glyph: `MainWindowViewModel.IsSidebarVisible` is
  shell-owned and mirrored onto every tab like `IsAdvancedView`, and
  `ClusterTabView.ApplySidebarVisibility` collapses the **column**, not just the panel
  — hiding a Grid child leaves its column at full width, which would have left a third
  of the content area blank and the list exactly as narrow as before.
- **A settings system**, `settings.json` beside the existing workspace — see "Settings,
  and what belongs in which file" above for the split, the migration and the five
  rules. It also connects something that had been dead: the shared hotkey resolver has
  supported a Ctrl/Cmd override since extraction, but kubeNimbus never called
  `Initialize`, so the setting existed in code and was unreachable.
- **A help system**: `CommandCatalog` in Core as the single source for key bindings,
  palette titles, the F1 sheet and a generated `docs/keyboard-shortcuts.md`, plus
  `CommandTip` for tooltips that carry a live shortcut, an About window, and a cheat
  sheet rebuilt with sectioned keycap chips instead of a flat list of monospace
  strings. See "The command catalog" above for the six rules. This also fixed a latent
  bug: `Hotkeys.cs` held its gestures in `static readonly` fields, which the shared
  resolver explicitly warns against — harmless while the modifier could never change,
  a real bug the moment the scheme became a preference.

**win-x64 NativeAOT now builds *and launches* — the first time either has happened.**
It was still failing at the same `/Assets/app.ico` `FileNotFoundException` that
CLAUDE.md had recorded for linux/osx, which means the bug was never platform-specific:
**every** release RID shipped a binary that could not start. The cause is narrower than
"Avalonia asset registration under AOT": `Icon="/Assets/app.ico"` goes through
`IconTypeConverter.CreateIconFromPath`, and it is the converter's resolution of a
*relative* path that does not survive — which is why fully qualifying the URI in the
XAML attribute (tried in an earlier session, reverted) did not help. Loading the same
file by absolute `avares://` URI through `AssetLoader` in code skips the converter
entirely; that is what pgNimbus has always done for its window icons, and why its AOT
binaries start. `WindowIcons.Apply` does it here, and the `Icon=` attributes are gone
from both windows. The published binary was launched and showed a real window.

**Verified this session**: build with **0 warnings**, **155/155 TUnit, 0 skipped** (the
10 new catalog/docs tests among them), all **84** screenshots (42 scenarios × both
themes) including the two new windows, the **win-x64 NativeAOT publish** with no new
warnings beyond the known DataGrid IL2104/IL3053, and that binary launching. pgNimbus
was rebuilt and re-rendered against the moved styles (30 screenshots) and is unchanged.

**Not verified**, in rough priority order: nothing in this pass has been driven by hand
against a live cluster — the preferences page's kubeconfig add/remove, the sidebar
toggle at various window widths, Ctrl/Cmd+, and Ctrl/Cmd+B, and above all **changing
the hotkey scheme while the app is open** (the rebuild-on-`Changed` path: bindings,
cheat sheet and tooltips all have to re-render, and `BuildKeyBindings` clearing first is
the part that would fail quietly). The linux/osx NativeAOT binaries should also be
re-published to confirm the icon fix unblocks them too — the diagnosis says it will,
but only win-x64 has actually been run. And the macOS half of UI rule 15/16 (traffic
lights beside the new left cluster, DWM caption colour has no macOS equivalent) is
untested, as ever.

**Launch-check pass (VER-2):** the gap the two paragraphs above describe is now closed
mechanically rather than by remembering. CI and the release workflow **run** every
binary they publish; see "The launch check (`--smoke-test`)" under Verification
workflow for the design, and the Releasing section for where it sits in the matrix.
New: `src/KubeNimbus.App/SmokeTest.cs`, a `--smoke-test` flag on `Program.Main`, one
`SmokeTest.Attach(desktop)` call in `App.OnFrameworkInitializationCompleted`, a launch
step in `ci.yml`'s `aot` job, and three OS-conditional launch steps in `release.yml`
placed between Publish and Stage.

**The negative test is the deliverable, and it was actually run.** Restoring
`Icon="/Assets/app.ico"` on `MainWindow` and re-publishing linux-x64 AOT produced a
publish that was *indistinguishable from a healthy one* — same two DataGrid
IL2104/IL3053 warnings, exit 0 — and the launch check then failed it:

```
SMOKE-FAIL (66) startup threw System.IO.FileNotFoundException: The resource /Assets/app.ico could not be found.
   at Avalonia.Platform.StandardAssetLoader.OpenAndGetAssembly(Uri, Uri)
   at Avalonia.Markup.Xaml.Converters.IconTypeConverter.CreateIconFromPath(ITypeDescriptorContext, String)
   at KubeNimbus.App.Views.MainWindow.InitializeComponent(Boolean)
STEP EXIT=66
```

The break was reverted and `MainWindow.axaml` re-verified byte-identical to HEAD. The
watchdog was proven separately (`KUBENIMBUS_SMOKE_TIMEOUT_SECONDS=1` against a ~1.4 s
Debug start → exit 67), and a no-flag launch was confirmed unchanged: the window is
still there under `xdotool search --name kubeNimbus` and the process still waits to be
closed.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (one
pre-existing CS8425 in `AsyncMergeTests.cs`, untouched), **155/155 TUnit, 0 failed, 0
skipped** via `--project` (no sandbox here, so that is the unit-only subset — the
cluster-gated tests return early), all **84** screenshots (42 scenarios × both themes),
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid pair,
and the launch check itself against that published binary — `SMOKE-OK main window
rendered at 1280x800 after 146 ms`, exit 0, under Xvfb.

**Not verified here, and it is the majority of what this pass adds**: the win-x64,
linux-arm64 and osx-arm64 legs of `release.yml` have never executed — this container is
linux-x64 only, and it has no `pwsh`, so even the Windows step's PowerShell was not
syntax-checked (the YAML around it was). Two platform assumptions are therefore
untested and are the first things to watch on the next tagged build or a
`workflow_dispatch` dry run: that a GitHub Windows runner's session lets a GUI-subsystem
process create a window at all, and that an unbundled (no `.app`) osx-arm64 binary can
open an NSWindow on a macOS runner. Both are expected to work and both would show up as
a *failed launch check* rather than a bad release, which is the right way round — but a
dry run is much cheaper than finding out on a tag. VER-1 is the item that will confirm
the three non-Windows RIDs actually start once these runners exist.

**linux-x64 is now confirmed on a real runner (VER-1, partial).** The merge of the pass
above pushed to `main`, which ran `ci.yml`, which now carries the launch check — so the
first real-runner evidence arrived as a side effect of landing it. CI run
[31902245451](https://github.com/Shman4ik/kubeNimbus/actions/runs/31902245451) at commit
`961b085`, job *NativeAOT publish (linux-x64)* on `ubuntu-latest`, step **Launch check
(linux-x64)** — conclusion `success`:

```
[smoke 0 ms] launch check starting (timeout 90s)
[smoke 793 ms] main window opened
SMOKE-OK main window rendered at 1280x800 after 794 ms
```

Two things this settles beyond "the step passes". The published linux-x64 AOT binary
**starts and composites a frame on a machine that is not this container**, which is the
half of the `WindowIcons.Apply` fix that had only ever been argued from a diagnosis; and
794 ms on a cold hosted runner (against ~100–150 ms locally) is the number to compare
future runs against before reading a slow start as a regression.

**`linux-arm64` and `osx-arm64` remain unconfirmed, and cannot be confirmed from here.**
Those legs live only in `release.yml`, which runs on a tag or a `workflow_dispatch`.
NativeAOT cannot cross-compile, so this linux-x64 container cannot build either one, and
the GitHub App token this repo's agents run under lacks `actions: write` — a dispatch
returns `403 Resource not accessible by integration`. So the remaining two thirds of
VER-1 need one of exactly two things: a human pressing **Run workflow** on `release.yml`
with `dry_run: true`, or `actions: write` granted to the integration. Until then the
release workflow still ships two RIDs on a diagnosis rather than an observation — which
is the same shape of gap that produced the v0.1.0 breakage, and the reason VER-1 is
recorded as `blocked` rather than quietly closed on one passing RID.

**Workload-actions pass (FEAT-1):** the app's first mutating actions beyond a YAML
apply/delete — scale, `rollout restart` and delete-a-pod, on the row context menu and in
the palette, each armed rather than fired. See "Mutating workload actions" above for the
six rules and UI rule 17 for the strip they all share. New: `WorkloadActions.cs` and
`ClusterClient.Workloads.cs` in Core (+ 13 `WorkloadActionsTests`), `Subresources`/`Verbs`
on `ResourceDescriptor` with the discovery parser to fill them, `RowActionViewModel` and
the strip in `ClusterTabView`, two app-local icons, two context-menu items, three palette
entries and four screenshot scenarios (`cluster-tab-row-action-{scale,restart,failed}`,
`cluster-tab-demo-scale-unavailable`).

Two things worth keeping from doing it:

- **The strip rendered as nothing, silently, and compiled clean.** A `Border` that both
  set `DataContext="{Binding PendingRowAction}"` and declared
  `x:DataType="vm:RowActionViewModel"` compiles — `x:DataType` re-roots that element's
  *own* bindings too, so the DataContext binding itself was resolved against the wrong
  type — and produces an invisible panel with no error anywhere. Only the screenshot
  showed it. It is a `ContentControl` + inline `DataTemplate` now (UI rule 17).
- **A fixture tab has no `Client`, so the three commands refuse in the harness — and are
  right to.** That is the disconnected case they must not act in. The fixture scenarios
  therefore build `RowActionViewModel` directly against the offline client, exactly as
  the exec/YAML/Helm scenarios build their inspector tabs; the demo scenario is the one
  that goes through the real command, because the demo path is designed to work without
  a client.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` reappears only on a from-scratch test-project
build and is untouched), **168/168 TUnit, 0 failed, 0 skipped** via `--project` — the 13
new ones among them, and 0 skipped here means the unit-only subset, since the
cluster-gated tests return early with no sandbox — all **92** screenshots (46 scenarios ×
both themes), the linux-x64 NativeAOT publish with no new warnings beyond the known
DataGrid IL2104/IL3053, and the launch check on that published binary under Xvfb
(`SMOKE-OK main window rendered at 1280x800 after 442 ms`, exit 0).

**Not verified, and it is the whole live half.** No cluster came up here (Docker's blob
CDN is blocked by this session's egress policy), so **not one of these three actions has
been run against an API server**: the scale patch, the restart annotation and the delete
are all argued from the wire format and pinned by unit tests, never observed. Nor has any
of it been driven by hand in the running app — the context menu items, the palette
entries, the replica box's keyboard behaviour and the `ConfirmDeletes: false` path (which
fires the delete straight from the menu) were all verified by construction or in the
headless harness. First things to do on a machine with a sandbox: scale
`demo-shop/shop-web` up and down and watch the list follow it; restart it and confirm the
pods roll rather than all disappear at once (`kubectl get pods -w` beside it), and that
`kubectl get deploy shop-web -o yaml` shows the `restartedAt` annotation; restart twice
inside one second and confirm the second is a no-op, as it is for kubectl; and check the
403 path with a `kubectl --as` impersonated user that cannot patch.

**Row-filter regression pass (VER-5):** UI rule 13's central invariant — `Rows` is the
watch's own complete list, `VisibleRows` is the rendered projection — had no automated
check, and could not have had one: the App layer had no test project, and the rule's
code cannot move to Core. New: `tests/KubeNimbus.App.Tests` (see "View-model tests"
under Verification workflow for why it is shaped the way it is) and
`ClusterTabRowFilterTests` in it, 13 tests over the real `Apply`/`ApplyFleet`.

**The demonstration is the deliverable here, not the passing run.** A regression test
for an invariant is worth exactly what it costs to break the invariant and watch it
fail, so both wrong implementations the item names were actually written and run:

- Making `RebuildVisibleRows` filter `Rows` in place (removing non-matching rows from
  `Rows` *and* `_rowsByKey`) turned **9 of 13** red. The headline one reported
  `Expected "api-7f9, cache-0, web-1" but received "api-7f9"` — `Rows` had been cut
  down to what was on screen — and `Repeated_modifications_…` reported `Expected 3
  but found 2`, which is the resurfacing itself: the key map had lost `cache-0`, so
  the next Modified for it built a fresh row and added it back.
- Making the watch-apply path drop rows the filter no longer matches turned **5 of
  13** red, the headline one reporting `Expected "api-7f9, cache-0, web-1" but
  received "api-7f9, web-1"`.

Both were reverted and the file diffed back to its intended state. What this says about
the assertions: the ones that catch this are on `Rows`, on row *object identity* across
a Modified, and on `RowFilterSummary` ("1 of 3", which a list filtered in place prints
as "1 of 1") — asserting only on `VisibleRows` would have passed under both breaks,
because a filtered-in-place list still renders correctly until the next event.

Two smaller things the pass settled. `IsListEmpty`/`IsFilterEmpty` are pinned as three
states, not two, including the loading one — and writing that test showed a bare
`ClusterTabViewModel` reports `IsListEmpty == false`, because `RecomputeListEmpty` has
never run; that is right (a tab that has never watched anything has no list to be
empty), so the test reaches the settled-empty state through a `Reset`, the way an empty
namespace's initial sync does. And `RowFilter` clearing on a kind change *is* cheaply
reachable — through the real `SelectKindCommand`, whose `RestartWatch` returns
immediately with no client, which is the disconnected state and not a contrivance.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **1 warning, and it is the
pre-existing CS8425 in `AsyncMergeTests.cs`** — 0 new; **168/168 Core TUnit, 0 failed, 0
skipped** (no sandbox here, so that is the unit-only subset — the cluster-gated tests
return early) and **13/13 App TUnit**, both via `--project`; the break/revert runs above;
and all **92** screenshots (46 scenarios × both themes) still render, run as the XAML
smoke test rather than for anything visual — nothing here changes a pixel and no
committed PNG was touched. **Not verified**: no NativeAOT publish was run — the change
adds no package, no binding and no serialization, and the only App-code edit is two
`private` methods becoming `internal`, which the trimmer treats identically; and nothing
was driven against a live cluster, because there is nothing here to drive (these tests
replace the watch, they do not exercise it). The one thing a reviewer should weigh rather
than take on trust is that `InternalsVisibleTo` line: it is the price of testing the real
watch-apply path instead of a copy of it.

**Terminal-launch pass (FEAT-16):** "open a terminal on this cluster" — the daily
gesture people leave a GUI for, and the last thing this app made you go and do by hand.
See "The machine's own terminal" above for the six rules. New: `TerminalLauncher.cs` in
Core (+ 21 `TerminalLauncherTests`), `ClusterTabViewModel.OpenInTerminalCommand` and the
notice `infoBar` it lands in (+ 6 `ClusterTabTerminalTests`), a `CommandId.OpenTerminal`
catalog entry (palette-only, so `docs/keyboard-shortcuts.md` is unchanged), a ☰ menu
item, and two screenshot scenarios — `cluster-tab-terminal-no-kubectl` and
`cluster-tab-demo-terminal-unavailable`, the second of which runs the real command end to
end.

**The thing this pass learned, and the reason two platforms do not use the obvious
command:** `wt.exe` and `open` both hand the request to *another* process, which is what
then spawns the shell — so the shell inherits **that** process's environment rather than
the one we so carefully set. On Windows Terminal this is the monarch/peasant model
(a second tab is created inside the already-running window); on macOS it is
LaunchServices. Either way the terminal opens looking correct and pointed at whatever
cluster that process happened to start with, which is the single failure this feature
must not have. Rule 4 of that section is the answer; it is worth re-reading before
"simplifying" the Windows path back to `wt.exe`.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the
one warning is the pre-existing CS8425 in `AsyncMergeTests.cs`), **190/190 Core TUnit,
0 failed, 0 skipped** and **19/19 App TUnit**, both via `--project`; all **48** scenarios
× both themes rendered (96 PNGs), including the two new ones in light and dark; the
linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 522 ms`, exit 0).

**And the launcher itself was actually run on Linux**, which the unit tests deliberately
do not do: a scratch harness put a fake `xdg-terminal-exec` on PATH that records its own
environment, and the child process was confirmed to receive
`KUBECONFIG=<overlay>:<real file>` and `KUBENIMBUS_CONTEXT`. The overlay written to disk
was confirmed to contain a context name and no credential. And the merge claim the whole
design rests on was checked against an independent implementation of it — the Kubernetes
client's own `$KUBECONFIG` chain handling: with the overlay first the resolved namespace
is the pinned context's (`payments`), without it the real file's own `current-context`
wins (`staging`), and the real file is byte-identical afterwards. Removing the fake
terminal and adding a fake `kubectl` produced the other outcome, `NoTerminal` with
`KubectlMissing` false.

**Not verified, and it is most of "all three platforms"**: no Windows box and no macOS
box, so `pwsh.exe`/`powershell.exe`/`cmd.exe` starting with a visible console from a
`WinExe` parent, and `open -a Terminal <script>` running the generated `.command` and
landing in a login shell, are both argued from the platform docs and have never been
run. Nor has any *real* terminal emulator been driven — this container has none, so the
Linux path is verified against a shell script standing in for one, which proves the
environment is handed over but proves nothing about a client/server emulator forwarding
it (gnome-terminal's D-Bus `environ` forwarding is the specific thing to watch). No live
cluster either, so nothing has been checked by typing `kubectl get pods` in a window this
feature opened — which is, in the end, the acceptance criterion.

**Hotkey-scheme drive-through (VER-3):** the Ctrl/Cmd preference had never been changed
with the app running — the whole re-render path (key bindings, the F1 sheet, every
`CommandTip` tooltip) was argued from the code and from the comment on
command-catalog rule 2, and `BuildKeyBindings` clearing first was the part that would
fail with nothing on screen to say so. It was driven, under Xvfb, and **it works** —
nothing was broken and nothing needed fixing. What this pass adds is the evidence and a
regression check, because "we read the code and it looked right" is exactly what was
already true before it.

**Linux is a real test bed for this, which is not obvious.** `Nimbus.Ui.Hotkeys.Resolve`
only consults the platform for `"auto"`; an explicit `"mac"` resolves to
`KeyModifiers.Meta` everywhere, and Avalonia's X11 backend maps Mod4 (Super) onto Meta.
So `xdotool key super+k` **is** the Cmd chord here, and the scheme is fully observable
without a Mac. What Linux cannot show is whether a real macOS keyboard's Cmd reaches the
same place — that is still untested.

Driven with `Xvfb :99` + `xdotool` + `import`, against a Debug build with
`XDG_CONFIG_HOME` redirected to a scratch dir, no kubeconfig (so the shell's empty state,
then the demo cluster). Preferences → Shortcut modifier → **Cmd**, with the window open,
and then, in order:

- **Bindings re-render.** `super+k` opened the palette; `super+p` opened the switcher;
  `super+f` focused the list search box and typing `redis` filtered the demo pods to
  "1 of 6"; `super+b` collapsed the sidebar. The command bar's palette pill relabelled
  itself from `Ctrl+K` to `Cmd+K` without a restart, and the switcher's own footer hint
  to `Cmd+1…9 jump to tab` — both are set by `BuildKeyBindings`, so they double as a
  witness that it ran.
- **The F1 sheet re-renders.** Every cap redrew as `Cmd` (`Cmd P`, `Cmd F`, `Cmd R`,
  `Cmd S`, and the note `Cmd+1 … Cmd+9`) — and the exec-pane rows still read `Ctrl C`
  and `Ctrl D`, which is command-catalog rule 5 holding where it is actually read.
- **Tooltips re-render.** The sidebar toggle read `Show or hide the resource sidebar
  (Cmd+B)` and the cog `Preferences… (Cmd+,)`. So did the switcher button's
  `SwitcherTooltip`, which is *not* a `CommandTip` and has no `Hotkeys.Changed`
  subscription at all — it survives because `ToolTip.Tip` holds a `TextBlock` whose
  binding is re-evaluated each time the popup is attached. Worth knowing that it works
  for a different reason than the other three: a future tooltip that caches its text
  would not.
- **The old gesture stops working.** `ctrl+k`, `ctrl+f` and `ctrl+b` all did nothing —
  no palette, an untouched search box, an unchanged sidebar. The reverse direction was
  driven too (back to **Auto**): `super+k` went dead and `ctrl+k` came back, with the
  labels following.

**The regression check, and what it can and cannot cover.** `HotkeySchemeTests`
(`tests/KubeNimbus.App.Tests`) pins the four, and the reason it needed a small seam is
that a `MainWindow` cannot be constructed without a running Application — so the clearing
rebuild moved out of `MainWindow.BuildKeyBindings` into
`CommandBindings.RebuildWindowBindings(IList<KeyBinding>, …)`, which a test can drive
twice over one list. The window keeps the half that is genuinely its own (the commands
that act on it, the two labels). `CommandTip.Compose` gained a control-free overload for
the same reason. Both breaks were written and confirmed red before the tests were called
done, same discipline as VER-5:

- Deleting `bindings.Clear()` — **3 of 27 red**, reporting `Expected to be 16` (the list
  had doubled), `KeyModifiers.Control` still present after switching to Meta, and
  `Ctrl+1` still bound. This is the silent failure the item names, and note that it
  breaks *nothing visible*: the new chord works and every label is correct.
- Caching `Hotkeys.PrimaryLabel` in a `static readonly` field in `ShortcutsViewModel` and
  `CommandTip` — the exact trap the shared resolver's own doc-comment warns about —
  **2 of 27 red**, on the cheat-sheet caps and the tooltip text.

What it does **not** cover, and could not: that `MainWindow` and `MainWindowViewModel`
still *subscribe* to `Hotkeys.Changed` at all. Deleting either subscription leaves every
test green and every projection correct-when-rebuilt; only the drive-through above catches
it. That is a window-level and shell-view-model-level wiring fact, and pinning it needs
`Avalonia.Headless` — which the App.Tests project deliberately does not start.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` only appears on a from-scratch test-project
build and is untouched); **190/190 Core TUnit** and **27/27 App TUnit**, 0 failed, 0
skipped, both via `--project` (no sandbox here, so the Core count is the unit-only subset
— the cluster-gated tests return early); the two break/revert runs above; all **48**
scenarios × both themes rendered (96 PNGs, as the XAML smoke test — nothing here changes
a pixel and no committed PNG was touched); the linux-x64 NativeAOT publish with no new
warnings beyond the known DataGrid IL2104/IL3053; `--smoke-test` on that published binary
under Xvfb (`SMOKE-OK main window rendered at 1280x800 after 107 ms`, exit 0); and the
**whole drive-through re-run against the refactored build**, since the first pass had
verified the code the refactor then moved.

**Not verified**: no macOS or Windows box, so the scheme has only ever been exercised
with X11's Meta standing in for Cmd — the `"auto"` branch resolving to Meta *because the
platform is macOS*, and a real Cmd keypress arriving as `KeyModifiers.Meta` on
`Avalonia.Native`, are both still argued rather than observed. No live cluster (the demo
cluster was used for the list-search half), so `Ctrl/Cmd+R`'s refresh and `Ctrl/Cmd+S`'s
YAML apply were not driven under the changed scheme; both are ordinary catalog rows in
the same rebuilt list as the four that were. And the drive-through is manual: there is no
automated Xvfb gesture test, so this evidence is a session's record, not a check that
re-runs.


**Exec terminal pass (FEAT-10):** the exec pane renders a real VT emulator instead of
stripping ANSI, so the full-screen tools people exec in for work at all. See "The exec
terminal" above for the seven rules, the upstream defect it found, and the vendoring
fallback. New: a `SvcSystems.UI.Terminal` package reference (→ `XTerm.NET/1.0.15` →
`Unicode.net`, `Wcwidth` — the graph the research predicted, exactly),
`ExecTabViewModel` rewritten around `TerminalControlModel` (bytes in on the same 50 ms
flush tick, `UserInput` bytes straight back to `StdIn`, the emulator's own cols/rows to
`ResizeAsync`), `ExecView` down to one chrome row plus the terminal, four terminal
palette resources in `Styles/Theme.axaml`, two `ExecCopy`/`ExecPaste` catalog rows
(Ctrl+Shift+C/V — the docs golden file regenerated), and two new screenshot scenarios
(`cluster-tab-exec-fullscreen`, `cluster-tab-exec-no-shell`). **Deleted**:
`Terminal/TerminalOutputBuffer.cs`, 419 lines of hand-written C0/CSI/OSC parsing whose
own doc-comment admitted it was "not a VT emulator — no addressable screen grid, no
colour attributes and no alternate buffer". Core is untouched: the WebSocket, the
channel-3 read and the bash→sh→ash probe are byte-for-byte what they were.

**`vi`, `top` and `mc` were actually run — against a local PTY, not a cluster.** There
is no live cluster in this container (Docker's daemon starts, but pulling `rancher/k3s`
still dies on a 403 from `production.cloudfront.docker.com`), so the acceptance
criterion was reached the only other way it can be: a scratch harness started `script
-q -c <program> /dev/null`, pumped its stdout through the *same* model the exec pane
feeds, wrote the control's `UserInput` bytes back to its stdin, and rendered the control
with Skia. `top` drew its full screen with columns aligned; `vim -u NONE -c 'syntax on'`
drew a YAML file with syntax colour, `~` filler and a status line, entered the alternate
buffer, and took typed input (`iHELLO` inserted, i.e. keystrokes round-tripped into a
real program); `mc` drew both panels, the box drawing, the menu bar and the F-key bar in
colour. That proves everything except the transport, which is the half that did not
change.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` appears only on a from-scratch test-project
build and is untouched); **190/190 Core TUnit** and **27/27 App TUnit**, 0 failed, 0
skipped, both via `--project` (no sandbox, so the Core count is the unit-only subset —
the cluster-gated tests return early); all **50** scenarios × both themes rendered (100
PNGs), including the three exec ones; the linux-x64 NativeAOT publish with **no new
warnings beyond the known DataGrid IL2104/IL3053** — the new package contributes none,
which is what the research had claimed for 1.0.3 and now holds for 1.1.0 — and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 351 ms`, exit 0). The emulator's own behaviour was pinned by a headless
probe rather than by argument: `Ctrl+C → 0x03`, `Ctrl+D → 0x04`, `Tab → 0x09`,
`Up → ESC [ A`, `Enter → 0x0D`, every one `handled=true`; `ESC[2J ESC[H` clears and
homes, `ESC[3;10H` places text at row 3 column 10, `ESC[?1049h/l` enters and leaves the
alternate buffer and restores what was under it, and `ESC c` (what Reconnect sends)
empties the buffer. The same probe is where the reverse-video defect and the split-UTF-8
result above come from.

**Not verified, and the transport is the whole of it.** No exec session has been opened
against an API server with this pane: `ExecAsync`, the probe and `ResizeAsync` are
unchanged code, but "unchanged" is an argument, not a run. First things to do on a
machine with a sandbox: exec into `demo-shop/shop-web` and confirm the shell's prompt
arrives and `vi` opens; drag the dock splitter and confirm the remote PTY follows (the
resize is now the emulator's real geometry, and `stty size` inside the container is the
check); reconnect into a session left inside `vi` and confirm the reset lands; and
confirm Ctrl+C interrupts a `tail -f` rather than merely being marked handled. Nothing
has been driven by hand in the running app either — the pane cannot be opened without a
cluster, and the demo tab renders its unavailable notice instead — so focus-on-open, the
right-click menu and Ctrl+Shift+C/V are verified by construction and by the headless
probe, not by a mouse. And the reverse-video defect is upstream and unfixed; `top`'s
header renders unhighlighted today.

**CRD printer-columns pass (FEAT-2):** a CRD list now wears the columns the CRD itself
declares, so `kubectl get certificates` and this app's Certificate list show the same
thing. See "CRD printer columns" above for the seven rules and the two traps. New:
`PrinterColumns.cs`, `SimpleJsonPath.cs`, `ClusterClient.PrinterColumns.cs` and
`RelativeTime.cs` in Core (+ 39 `PrinterColumnTests`), `PrinterCells` on
`ResourceRowViewModel` with ten XAML-declared slot columns and
`ClusterTabView.ApplyPrinterColumns` (+ 10 `ClusterTabPrinterColumnTests`), a
cert-manager CRD and five Certificates in the demo dataset, printer columns on two of
the three sandbox CRDs, and two screenshot scenarios
(`cluster-tab-crd-printer-columns{,-wide}`). `RelativeTime` moved from beside the list
row into Core because a `type: date` column is an age too, and Core is where "format a
duration" belongs; nothing else about it changed.

**The negative half of the acceptance criterion was measured, not argued.** "Built-in
kinds are untouched" is the easiest thing here to break silently, so the whole harness
was rendered from a worktree at the parent commit and diffed byte-for-byte against this
one: **all 102 pre-existing PNGs are identical**, and the only new files are the four
this pass adds. That covers every list, inspector and shell scenario in both themes.

**The header collision is the bug worth remembering**, and only the screenshot found it.
A printer slot's header becomes a CRD author's string, and cert-manager calls one of its
Certificate columns **Ready** — which `ApplySummaryColumns` then matched as the grid's
own Ready column and hid, so the most important column on the list this feature exists
to fix was silently missing. `ClusterTabView.FixedColumns` now excludes the slots from
every header match. The same trap sits one column name away from Status, Details,
Restarts, CPU, Memory and Cluster.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**;
**229/229 Core TUnit** and **37/37 App TUnit**, 0 failed, 0 skipped, both via
`--project` (no sandbox here, so the Core count is the unit-only subset — the
cluster-gated tests return early); all **53** scenarios × both themes rendered (106
PNGs), including the two new ones; the byte-for-byte baseline diff above; the linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid IL2104/IL3053; and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 423 ms`, exit 0).

**And `GetPrinterColumnsAsync` was actually driven over HTTP**, which the unit tests do
not do: a scratch harness stood a `HttpListener` up as an API server and connected a
real `ClusterClient` to it through a real kubeconfig. It asked for exactly
`/apis/apiextensions.k8s.io/v1/customresourcedefinitions/certificates.cert-manager.io`
and parsed the response into `[Ready, Secret, Issuer, Age]`; a **404**, a **403** and a
**500 returning HTML** each came back with zero columns and no exception; and a
core-group descriptor (`Pod`) made **no request at all**. That is the degradation
contract this feature rests on, observed rather than reasoned about.

**Not verified, and the live cluster is the whole of it.** No sandbox came up —
`dockerd` starts here but every registry is blocked by this session's egress policy
(Docker Hub's blob CDN answers 403, `registry.k8s.io` answers 403 on the manifest HEAD),
so the acceptance criterion's own wording — *a CRD-heavy cluster shows the same columns
`kubectl get` does* — has been reached against the demo dataset and a stand-in server,
never against a real API server serving a real CRD. First things to do on a machine with
a sandbox, in order: `kubectl get widgets.shop.kubenimbus.io -n demo-shop` and
`kubectl get backups` beside the app's own lists and compare column for column,
including the `-o wide` columns against the advanced view; confirm the Backup list's
"Last run" cell ticks on its own (it rides the shared age timer, which no test drives);
confirm the factory Widget — which declares no columns — still lists exactly as it did
before; and check a cluster with a real cert-manager or Flux installed, where the
condition-filter paths meet objects this repo did not write. Nothing has been driven by
hand in the running app either: the two screenshots are the whole of the visual
evidence, and a CRD with enough priority-0 columns to need horizontal scroll has not
been looked at on screen.

**One thing deliberately left**: the generated `design/screenshots/*.png` were not
regenerated. They differ from a fresh render today, but they differ at the parent commit
too — the Age column is a function of the real clock while the rest of the fixture is
pinned to `FixtureNow`, so those files drift by themselves. Nothing this pass changes
appears in any of them (the baseline diff above says so), and regenerating them would
commit a date rather than a change.

**Multi-pod logs pass (FEAT-3):** one workload's pods tail into one pane, colour-keyed
by pod — see "Multi-pod logs (one workload, one stream)" above for the eight rules, the
per-pod tail decision and the reason the merge is two-stage rather than a true k-way
one. New: `LabelSelector.cs` in Core (+ 15 `LabelSelectorTests`), a `labelSelector`
parameter on `WatchResourceAsync`/`ListResourceOnceAsync` and an `extraQuery` on the
watch engine, `WorkloadLogsTabViewModel` / `LogSourceViewModel` / `LogSourcePalette` and
`WorkloadLogsView` in the App layer (+ 12 `WorkloadLogsTests`), a row context-menu entry
and a palette entry, three demo `payment-service-report-generator` replicas across two
ReplicaSets with interleaving canned streams, and two screenshot scenarios
(`cluster-tab-workload-logs{,-filtered-empty}`).

**A second bug was fixed here because the new pane runs through the same binding**, and
it is the one recorded under "Log severity is three classes, not a brush binding":
`LogSeverityToBrushConverter`'s `UnsetValue` default case falls back to
`TextElement.Foreground`'s own opaque-black default rather than to the inherited
foreground, so every log line with no severity keyword rendered **invisible on the dark
theme** — most real output. Shipping the multi-pod pane over that converter would have
reproduced it in the new surface on day one. The converter is deleted and severity is
three style classes.

**Verified this session** — and this record is the verifier's own re-run rather than a
claim carried over, because the implementing session's report was lost to a restart
before it could be recorded: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the
one warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **244/244 Core TUnit**
and **49/49 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so
the Core count is the unit-only subset — the cluster-gated tests return early); all
**55** scenarios × both themes rendered (110 PNGs), including the two new ones; the
linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 3718 ms`, exit 0). The acceptance criterion was
checked at two levels rather than asserted: `WorkloadLogsTests` drives the real
`Enqueue`/`Flush`/`OrderBatch` against a deliberately scrambled arrival order, and the
demo rollout scenario was read line by line off the rendered PNG in both themes — the
three replicas' lines interleave by their real RFC3339 instants rather than arriving
grouped by pod.

**Not verified, and the live half is all of it.** No cluster came up here (registry
egress is blocked in this container, as it has been in most sessions), so **not one byte
of this pane has crossed a real API server**: the `labelSelector` list+watch, the 50-pod
concurrency cap against a genuinely large ReplicaSet, a dropped pod-list watch
reconnecting, and above all an actual `kubectl rollout restart` watched through the pane
are argued from the wire format, pinned by unit tests and rendered from the demo
dataset — never observed. Nothing has been driven by hand in the running app either:
the pod chips, the follow toggle and the filter are verified by construction, by unit
test and in the headless harness, not by a mouse. And the pixel measurements quoted in
the severity section describe a *before* state whose code is now deleted, so they cannot
be re-measured from this tree; they are consistent with Avalonia's documented `UnsetValue`
semantics and with the fixed panes now rendering legibly in both themes, which is the
most that can be said from here.

**Node-operations pass (FEAT-4):** the node surface — detail plus cordon / uncordon /
drain. See "Node operations" above for the whole design, in particular the drain's
classification table and the partial-drain lifetime story, which is the constraint that
cannot be engineered away in a desktop client. New: `NodeActions.cs`, `NodeResources.cs`
and `ClusterClient.Nodes.cs` in Core (+ 32 tests across `NodeActionsTests` and
`NodeResourcesTests`), `NodeDetailTabViewModel` + `NodeDetailView` in the App layer, three
new `RowActionKind`s and the drain's options/plan/progress on the existing confirm strip,
three context-menu items and three palette entries, two app-local icons, three demo nodes
plus the five kube-system pods the drain's classification needs, and seven screenshot
scenarios (+ 14 `NodeActionTests` in `tests/KubeNimbus.App.Tests`).

**The demonstration is the deliverable, not the passing run**, same discipline as VER-5 and
VER-3. Five invariants were broken, run, and reverted:

- **Cordon patching `spec.schedulable` instead of `spec.unschedulable`** (the exact silent
  200) — **2 of 276 red**: `Expected to be equal to "{"spec":{"unschedulable":true}}"` and
  the same for the uncordon body.
- **Mirror pods no longer skipped, and `emptyDir` pods evicted without asking** — i.e.
  headlamp#7268 reproduced deliberately — **3 red**: `Expected to be equal to SkippedMirror`,
  `Expected to be equal to BlockedLocalData`, and the plan summary's `Expected to be 2`.
- **Init containers summed alongside the regular ones, and terminal pods counted** —
  **3 red**: `Expected to be within 0.0001 of 2` (the init-container floor),
  `…of 0.7` (the native sidecar), and `…of 0.5` (a node otherwise reading as full of
  finished Jobs).
- **The eviction body sent `apiVersion: v1`** — **2 red** on the byte-for-byte body.
- **`ArmRowAction` allowed a running action to be replaced** — **1 of 63 red** in the App
  suite: `Expected to be the same reference`, i.e. an eviction loop orphaned with nothing on
  screen reporting it.

**Two defects were found by looking at the rendered strip rather than by any test**, and
both are recorded in the section above: a compiled binding to a method group renders the
delegate's type name with no error anywhere, and the strip's target sentence read
"`Node/demo-worker-1 in `" for every cluster-scoped object (pre-existing — it applied to
deleting a PersistentVolume too). A third was caught the same way: "Stop draining" was drawn
*over* the still-visible Drain button, now one slot swapped on `IsPromptVisible`.

**The negative half of the demo-data change was measured, not argued.** Enlarging the shared
dataset is the one thing here that could silently rewrite a dozen committed images, so the
whole harness was rendered from a worktree at the parent commit and diffed byte for byte:
after scoping the three fixture list scenarios to the namespace they already claim to be
showing, **exactly two of the 110 pre-existing PNGs differ**, and both are intended — the
demo strip's notice now reads "Scale, restart, delete, cordon and drain …", cropped and
compared line by line. (`cluster-tab-workload-logs.dark` also flapped, and was confirmed to
flap between two renders of the *baseline* as well: its lines arrive on a timer.)

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **276/276 Core TUnit** and
**63/63 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the Core
count is the unit-only subset — the cluster-gated tests return early); the five break/revert
runs above; all **62** scenarios × both themes rendered (124 PNGs) plus the baseline diff;
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window
rendered at 1280x800 after 4535 ms`, exit 0).

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but the Docker Hub blob CDN answers 403 on the layer fetch
(`production.cloudfront.docker.com`), as in most sessions — so **not one byte of this has
crossed a real API server**: the cordon patch, the eviction POST, a real 429 from a
PodDisruptionBudget, a real 403 on `pods/eviction`, and the re-list loop actually watching a
node empty are all argued from the wire format, pinned by unit tests and rendered from the
demo dataset. Nothing has been driven by hand in the running app either — the pane, the
checkboxes and the Stop button are verified by construction, by unit test and in the
headless harness, not by a mouse. First things to do on a machine with a sandbox, in order:
cordon a node and confirm `kubectl get nodes` prints `SchedulingDisabled` and that nothing
schedules there; drain the k3s node's `demo-shop` workloads with `kubectl get pods -w`
beside it and confirm the pods roll rather than vanish at once; add a PodDisruptionBudget
that forbids the eviction and confirm the pane reads "blocked", stays honest and can be
stopped; confirm a static pod and the DaemonSet pods really are left behind; and check the
403 path with a `kubectl --as` impersonated user that cannot create `pods/eviction`. The
`design/screenshots/*.png` were deliberately **not** regenerated, for the reason the CRD
pass recorded: they drift by themselves because Age is a function of the real clock, so
regenerating them commits a date rather than a change.


**Apply-preview pass (FEAT-5):** the YAML editor's apply was blind — it sent the document
and reported what came back. It now asks the server what the apply would do and shows the
answer before anything changes. See "The apply preview (server-side dry run)" above for
the seven rules. New: `ResourceDiff.cs` and `PreviewApplyAsync` in Core (+ 21
`ResourceDiffTests` and 6 `ApplyPreviewHttpTests`), `ApplyPreviewViewModel` /
`DiffRowViewModel` and the panel under the editor (+ 9 `YamlEditorPreviewTests`), an
`AppSettings.PreviewApplies` preference with its own card on the preferences page, three
diff-row style classes, and two screenshot scenarios
(`cluster-tab-yaml-diff-{preview,no-change}`). No new gesture and no new always-visible
control, so `docs/keyboard-shortcuts.md` is unchanged.

**The request itself is observed rather than argued, which is new for this repo.**
`ApplyPreviewHttpTests` stands an `HttpListener` up as an API server and points a real
`ClusterClient` at it through a real kubeconfig, so `?fieldManager=kubenimbus&force=false&dryRun=All`,
the `application/apply-patch+yaml` content type, the 409, the 404-means-create and a 422
rejection are all things a test drove over HTTP. The pattern is worth reusing: several
items in `docs/BACKLOG.md`'s verification-debt section are "the wire format is argued,
never seen", and this closes that half of one of them without a cluster. What it still
cannot reach is the half that needs a real API server — defaulting, admission webhooks
and the server's own validation are precisely what the stand-in has no opinion about.

**Three breaks were written and confirmed red before the tests were called done**, same
discipline as VER-5 and VER-3. A preview that forgets `dryRun=All` — i.e. one that
silently *applies* what it claims to be previewing — turned 2 of 303 red on the query
string. Index pairing instead of the `name` merge key turned 4 red, including the
container-inserted-at-the-front case the rule exists for. A preview surviving the edit
that invalidated it turned 1 of 72 red. All three were reverted and the suites re-run.

**Two layout defects were found by looking at the rendered panel**, not by any test, and
both are in rule 7 above: an `Auto` row for the diff left the editor at zero height in
the default ~300px dock, and giving the editor a `MinHeight` instead overflowed the grid
so the header, the editor and the panel drew on top of each other. The star/`Auto`
row-height switch in `YamlEditorView.axaml.cs` is the fix.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **303/303
Core TUnit** and **72/72 App TUnit**, 0 failed, 0 skipped, both via `--project` (no
sandbox here, so the Core count is the unit-only subset — the cluster-gated tests return
early); the three break/revert runs above; all **64** scenarios × both themes rendered
(128 PNGs) plus a byte-for-byte baseline diff against the parent commit — of the 124
pre-existing PNGs exactly **two** differ, both `main-window-preferences.*` and both
intended (the new settings card), while `cluster-tab-workload-logs.dark` and
`cluster-tab-workload-logs-filtered-empty.light` were each confirmed to flap between two
renders of the *same* tree, which is ENG-10 and not this change; the linux-x64 NativeAOT
publish with no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test`
on that published binary under Xvfb (`SMOKE-OK main window rendered at 1280x800 after
448 ms`, exit 0).

**Not verified, and it is the half that needs a cluster.** No sandbox came up — `dockerd`
starts in this container but Docker Hub's blob CDN, ghcr.io and quay.io all answer 403, so
**no dry-run apply has crossed a real API server**. Everything specific to a live cluster
is therefore untouched by this evidence: that the API server accepts our apply body under
`dryRun=All` and returns the object it would store, that a defaulting or mutating webhook
shows up in the diff as this design claims (the single strongest argument for the feature,
and the one thing a stand-in cannot fake), that a real field-manager conflict raises during
the preview rather than only during the apply, and that the diff of a real Deployment is as
readable as the fixture's. Nothing has been driven by hand in the running app either: the
panel, its two buttons and the preference toggle are verified by construction, by unit test
and in the headless harness, not by a mouse. First things to do on a machine with a
sandbox, in order: edit a Deployment's image in the editor and confirm the preview names
that field and nothing else; add `resources: {}` to a container and see what the cluster
defaults into the diff; run `kubectl scale` on the same object from a terminal and then
apply from the editor, to reach the conflict path from the outside; and turn the preference
off and confirm Apply goes straight through as it did before.

**Manifest-diff pass (FEAT-58):** the apply preview's body is the manifest now, not a list
of field paths — the shape `kubectl diff`, `git diff` and VS Code's diff editor all show.
See "The apply preview (server-side dry run)" above for the five rules this added (8–12).
New: `TextDiff.cs` and `ResourceDiff.ToDiffableYaml` in Core (+ 18 `TextDiffTests`), a
`Live` object on `ApplyPreview` — which `PreviewApplyAsync` already read and discarded —
`DiffLineViewModel` / `DiffPairViewModel` and a `Diff / Split / Fields` strip on the panel's
existing chrome row (+ 7 more `YamlEditorPreviewTests`), six diff style classes in
`Styles/Theme.axaml`, and two screenshot scenarios (`cluster-tab-yaml-diff-split`, rendered
at the dock's default height on purpose, and `-fields`). No new gesture and no new
always-visible control, so `docs/keyboard-shortcuts.md` is unchanged.

**Three things were found by breaking them, and one by looking at the screenshot.** The
break/revert runs, same discipline as VER-5 and VER-3: replacing the LCS with index pairing
turned **1 of 321 red** — and the interesting part is that it turned *nothing* red until the
right test existed, because the prefix/suffix trim settles a lone insert, delete or replace
by itself (see rule 9); dropping the bookkeeping strip from `ToDiffableYaml` turned **1 of
321** and **1 of 79** red, in the Core and App suites respectively; and an off-by-one in the
collapse context turned **2 of 321** red. The screenshot found the fourth: an unchanged
document collapsed to one row reading `56 unchanged lines` underneath "this apply would
change nothing", and separately that an even split of the dock left the diff body at *zero*
height while the editor kept five lines nobody was reading — which is what the 3:1 row
weight in `YamlEditorView.axaml.cs` is for.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **321/321
Core TUnit** and **79/79 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox
here, so the Core count is the unit-only subset — the cluster-gated tests return early); the
four break/revert runs above; all **66** scenarios × both themes rendered (132 PNGs) plus a
byte-for-byte baseline diff against the parent commit — of the 128 pre-existing PNGs exactly
**four** differ, all of them the two `cluster-tab-yaml-diff-{preview,no-change}` pairs this
change is about, while `cluster-tab-workload-logs.{light,dark}` and
`cluster-tab-demo-pod-detail.light` were each confirmed to flap between two renders of the
*baseline itself*, which is ENG-10 and not this change; the linux-x64 NativeAOT publish with
no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test` on that published
binary under Xvfb (`SMOKE-OK main window rendered at 1280x800 after 1101 ms`, exit 0).

**Not verified.** The live half is unchanged from FEAT-5's and untouched by this pass — no
sandbox came up here, so no dry-run apply has crossed a real API server and the diff of a
*real* Deployment, with a real webhook's defaulting in it, has still only been argued. Two
things are this pass's own gaps rather than inherited ones: nothing has been driven by hand
in the running app, so the mode strip, the two-way `SelectedIndex` binding that carries the
mode across previews and the split view's horizontal behaviour are verified by unit test and
by rendering, not by a mouse; and the diff has only ever been rendered over fixture
documents of ~60 lines, so the collapse boundaries and the cell budget's fallback are pinned
by tests and have never been *seen* on a several-hundred-line CRD. The panel at the dock's
default ~300px shows about three lines of diff, which `cluster-tab-yaml-diff-split` records
honestly; whether that is enough, or whether the editor should give way entirely while a
preview is armed, is a judgement worth making in front of a real cluster.

**Pod-detail Overview pass (FEAT-43):** the pod's conditions, tolerations, node selector,
QoS class, priority class and each container's probes are structured sections now rather
than a trip to the YAML editor — see "Pod detail's Overview tab" above for the eight rules,
in particular the condition-polarity one, which is deliberately the *opposite* default from
the node surface's and has a third answer the node surface does not need. New:
`PodDetails.cs` in Core (+ 14 `PodDetailsTests`), an Overview tab at index 4 in
`PodDetailTabViewModel`/`PodDetailView` with `PodConditionViewModel` beside it (+ 4
`PodOverviewTests` in `tests/KubeNimbus.App.Tests`), conditions/tolerations/node
selector/QoS/priority/probes across the demo dataset's `payments` pods, the same states in
`scripts/manifests/10-shop.yaml` (which needed a `PriorityClass` — nothing in the sandbox
had one), and two screenshot scenarios. No new gesture and no new always-visible control,
so `docs/keyboard-shortcuts.md` is unchanged.

**Five breaks were written and confirmed red before the tests were called done**, same
discipline as VER-5 and VER-3. Claiming an unclassified condition type is positive — the
false-reassurance failure rule 4 exists to prevent — turned **1 of 335** red (`Expected to be
equal to Unclassified`). Dropping `op=Exists` from a toleration's rendering turned **2** red,
including the empty-key form that tolerates every node. Dropping the API server's own probe
timing defaults turned **1** red. In the App suite: stopping the probe section following the
container strip turned **1 of 83** red (`Expected to be empty`), and guarding the rebuild on
"have we rendered once" instead of on the fields' own text turned **3** red — the headline
one being `A_watch_tick_that_changes_a_condition_is_not_swallowed_by_the_rebuild_guard`,
which is the guard swallowing exactly the tick someone opened the tab for. All five were
reverted and both suites re-run.

**The negative half was measured, not argued.** Enlarging the shared demo dataset is the one
thing here that could silently rewrite committed images, so the whole harness was rendered
from a worktree at the parent commit and diffed byte for byte: of the 132 pre-existing PNGs,
**16 differ and every one of them is the new `Overview` chip in the tab strip** — the diff
bounding box on `main-window`, `main-window-about` and `cluster-tab-pod-detail-events` is a
~165×15 box at (513, 528), which is where that chip sits. `cluster-tab-workload-logs.{light,
dark}` were confirmed to flap between two renders of the *baseline itself* (ENG-10), and
`cluster-tab-demo-pod-detail` was confirmed to differ by one canned log line between a
full-harness run and a single-scenario run **on the baseline tree as well as on this one** —
its demo log replay is timer-driven, so it is the same nondeterminism and not this change.
No committed PNG under `design/screenshots/` was regenerated, for the reason the CRD and
node passes both recorded: Age is a function of the real clock, so those files drift by
themselves and regenerating them commits a date rather than a change.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **335/335 Core TUnit** and
**83/83 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the Core
count is the unit-only subset — the cluster-gated tests return early); the five break/revert
runs above; all **68** scenarios × both themes rendered (136 PNGs) plus the baseline diff;
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window
rendered at 1280x800 after 1078 ms`, exit 0).

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but Docker Hub's blob CDN still answers 403 on the layer fetch
(`production.cloudfront.docker.com`), as in most sessions — so **no pod this pane has
rendered has come from a real API server**. Everything specific to one is therefore
untouched by this evidence: that a real cluster's `status.conditions` carry the transition
times and messages this assumes, that the API server really does default all five probe
timings so the fallback is a fallback rather than the common case, that `status.qosClass` is
populated on every object worth reading, and above all that a *failing readiness probe* —
the acceptance criterion's real subject — reads usefully here against a pod that is actually
failing one. Nothing has been driven by hand in the running app either: the tab, its scroll
and the container strip switching the PROBES section are verified by unit test and in the
headless harness, not by a mouse. First things to do on a machine with a sandbox: open
`demo-shop/shop-web` and compare the tab against `kubectl describe pod` line by line; break
its readiness probe (point it at a path nginx does not serve) and watch `Ready` and
`ContainersReady` flip in the pane on the watch's own tick; cordon or taint the k3s node and
confirm the toleration list reads against it; and check a pod carrying a `DisruptionTarget`
condition (start a drain from the node pane and open a pod mid-eviction), which is the one
polarity in rule 4 that no fixture in this repo produces.

**Requests/limits pass (FEAT-44):** container requests and limits are visible text on pod
detail's Usage tab now, beside the current and peak readings and with the current usage as
a percentage of the limit — see "Requests and limits are text on the Usage tab" above for
the six rules, in particular the one that keeps the declared numbers out of the metrics
gate. This is a rendering change on numbers the app already parsed: `ReadContainerSpecs`
has been reading `spec.containers[].resources` since the metrics pass, and the only place
the result was rendered was a hover tooltip on the container chip. New:
`ContainerViewModel.CpuMeasuredText` / `MemoryMeasuredText` / `CpuResourceText` /
`MemoryResourceText`, `PodDetailTabViewModel.IsCollectingUsage` and its
`OnIsMetricsUnavailableChanged` partial, a restructured Usage tab (two `infoBar` notices
above the content rather than two panels instead of it), limits on the demo dataset's
report-generator `app` container, one new screenshot scenario
(`cluster-tab-pod-detail-usage-unset`), and 8 `ContainerResourceTextTests` in
`tests/KubeNimbus.App.Tests`. No new gesture, no new always-visible control, so
`docs/keyboard-shortcuts.md` is unchanged.

**Two breaks were written and confirmed red before the tests were called done**, the usual
discipline. Rendering a missing request or limit as a blank rather than in words — the UI
rule 9 failure this whole item is about in miniature — turned **2 of 91** red in the App
suite (`Expected "request 50m · no limit" but received "request 50m · limit "`). Gating the
declared line on `HasUsage`, i.e. making requests and limits follow the metrics gate after
all, turned **1 of 91** red. Both were reverted and the suite re-run.

**A third defect was found by looking at the rendered tab rather than by any test**: the
tab strip's window caption still read "collecting…" beside a notice saying this cluster
serves no metrics at all and never will. It is cleared from the changed-partial now.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **335/335
Core TUnit** and **91/91 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox
here, so the Core count is the unit-only subset — the cluster-gated tests return early);
the two break/revert runs above; all **69** scenarios × both themes rendered (138 PNGs);
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 925 ms`, exit 0).

**The demo-dataset edit was measured, not argued.** The harness was rendered from a
worktree at the parent commit and diffed byte for byte: of the 136 pre-existing PNGs,
**four** differ and all four are the two usage scenarios in both themes (the new text, plus
the maximized dock). Three more flagged and are the known timer-driven flap, not this
change: `cluster-tab-demo-pod-detail.light` and `cluster-tab-workload-logs.light` were each
confirmed to differ between two renders of the *baseline itself*, and the light log shot
landed byte-identical to the baseline on a re-run; `cluster-tab-workload-logs.dark` differs
on every run but only in the interleave and scroll position of its timer-driven canned
streams — same three pods, same "24 lines", same content. That is ENG-10. The generated
`design/screenshots/*.png` were deliberately not regenerated, for the reason the CRD, node
and Overview passes all recorded: Age is a function of the real clock, so those files drift
by themselves and regenerating them commits a date rather than a change.

**Not verified, and the live half is all of it.** No cluster came up here, so no pod whose
requests and limits this pane has rendered came from a real API server — that a real
cluster's usage against a real limit lands where this arithmetic says, that a LimitRange's
defaulted requests show up here as the object's own (they will: they are written into the
spec on admission, but that is reasoning, not an observation), and that the percentage is
worth reading on a container actually approaching its cap are all untested against a
server. Nothing has been driven by hand in the running app either: the tab, its scroll at
the dock's default height and the container strip are verified by unit test and in the
headless harness, not by a mouse — and the default-height case is the one worth looking at,
since the per-container section sits below two full-width charts and the screenshots are
maximized precisely because of that. First things to do on a machine with a sandbox: open
`demo-shop/shop-web` and compare the two containers against `kubectl describe pod` and
`kubectl top pod --containers`; delete metrics-server and confirm the requests/limits stay
readable under the notice; and check a BestEffort pod from `40-broken.yaml`, which is the
"no request or limit set" line against a real object.

**Visual-audit pass (cognitive load, 2026-08-19):** a review of all 69 screenshot
scenarios in both themes, read off the rendered pixels rather than the code, followed by
the uncontroversial half of its own findings. The report is
`docs/research/2026-08-19-visual-audit.md`; the three substantive changes have sections
of their own above ("The status dot…", "The meter track was invisible…", "Sidebar labels
come from the server's plural…"). Also shipped: the YAML editor's duplicate title, the
demo tab's status bar repeating the `demoBar` verbatim, the access review's `namespace
<ns>` caption and its borderless `Verify`, and the preferences kubeconfig box that
rendered as an 88px empty rectangle with its own explanation sitting outside it. Two
condition states that no object in the repo could produce — `DisruptionTarget` (the one
type with inverted polarity) and an unclassified custom readiness gate — were added to
the demo dataset and are rendered by `cluster-tab-pod-detail-overview-disrupted`; both
branches had been reachable only in code.

**Four of the twelve planned fixes were withdrawn during implementation, and that is the
part worth keeping.** Each was a finding read off a render that was wrong about its own
cause: the YAML title row does *not* cost a row (it also holds Apply/Delete, so the gain
is horizontal); `BY CONTAINER` cannot move inside a card because it heads a list of
cards; the cheat sheet's arrow was already `→` and was misread from a downscaled image;
and the CONDITIONS card's 16px indent has no clean fix, because the dot cannot hang into
the card's 12px padding without sitting on the border. All four are filed rather than
forgotten (`ENG-27`, `ENG-28`).

**One regression was written, rendered and reverted**, and it is the argument for the
byte-diff in a sentence. Shortening the shell's `Status` so the no-kubeconfig status bar
would stop repeating the empty-state card's heading looked like removing a duplicate; the
card binds its *heading* to that same property, so the change replaced the app's own
diagnosis with "Searched 1 location(s) — see above." pointing at nothing above it. It
built cleanly and both suites stayed green. What caught it was diffing the render and
asking why a file had changed that had no business changing (`ENG-29`).

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **335/335 Core TUnit** and
**97/97 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the
Core count is the unit-only subset); and **140** PNGs rendered (70 scenarios × both
themes) with every changed file accounted for — 2 new, 2 unchanged (the only two
scenarios with no cluster tab, which is also the proof the reverted regression is fully
out), 12 whose diff is confined to the sidebar, and 124 that also show a list. The
cleanest single check is `cluster-tab-exec-fullscreen-maximized`, whose entire diff is a
16×12px box at (120, 513) — exactly the `…Policys` → `…Policies` tail.

**Not verified**: no live cluster (registry egress blocked), so everything here was read
on demo data and fixtures — which for a pass that is entirely layout, colour and text is
the right medium, but it means the CRD-heavy real-cluster cases the report calls out
(KEDA's eleven printer columns, a sidebar of 70 real CRD plurals now going through the
re-casing path) have still never been rendered. Nothing was driven by hand in the running
app. `design/screenshots/*.png` were deliberately not regenerated: Age is computed from
the real clock, so they drift by themselves.

**Resizable and sortable grid pass (FEAT-66):** the resource list's columns can be
dragged to any width and ordered by a header click, and both choices are remembered per
kind — see "The resource grid is the reader's to re-cut" above for the twelve rules,
including the two Avalonia behaviours the design turns on (a template column raises no
`Sorting` event unless it carries `CanUserSort`, and a drag rewrites a star column's
*ratio* while leaving an Auto column's declared width alone). New: `ResourceGridSort.cs`
(`ResourceColumn` ids + `ResourceRowComparer`) and `GridLayoutStore.cs` in the App layer,
`WorkspaceSettings.GridLayouts`, sort state and a sorted mirror in `ClusterTabViewModel`,
a `Tag` on every grid column with the code-behind's four `Apply*Columns` passes moved off
header matching onto it, `ApplyColumnLayout`/`ApplySortIndicator` in `ClusterTabView`, 24
new tests across `ClusterTabSortTests` and `GridLayoutStoreTests`, and one screenshot
scenario (`cluster-tab-list-sorted`). No new gesture and no new always-visible control,
so `docs/keyboard-shortcuts.md` is unchanged.

**The two Avalonia behaviours above were measured on a real grid rather than reasoned
about**, and the first cut of this design was wrong about both. A headless probe drove
actual pointer events at a real `DataGrid`: with `CanUserSort` left at its default the
`Sorting` event never fired at all (so a header click did nothing, which is also what it
did in this app before this pass), and a resize drag on a grid whose only star column was
the one being dragged moved nothing — a star column can only take width from another star
column, which is why the app's own list resizes and a two-column test grid did not. The
same probe was then pointed at the **real** `ClusterTabView` inside a real `MainWindow`:
clicking the Name header cycled ascending → descending → off with `Rows` unmoved
throughout, dragging its edge took it from `2*` to `5.6*`, switching to ConfigMaps gave
that kind its own declared widths back, and switching back to Pods restored both the
width and the Status sort. A second process started against the same workspace came up
with the list already ordered by Status and the Name column at `5.6*` — the restart half
of "the choice survives", read out of the file by production code.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **335/335
Core TUnit** and **121/121 App TUnit**, 0 failed, 0 skipped, both via `--project` (no
sandbox here, so the Core count is the unit-only subset — the cluster-gated tests return
early); the two break/revert runs above (sorting `Rows` instead of the projection turned
**5 of 121** red, including the informer's own order after a delete; sorting only on the
click rather than maintaining it turned **2 of 121** red, on the created-while-sorted and
the modified-while-sorted cases; rebuilding on the metrics poll instead of re-ordering in
place turned **1 of 121** red, on the Reset the DataGrid would have answered by scrolling
to the top); all **71** scenarios × both themes rendered (142 PNGs)
plus a byte-for-byte baseline diff against the parent commit — of the 140 pre-existing
PNGs **none** differ for any reason this change is responsible for. Two classes of
pre-existing nondeterminism do show up and were each pinned on the baseline tree itself:
the timer-driven log panes (`cluster-tab-workload-logs.{light,dark}` and
`cluster-tab-demo-pod-detail.dark`), which flap between two full runs of the *baseline*
and land byte-identical between a second baseline run and a second run of this tree; and
`cluster-tab-crd-printer-columns{,-wide}`, whose only difference is a demo Certificate's
`type: date` cell reading `214d` where the earlier run read `213d` — that column is
computed from the real clock, which is the same drift that keeps `design/screenshots`
from being regenerated; the linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid IL2104/IL3053; and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 103 ms`, exit 0).

**Not verified.** No live cluster (no registry is reachable from this container), so
nothing here has ordered a list of real objects arriving from a real watch — which is the
one place rule 7 above earns its keep: a busy namespace producing Modified events several
times a second against a sorted list is a load and a jumpiness question that a fixture
cannot ask. Nothing has been driven by a real mouse either: the pointer events above are
synthetic (Avalonia headless), so the *feel* of the drag — the resize cursor appearing
over the separator, the hit zone being five pixels wide at 100% and at 150% scaling — is
unverified, and so is what a header click does on macOS with a trackpad. The persistence
is per kind and unbounded: a session that opens two hundred kinds and drags one column in
each writes two hundred entries into `workspace.json`, which nothing prunes. And the
committed `design/screenshots/*.png` were deliberately not regenerated, for the reason
every recent pass records: their Age column is a function of the real clock, so they drift
by themselves and regenerating them commits a date rather than a change.

**Argo CD pass:** GitOps in the navigator — an `Argo` sidebar section, a dashboard over
every Application on the cluster, an Application detail pane, and Sync / Refresh on the
shared confirm strip. See "Argo CD (GitOps in the navigator)" above for the nine rules,
in particular why a sync is a patch of the object's own top-level `operation` and why the
capability check names the kind. It is the feature Lens gated behind its paid tiers in
2026.8; here it is free, telemetry-free, and needs no Argo API server, no URL and no
second credential — Argo's objects are custom resources, so the whole integration is the
Kubernetes connection the app already has.

New: `ArgoCd.cs` and `ClusterClient.ArgoCd.cs` in Core (+ 29 `ArgoCdTests`),
`ArgoApplicationRowViewModel` / `ArgoApplicationTabViewModel` / `ArgoApplicationView` and
the dashboard in `ClusterTabView` (+ 16 `ArgoDashboardTests`), two `RowActionKind`s with a
prune option on the existing strip, two app-local icons, context-menu items on both the
dashboard and an ordinary Applications list, two palette entries, seven demo Applications,
a sandbox CRD *shape* plus five Applications, and three screenshot scenarios.

**Three breaks were written and confirmed red before the tests were called done**, the
usual discipline. Writing the sync into `spec` instead of the top-level `operation` — the
silent 200 that patches an Application and rolls nothing — turned **3 of 364** red. Making
sync outrank health in `AttentionReason` turned **1 of 364** red on the Synced-but-Degraded
case, which is the one the two-pill design exists for. Dropping the dashboard's attention
ordering turned **1 of 137** red in the App suite. All three were reverted and both suites
re-run.

**Two defects were found by looking at the rendered panes rather than by any test**, and
both generalize. Maximizing the inspector sets the list row's height to 0, and a `Grid`
does not clip its children — the resource list and the Helm browser get away with it only
because a `DataGrid` clips itself, so the dashboard's summary card went on painting
straight through the maximized dock; `ClipToBounds="True"` is the fix and the rule for
anything else that ever occupies that slot. And the detail pane's two-line resource rows
were separated from each other by less than their own two lines were separated internally
(19px against 16px), so each qualifier grouped with the row beneath it and the list read as
one run-on block — reported off the screenshot, fixed by the row's bottom margin, and true
of any multi-line row template.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **364/364 Core TUnit** and
**137/137 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the
Core count is the unit-only subset — the cluster-gated tests return early); the three
break/revert runs above; all **74** scenarios × both themes rendered (148 PNGs) plus a
byte-for-byte baseline diff against the parent commit; the linux-x64 NativeAOT publish
with no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test` on that
published binary under Xvfb.

**The baseline diff is worth reading before the next pass touches the sidebar.** Of the
142 pre-existing PNGs, 91 differ **only in a 3px-wide vertical strip at x=309** — the
sidebar's scrollbar thumb, which got shorter because the catalog gained a section. 25 more
differ across the 288px sidebar column, which is that section rendering. Two are
`cluster-tab-demo-scale-unavailable`, where `RowActionViewModel.DemoNotice` now names the
Argo actions too, and six are the node-drain trio, where that same third line of notice
text pushes the whole strip and the list below it down. The rest are the known
timer-driven log panes (ENG-10), confirmed to flap between two renders of the baseline
itself. Nothing else moved.

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but the registries are blocked, as in most sessions — so **no Argo
controller has ever seen a patch this feature produced**. The sync's `operation` field, the
refresh annotation, prune actually deleting something, and a 403 on `applications` are all
argued from Argo's documented controller behaviour, pinned byte-for-byte by unit tests, and
rendered from the demo dataset. The sandbox manifests are a *shape* — a CRD with Argo's
group, kind, version and printer columns, and five Applications in fixed states — so they
prove the patch lands on the object and nothing more: nothing reconciles them, a refresh
annotation stays where it is put, and a sync writes `operation` that no controller picks
up. That is `VER-15`. Nothing has been driven by hand in the running app either: the
dashboard, the context menus, the prune checkbox and the detail pane's chevrons are
verified by unit test and by rendering, not by a mouse. And the committed
`design/screenshots/*.png` were deliberately not regenerated, for the reason every recent
pass records — their Age column is a function of the real clock, so they drift by
themselves.

**UI-report pass (theme toggle, macOS menu, sidebar width, Advanced view):** four
complaints from someone running the app on a Mac, and the first one is a real defect
rather than a preference. See "The theme toggle wrote a string nothing could read",
"macOS has a real menu bar, and the app is called kubeNimbus", "The sidebar is 224px and
the reader can drag it" and the rewritten "The Advanced view" above for the rules; what
follows is the evidence.

**The theme bug was a stringly-typed mismatch, and it is not macOS-specific.** The
command bar's toggle persisted `"Dark"`/`"Light"` where `AppSettings` spells the setting
`"dark"`/`"light"`, so `Normalized()` rejected it and the app went straight back to
following the OS. On a dark-mode Mac that reads exactly as reported — light → dark
appears to work, dark → light does nothing — and on a light-mode machine it fails the
other way, which is presumably how it survived. The toggle now writes through
`App.ThemeToString` once instead of assigning the variant *and* persisting it, and
`Normalized()` canonicalizes case so a file written by the broken build recovers rather
than losing the choice.

**The Advanced view rework is mostly deletion.** `InspectorTabViewModelBase` lost its
`IsAdvancedView` mirror outright — with the content area ungated nothing read it — along
with the stamping line in `AddInspectorTab`, the `NotifyPropertyChangedFor` chain, the
`-o wide` argument to `PrinterColumns.Visible`, the `IsAdvancedView` arm of
`ClusterTabView`'s property-changed handler, and the gates in `PodDetailView`,
`WorkloadLogsView`, `YamlEditorView` and two palette blocks. What replaced them is one
classification (`SidebarGrouping.IsAdvancedSection`) and one pushed-down flag
(`SidebarSectionViewModel.IsHiddenByBasicView`, folded with the filter's own
`HasVisibleKinds` into `IsSectionVisible`). The old workspace value is deliberately not
migrated — see the section above for why carrying it forward would opt nearly everyone
into a shorter sidebar on the strength of a default they never chose.

**Five breaks were written, run and reverted**, the usual discipline. Making the theme
parse case-sensitively again turned **4 of 380** Core tests red on exactly the miscased
spellings. Stopping the sidebar filter from reaching into a hidden section turned
`A_filter_reaches_into_a_hidden_section` red; re-gating the usage columns on the switch
turned `The_switch_does_not_touch_the_list` red — the two assertions that exist to make a
re-gating visible. Dropping `SidebarWidthChanged` turned `A_width_change_reaches_the_shell`
red. All reverted and both suites re-run.

**The splitter was driven, not assumed, and that found the one real defect in this
pass.** A headless probe drove real pointer events at the real `ClusterTabView` inside a
real `MainWindow` — the technique FEAT-66 used on the grid's own column drags. It
reported the column resizing correctly (224 → 284, clamped at 520 and at 150, collapsing
to 0 when the sidebar is hidden and restoring afterwards) **and the width never reaching
the shell or `settings.json`**: the write-back from tab to shell was missing, so every
drag was silently lost on the next tab switch. That is invisible in a screenshot and in
every static test; `ClusterTabViewModel.SidebarWidthChanged` is the fix, pinned by
`SidebarWidthTests`. After it, the same probe reports `persisted settings.SidebarWidth =
284`. The probe was deleted afterwards rather than committed.

**One regression was caught by looking at the render.** Repointing `ApplySidebarChrome`
dropped the `ShowKindCount` assignment entirely, so the sidebar's count badges vanished
in every scenario — the build was clean and both suites stayed green. The badge belongs
on this switch (it is a question about the catalog, and the switch governs the catalog),
so it was restored rather than made unconditional. Fixing it exposed a second thing worth
keeping: the screenshot harness builds its sections by hand and so never ran the chrome
pass at all — it only ever looked right because the old default was *off*. `ApplySidebarChrome`
is `internal` now with `InternalsVisibleTo("Screenshot")`, and `BaseTab` calls it, which
is the same "drive the real entry point" rule the harness follows everywhere else.

**Two screenshot scenarios changed meaning and one is gone.** `cluster-tab-advanced-view`
is now `cluster-tab-basic-sidebar` and renders the switch **off**, which is the state
worth looking at; the pair with `cluster-tab-workloads-list` has to show as much sameness
in the content area as difference in the sidebar. `cluster-tab-crd-printer-columns-wide`
was deleted: with every declared column always drawn it rendered identically to
`cluster-tab-crd-printer-columns`.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**;
**380/380 Core TUnit** and **149/149 App TUnit**, 0 failed, 0 skipped, both via
`--project` (no sandbox here, so the Core count is the unit-only subset — the
cluster-gated tests return early); the five break/revert runs above; the splitter probe;
all **73** scenarios × both themes rendered (146 PNGs) with the sidebar, the CRD list and
the basic/advanced pair read off the images rather than asserted; `docs/keyboard-shortcuts.md`
regenerated through `KUBENIMBUS_UPDATE_DOCS=1`; the linux-x64 NativeAOT publish with no
new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test` on that published
binary under Xvfb (`SMOKE-OK main window rendered at 1280x800 after 4216 ms`, exit 0).

**Not verified, and the macOS half is the whole of it.** There is no Mac here, so the
menu bar has **never been seen**: that `Application.Name` really does title the app menu,
that AppKit appends Hide/Quit rather than duplicating what `MacMenu` adds, that the
Cluster/View/Help menus appear at all from an unbundled binary, and that the two
checkable items show their checkmarks are all argued from the platform's documented
behaviour and from Avalonia's `SetupApplicationName`, never observed. The theme fix is
equally untested *on a Mac*, though the mechanism is platform-independent and is pinned
by `AppSettingsTests`. Nothing has been driven by a real mouse either — the splitter
probe's pointer events are synthetic, so the resize cursor over the 8px handle, the hit
zone at 150% scaling, and how the drag feels on a trackpad are unverified. And no live
cluster came up (no registry is reachable from this container), so the narrower sidebar
has never been seen against a real 70-CRD catalog, which is the case it was cut for.
`design/screenshots/*.png` were deliberately not regenerated, for the reason every recent
pass records: their Age column is a function of the real clock, so they drift by
themselves.

**Strict field validation pass (FEAT-41):** the apply and its preview send
`fieldValidation=Strict`, so a misspelled or unknown field is refused in the API server's
own words instead of being pruned into a 200 — see the additions to "The apply preview
(server-side dry run)" above for the five rules, in particular why the strict rejection is
classified *before* the parameter is suspected and what the pre-1.27 fallback silently
costs. It is the gap that section previously named as somebody else's row, and it was the
one thing that could make a dry-run diff wrong in the same direction as no diff at all:
in `Warn` mode the typo is dropped before the dry run produces the object, so the preview
comes back clean about exactly the edit that is not going to happen.

New: `fieldValidation=Strict` on both halves of `SendApplyAsync` with a one-shot retry
without it, `ClusterClient.SupportsFieldValidation`, `ServerSideApplyValidationException`,
`ApplyPreview.StrictValidation`, `YamlEditorTabViewModel.ValidationDetails` and the
`infoBar` panel it drives, a footnote and an apply-status sentence for the degraded case,
six `ApplyPreviewHttpTests`, two `YamlEditorPreviewTests`, and one screenshot scenario
(`cluster-tab-yaml-validation-rejected`). No new gesture and no new always-visible
control, so `docs/keyboard-shortcuts.md` is unchanged.

**The request is observed rather than argued**, which is what `ApplyPreviewHttpTests`
exists for: a loopback `HttpListener` answers a real `ClusterClient` built from a real
kubeconfig, so the query string, the retry and the exact number of PATCHes are read off
the wire. The stub gained a queue per endpoint — the first PATCH refuses the parameter,
the retry succeeds — which is the only way to drive a fallback at all.

**Four breaks were written and confirmed red before the tests were called done.** Never
sending the parameter turned **4 of 386** red, all on the query string; note that it did
*not* turn the two rejection tests red, because those pin the classification rather than
the parameter, and that is the honest limit of a stand-in server. Suspecting the parameter
before ruling out a strict rejection — the fallback applying the typo it just caught —
turned **1 of 386** red (`Expected to be 1 but found 2`, i.e. a second PATCH went out).
Not remembering the fallback turned **3 of 386** red. Dropping the preview's
lost-strictness footnote turned **1 of 151** red in the App suite. All four were reverted
and both suites re-run.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **386/386
Core TUnit** and **151/151 App TUnit**, 0 failed, 0 skipped, both via `--project` (no
sandbox here, so the Core count is the unit-only subset — the cluster-gated tests return
early); the four break/revert runs above; all **74** scenarios × both themes rendered (148
PNGs) plus a byte-for-byte baseline diff against the parent commit — of the 146
pre-existing PNGs **none** differ for any reason this change is responsible for, and the
two that flagged (`cluster-tab-workload-logs.{light,dark}`) were confirmed to differ
between two renders of the *baseline itself*, which is ENG-10; the linux-x64 NativeAOT
publish with no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test`
on that published binary under Xvfb. The new panel was read off the rendered PNG in both
themes rather than asserted — the server's sentence wraps, nothing is clipped at the
dock's default height, and there is no button beside it.

**Not verified, and it is the half that needs a cluster.** No sandbox came up (no registry
is reachable from this container), so **no API server has ever answered one of these
requests**: that a real server accepts `fieldValidation=Strict` on an apply-patch at all,
that it refuses a misspelled field in one of the three wordings this classifies on rather
than a fourth, and — the acceptance criterion in its own words — that typing `contaienrs:`
into the editor against a live cluster produces the panel above are all argued from the
API's documented behaviour and observed only against a stand-in. The pre-1.27 fallback is
in the same position twice over: no server old enough to reject the parameter exists here,
and the *silent-ignore* case cannot be observed anywhere, because by construction it looks
exactly like success (see rule 4's honest half). Nothing has been driven by hand in the
running app either — the panel is verified by unit test and by rendering, not by a mouse.
First things to do on a machine with a sandbox: misspell a field on `demo-shop/shop-web`
and compare the refusal against `kubectl apply --server-side --validate=strict`; check a
CRD as well as a built-in, since apply's typed-patch conversion is what produces the
`field not declared in schema` wording; and confirm a *valid* apply is unaffected, which is
the regression this parameter could most plausibly cause. `design/screenshots/*.png` were
deliberately not regenerated, for the reason every recent pass records: their Age column is
a function of the real clock, so they drift by themselves.

**Remote-cluster responsiveness pass (2026-08-28):** two complaints from someone running
the app against a real, distant cluster — "you click Pods, you see an empty list, and only
several seconds later the data appears", and "the text in Details jumps around at random".
Both were real, both are invisible on a sandbox at localhost, and neither could have been
caught by a screenshot. See UI rule 18 and "An Auto DataGrid column ratchets, and only one
grid can afford it" above for the rules they produced.

**The empty list was the app answering before it had asked.** The informer writes its
`Reset` frame *before* it issues the list request, and `ClusterTabViewModel.Apply` ended
the loading state on whatever frame arrived first — so Reset cleared the flag, cleared the
rows, and `IsListEmpty` went true, rendering "No pods found" for the entire duration of a
round trip. `ResourceEventType.Synced` is the fix: written after the last page of the
initial list, it is the only frame that can honestly settle an empty namespace. Reset now
turns loading back *on*, the first row turns it off early (the list paginates), and both
`catch` arms clear it, because a watch that ended is not a watch that is still loading.
`WorkloadLogsTabViewModel.IsResolvingPods` had the identical bug and got the identical fix.
The waiting state itself now names the kind and the namespace over an indeterminate
`ProgressBar`, laid out in the same shape as the empty state below it.

**The jumping text was `Width="Auto"`, and the probe is the deliverable.** A headless probe
over the real `ClusterTabView` in a real `MainWindow` printed every column's `ActualWidth`
across three mutations. Making one row's namespace longer took Namespace **113 → 294px**
and moved seven other columns in the same pass (Name 240 → 136, Memory 138 → 106, Age
68 → 60); setting it back to `"x"` left the column at **294** — an Auto column sizes to the
widest cell it has ever realized and never shrinks. The third experiment caught the version
with no user action behind it at all: `RestartsText` gaining `"(43m ago)"` on the shared
wall-clock timer moved Restarts 93 → 108, Memory 138 → 131 and Age 68 → 60. After the
change the same probe reports every column identical across all three mutations. The probe
was deleted rather than committed.

**Three findings from looking at renders rather than at code.** Fixing Ready at 72px
clipped its own header to `Read` — a fixed width has to fit the header, which Auto did for
free. And fixing the **Helm and Argo** grids the same way measurably rendered them *worse*
(the Helm list clipped its `Rev` header, its status pill and the end of `Updated`), so both
keep `Auto` and the scope is stated: they are short one-shot lists over a small fixed
vocabulary, with no virtualized tail to ratchet through and no timer rewriting a cell.

**Three breaks were written and confirmed red before the tests were called done.** Ending
the loading state on Reset — the shipped bug — turned **3 of 157** App tests red. Not
emitting `Synced` turned the new Core integration test red against the live cluster, and
did so as a 60-second timeout rather than an assertion, which is the failure mode stated
honestly: without that frame the spinner never stops. And one *existing* test had encoded
the bug — `Empty_list_and_empty_filter_are_distinct_states` asserted that a bare Reset
settles the empty state, with a comment saying that is "how the settled-empty state is
actually reached". It goes through Synced now.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **387/387
Core TUnit** and **157/157 App TUnit**, 0 failed and **0 skipped** — the sandbox was up, so
the cluster-gated tests really ran against a live k3s API server, the new `Synced` frame
included; the probe runs above; all **74** scenarios × both themes rendered (148 PNGs) plus
a byte-for-byte baseline diff against the parent commit, which is worth recording because
two full baseline runs came out **byte-identical** (a *filtered* run does not — scenario
ordering shares scratch state, so only compare full runs). 112 of the 148 differ and every
one of them is the resource list's column widths; the Helm and Argo shots are byte-identical
to the baseline, which is what makes the scope statement above checkable. The **win-x64
NativeAOT publish** with no new warnings beyond the known DataGrid IL2104/IL3053, and
`--smoke-test` on that binary exiting 0. The eight committed `design/screenshots/*.png`
**were** regenerated this time, breaking with the recent convention deliberately: the
column layout is the literal subject of the hero image, so a stale one is worse than the
Age-column date drift those files carry.

**Not verified, and it is the case that produced the report.** There is no distant cluster
here — the sandbox is a container on localhost, where the whole gap this pass is about
plays out in a few milliseconds — so the *ordering* is proven (the Core test asserts
Reset → Added… → Synced against a real API server) while the *experience* is not: nobody
has watched the new "Loading Pods… in payments" panel sit there for the second or two it
was written for. Nor has any of this been driven by hand in the running app: the loading
panel, the ProgressBar and the drag behaviour of the now-fixed-width columns are verified
by unit test, by the layout probe and by rendering, not by a mouse. The first thing to do
against a real remote cluster is the reported gesture itself — click Pods on a namespace
with a few hundred objects and confirm the panel names the kind, stays until rows arrive,
and that nothing shifts sideways afterwards while the Age column ticks.

### Fewer clicks for the daily scenarios (2026-09-22)

A pass over the gestures the app is opened for — tail a pod's logs, shell into it,
restart or scale a workload, change namespace, come back tomorrow — counted in clicks
against k9s and Lens. Shipped:

- **Row keys** (k9s's): L, P, S, F, E, R, Delete and `/` on the resource list. Logs on a
  Deployment went from right-click → read menu → "Logs (all pods)" to select → L.
- **Namespace switching from the palette** ("Namespace: payments"), where it used to be
  only a dropdown that runs to hundreds of entries on a shared cluster.
- **Workspace restore keeps kind, namespace and the front tab**, first launch opens the
  kubeconfig's `current-context`, and a context's own `namespace` is honoured (the first
  half of FEAT-52).
- **Connect is faster on a distant cluster**: discovery's per-group requests are
  concurrent, discovery/namespaces/metrics run together, and restored tabs connect in
  parallel instead of one after another.
- **YamlDotNet pinned back to 16.3.0** — #77 had bumped it to 18.1.0 by accident, which
  makes every kubeconfig load throw `TypeLoadException` (the #15 failure again). 33 Core
  tests caught it; the launch check did not (VER-14).

Verified: both TUnit suites (Core 372 passed, 17 skipped for want of a sandbox; App 164
passed), `DiscoveryHttpTests` shown red with the concurrency bound set to 1, the
screenshot harness over every `cluster-tab` scenario, and the Debug app driven by hand on
the demo cluster on Windows — restore onto Deployments/payments, L on a Deployment
opening its multi-pod logs, S arming the scale strip, `/` + typing + Enter + E opening a
pod's YAML, and the workspace written back as `/Pod` + `payments` on close.

Not verified: any of it against a real API server (Docker was not running, so no
sandbox) — in particular that concurrent discovery and the parallel connect behave with
a real exec credential plugin, and the RBAC case where namespaces cannot be listed. No
NativeAOT publish was run; nothing here adds reflection, and `TabSnapshot`/
`WorkspaceSettings` stay on the source-generated JSON context.

[fluent-basics]: https://learn.microsoft.com/en-us/windows/apps/design/basics/

### Workload navigation and discovery follow-up (2026-09-22)

UX-1 through UX-4 are complete. Workloads open on live pods, conditions and events.
Their Actions menu uses the existing confirmation strip for scale and rollout restart.
Pod navigation and actions retain the source cluster. The namespace picker supports
search, persistent recents and Ctrl/Cmd+Shift+N.

Pods start before discovery finishes. Aggregated discovery negotiates v2 and v2beta1,
with the existing per-group walk as fallback. The disk cache stores descriptors only.
It expires after six hours or a server-version change. Explicit catalog refresh
bypasses it. Partial discovery results do not replace the disk cache.

Checks: solution build; 394 Core tests and 168 App tests; 156 screenshots across
both themes; keyboard interaction against 300 namespaces. The local k3s sandbox
was available for all Core integration tests. Windows NativeAOT publish and the
launch check passed. Only the known DataGrid IL2104/IL3053 warnings remained.

Limits: fixture screenshots cover the new workload pane. Remote exec authentication,
restricted RBAC and macOS keyboard behavior were not exercised against live systems.

### Node detail: system info, events and usage (2026-09-23)

Node detail gained three things. The System card (was Kubelet) lists platform, every
address, pod ranges, zone/region, instance type, provider ID and creation time, omitting
anything the node did not report. A new Events tab reads node events by kind and name,
because the kubelet stamps its node events with the node's name as UID. A new Usage tab
polls the node's own metrics, seeded from the list row's history, and shows each figure
as a share of allocatable.

Checks: solution build; Core tests 385 passed and 17 skipped (no sandbox cluster); App
tests 235 passed, 10 of them new in `NodeDetailTests`; every screenshot scenario renders,
including the new `cluster-tab-node-detail-events` and `cluster-tab-node-detail-usage`;
linux-x64 NativeAOT publish with only the known DataGrid warnings, and its
`--smoke-test` under Xvfb exited 0.

Not verified: any of it against a real API server — in particular the node-events field
selector and the single-node metrics GET on a live metrics-server. Windows NativeAOT
publish was not run.
