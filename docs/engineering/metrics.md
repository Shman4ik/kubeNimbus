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
