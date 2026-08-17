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

- **A real terminal in the exec pane.** Exec now runs a full VT emulator instead
  of stripping escape codes, so the tools people actually exec in for work:
  `vi`, `top`, `htop`, `mc`, `less` and anything else that paints a screen draw
  properly, in colour, with the cursor where the program put it. The pane
  scrolls back, text can be selected with the mouse, and it tells the container
  how wide it really is — so nothing wraps at 80 columns any more just because
  the dock is wider than that. `Ctrl`+`C`, `Ctrl`+`D`, `Tab`, the arrow keys and
  the function keys all reach the shell, which means the terminal — not
  kubeNimbus — owns `Ctrl` chords while it has focus; **Copy and Paste are
  `Ctrl`+`Shift`+`C` / `Ctrl`+`Shift`+`V`**, or right-click, as in any terminal
  emulator. The command input box below the terminal is gone: you type into the
  terminal itself. One known gap: highlighted text drawn with a terminal's
  "reverse video" (`top`'s column header, `less`'s prompt line) currently renders
  unhighlighted.
- **Open a terminal on this cluster.** From the ☰ menu or `Ctrl`/`Cmd`+`K`,
  kubeNimbus starts your own terminal — Windows Terminal or conhost, Terminal on
  macOS, whatever `xdg-terminal-exec`/`$TERMINAL` resolves to on Linux — with
  `KUBECONFIG` set and the current context already pointed at the cluster in the
  selected tab. Your kubeconfig is never modified and never copied: the context
  is pinned by merging a tiny generated file ahead of it, which holds a context
  name and nothing else, so `kubectl`, `helm`, `k9s`, `stern` and `kubectx` all
  agree about which cluster that window is on. Each cluster gets its own pinning
  file, so two terminals on two clusters cannot end up pointed at the same one.
  If `kubectl` is not found, the terminal still opens and the app says so — it
  also notes that an app usually sees a shorter `PATH` than your shell does, so
  the tool may well be there. If no terminal could be opened at all, the exact
  `KUBECONFIG` value is shown to copy. On the demo cluster it explains that
  there is no kubeconfig behind sample data rather than opening anything.
- **Workload actions — scale, rollout restart, and delete — from the resource
  list.** Right-click a row (or use `Ctrl`/`Cmd`+`K`) for **Scale…**, **Rollout
  restart…** and **Delete…**. Each one arms a confirm strip above the list that
  names the object before anything happens: Scale reads the workload's current
  replica count and takes a new one, Restart stamps the pod template the same
  way `kubectl rollout restart` does — so the controller rolls the pods under
  its own update strategy, honoring surge, `maxUnavailable` and
  PodDisruptionBudgets, rather than deleting them out from under it — and
  Delete asks first (unless you have turned "Confirm before deleting" off).
  Whether an object can be scaled or restarted comes from the cluster itself,
  so a custom resource with a `scale` subresource or an embedded pod template
  gets the same actions the built-in kinds do. Failures are shown in place with
  the API server's own message, which for the common one (RBAC) names the user,
  the verb and the resource. Until now the only way to change a replica count
  was to edit YAML by hand.
- **Demo cluster.** With no kubeconfig and no cluster, **Explore demo cluster**
  (in the empty state, in `Ctrl`/`Cmd`+`K`, and in the cluster switcher's own
  group) opens a full sample workload set that ships inside the binary — pods
  in every interesting state, live-looking log streams, env vars and secrets,
  events, usage graphs, Helm releases and a realistic CRD catalog. No cluster,
  no credentials, no network. It is labelled as sample data throughout (tab
  name, switcher group, and a banner above the content for the tab's whole
  life), and the panes that genuinely need an API server — exec, port-forward,
  YAML apply and delete — say so rather than pretending. The dataset is the
  same one the screenshot harness renders, so the two cannot drift apart.
