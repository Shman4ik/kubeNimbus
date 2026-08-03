# Changelog

All notable changes to kubeNimbus are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While kubeNimbus is pre-1.0, minor versions may contain breaking changes.

The release workflow reads the section matching a tag out of this file and uses
it as the GitHub Release body, so headings must match tags exactly
(`## [0.1.0] - …` ↔ `v0.1.0`).

## [Unreleased]

### Added

- **Cluster switcher** (`Ctrl`/`Cmd`+`P`, or the cluster button in the top bar):
  one fuzzy-searchable list over both open cluster tabs and unopened kubeconfig
  contexts, grouped Open / Pinned / Recent / All. Matches on context name,
  cluster name and kubeconfig path, so `ppr` finds `payments-prod` and an
  opaque EKS ARN is still reachable by the cluster behind it.
- **Pinned clusters.** The handful you actually work in sit at the top of the
  switcher, every session. Persisted in the workspace.
- **Environment colours.** Clusters are classified production / staging /
  development from their context and cluster names and coloured accordingly —
  a dot on the switcher button, a left edge on each cluster tab, a pill in the
  switcher, and a red band under the command bar while a production cluster is
  selected. Right-click a cluster tab to correct the guess; the assignment is
  remembered.
- **`Ctrl`/`Cmd`+`1`…`9`** jumps straight to a cluster tab (9 = last).

### Changed

- The top bar's context dropdown is gone. It could not search, truncated the
  long auto-generated names managed Kubernetes hands out, and only chose what
  the `+` button would open — switching to an already-open cluster was a
  separate gesture. The switcher does both jobs.
- Cluster tabs scroll instead of squeezing the rest of the command bar off the
  right edge, and show an active-tab highlight.
- The command palette no longer lists every kubeconfig context; it offers the
  switcher instead, so a large kubeconfig can't bury every other command.
- **Inspector panels give their content the room back.** The bottom dock spent
  up to four stacked rows of chrome before anything you opened it to read: pod
  detail had an owner row, a container row, a tab strip and a per-tab toolbar,
  plus a full-width filter box on Logs. Tabs are now a compact strip that shares
  its row with the selected tab's tools, the owner chips ride the container row,
  and the Helm and access-review panels dropped title rows that only repeated
  their own dock tab. Roughly 100px of a 300px dock handed back, in every panel.
- Pod detail's environment list puts each variable's name beside its value
  instead of above it, and its events feed puts the count and timestamp on the
  reason's line — twice as many rows visible in the same space. "Reveal" now
  sits next to the reference it reveals rather than at the pane's right edge.
- Long inspector tab titles (a generated pod name is ~50 characters) trim with
  a tooltip instead of pushing the dock's tab strip onto a second row.

### Fixed

- Clicking a cluster in the switcher only worked when the click landed on the
  cluster's name. A pointer handler sat on the row's content panel, which in
  Avalonia doesn't receive clicks where no child covers it — so most of each
  row, and the row's own padding, selected the cluster without opening it. Taps
  are now handled on the list itself and resolved to the row underneath.
- Cluster tabs and switcher rows had no pressed state and no hand cursor, so a
  click gave nothing back until whatever it did became visible — and switching
  to the cluster you were already on gave nothing back at all.
- A cluster tab's status dot showed the same grey for "connecting" and "not
  connected", so opening a cluster looked like it had failed until it finished.
  Connecting is now amber, and the dot carries the connection status as a
  tooltip.

## [0.1.0] - 2026-08-02

First public release. Everything below is new.

### Added

**Connecting**

