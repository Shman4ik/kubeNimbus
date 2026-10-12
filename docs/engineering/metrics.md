# Metrics (metrics.k8s.io)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ClusterClient.Metrics.cs` reads the aggregated metrics API for pod (per
container) and node usage. Three things are deliberate:

- **The API version comes from discovery**, not a hardcoded `v1beta1` — same
  rule as everywhere else: nothing about the server's API surface is assumed.
- **Absence is a first-class outcome.** No metrics-server (group missing) and a
  registered-but-dead metrics API (503/404) both raise
  `MetricsUnavailableException`; the UI hides the CPU/Memory columns instead of
  showing an error or a column full of dashes.
- **This is the one kind of thing the app polls** (15s) — from the list, and from pod
  detail's and node detail's Usage tabs, each on its own pane's token. The metrics API is a
  point-in-time aggregate over a ~30s window with no watch endpoint, so there is
  nothing to stream; polling is scoped to the current list's `CancellationToken`
  so it dies with the watch when the kind/namespace changes.
- **A row that arrives after a poll takes that poll's sample (ENG-55, #252).** The first poll
  and the initial list race: when the poll won, it was applied to no rows and kept nowhere,
  so every row read "—" until the next poll, up to an interval (15 s) later — measured in the
  0.5.1 pre-release pass. `ClusterTabViewModel` keeps the last poll's samples
  (`_lastUsage`, memory only), and a new row in `ApplyOne`/`ApplyFleetOne` takes its key's
  sample before it is added, so a CPU-sorted list places it correctly too. `StopWatch` drops
  them, so a sample never outlives the kind and namespaces it was read for, and a poll that
  finishes after its watch was cancelled is neither applied nor kept.
  `MetricsLastSampleTests` pins all three; the first goes red without the new-row step
  (checked).

- **The Nodes list's poll also feeds the capacity strip (#206)**: a successful poll marks usage
  readable, the "no metrics API" path says so, and a 403 on node metrics is reported to the strip
  as refused rather than swallowed as transient (the columns keep their dashes). See
  [node operations](node-operations.md#the-capacity-strip-above-the-nodes-list-feat-25-206).
  The refusal travels *with* the poll's result, never beside it: `ClusterClient.ReadNodeUsageAsync`
  classifies one read as `Read`, `Refused` (with the server's sentence) or `Unavailable`, and
  the poll hands the refusal to `ApplyUsage(byKey, token, refused)`. In 0.7.0 as first built the
  poll posted "refused" to the strip and then applied its own empty result as a successful
  read, which overwrote it, so under a 403 the strip said "no node has reported usage yet" with
  metrics-server's "answered with no node in it" as the reason — a verdict it did not have
  (UI rule 20; the 0.7.0 QA run's M-4). The metrics calls also lost the server's sentence,
  because `EnsureMetricsSuccess` used `EnsureSuccessStatusCode`; every non-503/404 failure is
  now a `KubernetesApiException`. `CapacityStripTests.A_refused_poll_says_usage_not_permitted_not_that_no_node_reported`
  goes red when `ApplyUsage` ignores the refusal, and `ClusterCapacityLiveTests` reads a real
  403 through `ReadNodeUsageAsync` as a narrow user and goes red without the server's sentence
  (both checked).

- **A 403 is an answer, and the poll stops asking (#310).** Narrow RBAC is the expected case.
  Until 0.7.x a 403 on pod usage was swallowed as transient, so the Pods list's CPU/Memory
  columns read "—" for ever while the poll asked again every interval, and on the Nodes list the
  columns kept their dashes beside the strip's "usage not permitted". Now a poll in which every
  cluster in scope refused (or has no metrics API) hides the columns the way a cluster without
  metrics-server does, hands the refusal to the strip, and returns. The refusal is remembered per
  kind (`_metricsRefused`) for the tab's connection, so going back to the list asks nothing; it is
  cleared on connect, the one moment the user's permissions can have changed. The reason goes in
  the status bar (`MetricsNotice`, "CPU and memory are hidden: this user may not read pod usage
  (…server's sentence…)."), except on the Nodes list, whose strip already says it (UI rule 20).
  A refused kind is per kind: a user may read node usage and not pod usage, or the reverse. In
  fleet mode a refusal hides the columns for that watch only, since it is about one member.
  `MetricsRefusedTests` pins the hide, the notice and the memo; `ClusterCapacityLiveTests` reads
  the real 403 on pod metrics as a narrow ServiceAccount and checks it is the
  `HttpRequestException` with `Forbidden` the poll catches. The poll loop itself (that it returns)
  has no test: it needs a live client on the UI thread.

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
  node detail's **Usage** tab (whole-node CPU and memory, each also as a share of
  allocatable — see [node operations](node-operations.md)), and pod detail's **Usage** tab (whole-pod CPU and memory charts plus a
  per-container pair). The tab is appended *after* Events so the existing
  `SelectedDetailTabIndex` values (Logs=0, Env=1, Events=2) stay stable.
- The Usage tab distinguishes its three states explicitly (UI rule 9):
  no metrics API on this cluster / samples not collected yet / charts. The
  first two look identical otherwise and lead to very different next steps.