- **Open kubeconfig file…** in the no-kubeconfig empty state. Until now the only
  routes to a cluster were `$KUBECONFIG` — which a GUI launched from Explorer, a
  shortcut or the Microsoft Store never inherits — and dropping a file at
  `~/.kube/config` by hand, so a first run on a clean machine had no next step
  that could be taken from inside the app. Only the **path** is remembered (in
  the workspace, across restarts); the file is re-read through the normal
  kubeconfig chain at load and at connect time, so nothing is copied into app
  storage. A picked file that has since moved is listed as `missing` in the
  empty state's search list rather than failing the load.
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
- **Search the resource list by name** (`Ctrl`/`Cmd`+`F`, or the box in the list
  header). Matches name, namespace and — across clusters — the cluster, with a
  running "12 of 87" beside it so a filtered list never looks like a small one.
  `Esc` clears it, `Enter` moves to the rows, and a search that matches nothing
  says so and offers the way back rather than showing an empty table. The
  sidebar's box filters resource *kinds*; this one filters the objects.

### Changed

- **One bar of chrome at the top of the window instead of two.** On Windows and
  macOS the command bar is now the title bar itself, so the row that held only a
  window title and three buttons is gone and the content starts ~36px higher —
  about two more log lines in the inspector dock, permanently. Dragging,
  double-click to maximize, the window menu and Windows 11 Snap Layouts all still
  work, from the empty space in the tab strip. Linux keeps its system window
  decorations, where client-side ones would look out of place on half the desktops
  we ship for.
- The kubeNimbus wordmark is no longer drawn in the top bar. The window title and
  the taskbar icon already carry it, and once the bar became the title bar it was
  printing the window's own title back at it.
- The cluster switcher and the top bar's cluster button are no longer disabled
  when no kubeconfig context exists — they always carry at least the demo
  cluster, and gating them on "has contexts" made them dead on precisely the
  machine where the demo cluster is the only cluster there is.
- The no-kubeconfig empty state now leads with the file picker and no longer
  tells you to run `scripts/sandbox-up` — an instruction nobody who installed a
  released build can follow. Setting up a throwaway local cluster is covered in
  [CONTRIBUTING.md](CONTRIBUTING.md) and the README, where a contributor is
  already looking.
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
- **ConfigMap-backed environment variables show their value straight away.**
  Only Secrets are hidden now, behind an eye toggle that can also hide the value
  again — a ConfigMap is ordinary configuration, and a click to read
  `LOG_LEVEL=info` bought nothing. Nothing is fetched for a Secret until you ask
  for it, so the value still never reaches the app unrequested.
- **The port-forward panel was rebuilt.** Six rows became two: the panel title
  the dock tab already carried is gone, fields read local → pod in the direction
  the traffic goes, the local port is an empty box marked `auto` instead of a
  `0` that silently meant the same thing, Start and Stop are one button rather
  than a pair with one half always dead, and a running forward shows its local
  URL — selectable, copyable, openable — instead of a sentence about it.
- **The list's name filter is now covered by automated tests.** The thing they
  guard is specific: while a filter is on, kubeNimbus keeps watching every
  object, not just the ones on screen, so an update to something the filter
  hides can never make it pop back into a filtered list — and clearing the
  filter always gives you back the current state of everything, including
  objects that appeared or changed while it was on.
- **Changing the shortcut modifier while the app is open is now covered by
  automated tests.** Preferences → Shortcut modifier already applied without a
  restart — the shortcuts, the `F1` cheat sheet and every tooltip switch between
  `Ctrl` and `Cmd` immediately, and the modifier you switched away from stops
  working — and that behaviour was checked by hand against the running app for
  the first time. The tests are there so the half that would fail silently
  cannot come back: a modifier you have turned off continuing to work looks
  exactly like the setting doing nothing.

### Fixed

- **Every downloadable binary is now launched before it is published.** The
  0.1.0 release shipped Linux and macOS builds that could not start at all —
  they exited with an error before drawing anything — because the release
  workflow compiled each one and never ran it. CI and the release workflow now
  start the binary they just built, on a runner of its own platform, and refuse
  to archive or publish it unless its main window actually appears. A build that
  cannot start fails loudly instead of reaching a download page.
- Table columns ran into each other. A right-aligned value sat flush against the
  next column's text — `48 MiB16d` — and, worse, the `—` shown for a pod with no
  usage reading landed against its age and read as a negative one (`—5d`). Cells
  now have a gutter on both sides, and the column widths were re-cut so the last
  column still fits in a narrow window.
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