- Kubeconfig context discovery across the whole `$KUBECONFIG` chain plus
  `~/.kube/config`, with an explicit empty state that names the paths it
  searched (including the ones that didn't exist) and offers a rescan.
- Exec-plugin authentication — `aws eks get-token`, `gke-gcloud-auth-plugin`,
  `azure kubelogin` — resolved through the kubeconfig at connect time, and
  applied to watch and log streams as well as ordinary requests.
- Multi-cluster context tabs with drag-reorder and workspace restore.

**Browsing**

- Discovery-driven sidebar covering built-in kinds *and* CRDs, grouped into
  Workloads / Network / Config / Storage / CRDs. Nothing is hardcoded: an
  unrecognised API group falls through to CRDs automatically.
- Sidebar filter matching display name, API group and `kubectl` short names
  (`svc`, `po`), collapsible sections, and a pinned session-scoped **Recent**
  section.
- Namespace-scoped or all-namespaces live lists for any kind, backed by
  informer-style list+watch with `continue`-token pagination, resourceVersion
  resume, relist on 410 Gone, and exponential-backoff reconnect with the
  connection state surfaced inline.
- Explicit loading, empty, disconnected and error states throughout — no view
  renders as a blank rectangle.
- Owner-reference navigation (pod → replicaset → deployment) as clickable
  chips; double-clicking an Event navigates to its involved object.

**Inspecting**

- Pod detail docked along the bottom, Lens-style, resizable and maximizable:
  container chips with status and restarts, and tabs for logs, environment,
  events and usage.
- Live log streaming with follow mode, container picker, previous-container
  fetch, in-buffer search, ERROR/WARN/INFO colouring, timestamp and wrap
  toggles, copy and download.
- Environment tab showing `env` and `envFrom`, with `secretKeyRef` /
  `configMapKeyRef` displayed as references until an explicit per-row reveal
  fetches and decodes the value.
- Events as a scannable card feed with type colouring and a jump to the
  involved object.
- YAML view and edit with syntax highlighting, server-side apply through a
  field manager, conflict detection with an offered force-apply, and a
  two-step delete. Secrets keep `data` base64 in the editor with a separate
  opt-in decoded panel.
- Interactive **exec** into a container and **port-forward**, both over
  websockets, with ANSI handling and capped scrollback in the terminal.

**Measuring**

- Live CPU and memory from `metrics.k8s.io` in the resource list and per
  container, with the API version read from discovery. A cluster without
  metrics-server degrades to no CPU/Memory columns rather than an error.
- Usage graphs over the session's rolling 30-minute window: a sparkline beside
  each list number, and pod-total plus per-container charts in a Usage tab.
  A missing reading is drawn as a gap, not as zero.

**Operating**

- Read-only Helm release browsing — values, rendered manifest, notes and
  revision history — read straight from release Secrets, with no Helm binary
  involved. The section appears only on clusters that actually store releases.
- RBAC access review in three directions: your effective permissions via the
  API server's own `SelfSubjectRulesReview`; where a ServiceAccount's access
  comes from, via binding provenance; and cluster-wide "who can do X?", a
  local RBAC scan labelled as provenance with a one-click
  `SubjectAccessReview` to confirm any row against the API server. Partial
  results always say they are partial.
- Multi-cluster aggregated ("All clusters") lists for any kind, with a Cluster
  column, per-cluster reconnect handling, and an honest "n of m clusters serve
  X" when a kind isn't served everywhere.

**Shell**

- Command palette (Ctrl/Cmd+K), platform-aware shortcuts, an F1 cheat sheet,
  and light/dark themes.

**Project**

- NativeAOT as the shipping configuration, verified in CI on every change.
- A one-command sandbox cluster (`scripts/sandbox-up`) — single-node k3s in
  Docker preloaded with workloads chosen to make every UI surface non-empty.
- A headless screenshot harness (`tools/Screenshot`) that renders real Views
  without a display, a cluster or a Windows box.

### Security

- No credentials are ever persisted. Kubeconfig is the single source of truth
  and is re-resolved at connect time; the workspace file stores context names
  only. See [SECURITY.md](SECURITY.md).
- No telemetry of any kind, and no network traffic beyond the Kubernetes API
  servers you connect to. This is a permanent non-goal.

### Known limitations

- Release binaries are **unsigned** — Windows shows a SmartScreen prompt and
  macOS quarantines the app.
- macOS and Linux builds are produced by CI but have had far less hands-on
  testing than Windows.
- No `linux-arm64` or `osx-x64`-specific testing beyond the build itself.
- Helm is read-only; install, upgrade and rollback stay Helm's job.
- Usage history is session-scoped and bounded at 30 minutes by design. Long-range
  metrics history is a non-goal — that's Prometheus's job.

[Unreleased]: https://github.com/Shman4ik/kubeNimbus/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.1.0
