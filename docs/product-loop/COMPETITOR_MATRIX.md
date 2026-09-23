# Competitor matrix — jobs, not features

Cumulative. Rows are **jobs a user opens a Kubernetes client to do**; columns are the
products. Each cell says roughly **how many interactions** the job takes from an open
cluster tab and **what is paywalled**, with the evidence behind it in the linked
research. A feature list compares checkboxes; this compares how far each product is
from the job being done.

**How to read a cell.** An *interaction* is one click, one key chord, or one typed
command submitted (`:pods⏎` is one). Counts start with the cluster already connected
and the app already showing something, and they are for the common path, not the
fastest trick. "Not found" means the evidence searched did not show it — it is weaker
than "no", and the cell says which. Paywalls are stated per cell where they apply.

**Provenance.** Seeded on 2026-09-22 (train v0.4.0) from the August 2026 reports in
[`docs/research/`](../research/) — especially
[kubeui-positioning](../research/2026-08-17-kubeui-positioning.md),
[node-ops-and-overview](../research/2026-08-16-node-ops-and-overview.md),
[logs](../research/2026-08-17-logs.md),
[create-and-edit](../research/2026-08-17-create-and-edit.md),
[pod-workload-detail](../research/2026-08-18-pod-workload-detail.md),
[connecting-to-a-cluster](../research/2026-08-18-connecting-to-a-cluster.md) and
[networking](../research/2026-08-18-networking.md) — then updated with the delta in
[`history/v0.4.0/research.md`](history/v0.4.0/research.md). Each train's researcher
updates cells in place and adds a line to the change log at the bottom; a cell whose
evidence is older than the last survey that touched its row says so.

**kubeNimbus's column is taken from `CLAUDE.md`, `docs/engineering/` and `src/`**, not
from memory; where a Ready/Inbox row in `docs/BACKLOG.md` would change a cell, its ID is
named.

## The matrix

| Job | kubeNimbus | Lens (Mirantis) | FreeLens | Aptakube | Headlamp | k9s | KubeUI |
|---|---|---|---|---|---|---|---|
| **Find what is broken across the cluster** | **~4 per kind, no single view.** Pick the kind, All namespaces (Ctrl/Cmd+Shift+N), sort by Status; repeat for Deployments, Events, Nodes. The row search deliberately does not match status (UI rule 13). No faults toggle, no issues panel (`FEAT-24` inbox, P1) | **1.** The cluster overview is the default route; its *Cluster Issues* table merges node warning conditions and Warning events, and is the whole page when metrics are off ([source](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx)). Charts need Prometheus | **1**, inherited from Lens; same Prometheus gate, and the top-reacted issues in the repo are about it ([#466](https://github.com/freelensapp/freelens/issues/466), [#627](https://github.com/freelensapp/freelens/issues/627), [#1670](https://github.com/freelensapp/freelens/issues/1670)) | **1.** *Workloads Overview*: failing pods, deployments not running, warning events, recent restarts, high/undersized usage, PVC, Argo/Rollout/Certificate status; the landing screen is a preference. Being refined monthly (1.19.5, 1.19.6, 1.20.0) ([releases](https://github.com/aptakube/aptakube/releases)). Paid | **1 for the overview**, which since v0.44 polls rather than watches to stop tab OOMs ([releases](https://github.com/kubernetes-sigs/headlamp/releases)). A cluster-wide *diagnostics* page is open and assigned ([#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974)) | **3 per kind**: `:pods⏎`, `0` (all namespaces), `Ctrl-z` (toggle faults). Plus `:pulses`, `:popeye` ([README](https://github.com/derailed/k9s/blob/master/README.md)) | Not found |
| **Find why one pod is broken** | **1–3.** Double-click opens logs; Overview tab (conditions, probes, QoS), Events tab, `P` for previous logs. Status pills name CrashLoopBackOff/ImagePullBackOff/OOMKilled | **1–2.** Detail drawer with conditions, tolerations, events at the foot | Same as Lens | **1–2**; Events tab carries a warning-count badge since 1.19.5 | **1–2.** Plus a rule-based **Diagnostics** section on pod and workload pages since v0.43 — reasons, exit codes, previous-log availability, scheduling hints, "does not require … AI" ([PR #5487](https://github.com/kubernetes-sigs/headlamp/issues/5487)) | **2**: `d` describe, `l` logs, `p` previous | Not found beyond logs/YAML |
| **See a workload's rollout history, compare revisions, roll back** | **No feature.** ReplicaSets and ControllerRevisions are browsable kinds and owner navigation reaches them; comparing two is by eye. No rollback except hand-editing YAML | Not found (docs blocked here) | Not found; vague open ask [#418](https://github.com/freelensapp/freelens/issues/418) | **Partial**: ControllerRevision is viewable ([#285](https://github.com/aptakube/aptakube/issues/285)) and *Compare* diffs any two objects, spec-only since 1.20.0. Rollback not found | **~3**: rollback button opens the revision list for Deployment/DaemonSet/StatefulSet and rolls back to any revision (v0.41, from [#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222)); a community plugin ports it to Argo Rollouts ([plugin](https://github.com/portone-io/headlamp-argo-rollouts)) | **3**: `:rs⏎`, select, `Ctrl-l` rollback ([README](https://github.com/derailed/k9s/blob/master/README.md)); a first-class rollback ask was closed not planned ([#1077](https://github.com/derailed/k9s/issues/1077)) | Not found |
| **What changed recently** (events timeline, who edited a field) | **~3**: Events kind, All namespaces, sort by age. Warning events are coloured. No field-level "who changed it" | Events list; not found beyond it | Events list | Events list; warnings surface on the overview | Events list; overview events | `:events⏎` | Not found |
| **Read a workload's logs (all its pods)** | **2**: select the Deployment/StatefulSet/DaemonSet row, `L`. Tail fixed at 200 lines (`FEAT-31` ready); no JSON rendering (`FEAT-32` ready); no reconnect on drop (`FEAT-34` ready) | **Not shipped** — [#272](https://github.com/lensapp/lens/issues/272) open since 2020. Pod logs themselves are proprietary since 6.3 | **No** — [#687](https://github.com/freelensapp/freelens/issues/687) is among the top-reacted open issues | **2** — *Aggregated Log Viewer* is README bullet #2, incl. by Service; *Time since*; JSON; Clear logs (1.20.2) | **2** — deploy/rs/ds; severity filter; Prettify | **Not shipped** (declined twice) | Per pod only |
| **Shell into a container** | **2**: select pod, `S`. Real VT terminal | **2**; proprietary since 6.3, free for personal use only | **2** | **2** | **2**; ephemeral debug container with target selection (v0.45) | **2**: `s` | **2** |
| **Port-forward** | **3**, pod only (`FEAT-29` inbox for Service) | **2–3**, pod and service | **2–3** | **2–3** | **2–3** | **2**: `Shift-f` | **2–3**, pod and service |
| **Edit YAML and apply safely** | **~4**: `E`, edit, Apply shows the server-side dry-run diff, confirm. Strict field validation. No schema completion | **~3**, no preview found | **~3** | **~3**; "review exactly what you're sending" as a JSON Patch (1.20.2), alert if the object changes under the editor | **~4**, server-side dry run shipped; a strict-validation gap is open ([#7147](https://github.com/kubernetes-sigs/headlamp/issues/7147)) | Edit in `$EDITOR` | **~4**, dry run, **schema-aware completion** |
| **Scale / restart / delete — one or many** | **3** each, confirm strip; **no multi-select** (`FEAT-6` inbox) | **3**, no bulk ([lens#3771](https://github.com/lensapp/lens/issues/3771)) | **3**, no bulk | **3**; multi-select scale since 1.20.4 | **3**; bulk delete/restart ([#2156](https://github.com/kubernetes-sigs/headlamp/issues/2156)) | **2**; `Space` marks rows for bulk | **3** |
| **Node maintenance** (cordon, drain) | **3**: row action, confirm; drain is an in-app eviction loop with a plan table | **3**, shells out to `kubectl` | **3** via extension | **3**, shells out to `kubectl` | **3**, backend job; four correctness bugs in a year | **2**: `u`, `r` | **3** |
| **Cluster capacity and usage, no Prometheus** | **Partial**: node list CPU/Mem and node detail allocatable vs requested from `metrics.k8s.io`; no cluster totals (`FEAT-25` inbox) | **Prometheus-gated** | **Prometheus-gated** (top-reacted issues) | Yes, metrics-server or Prometheus | Yes; percentage bugs reported | `:pulses` | LiveCharts, source not rechecked |
| **Understand permissions** | **2–3**: access review from the palette; who-can scan | Not found | Not found | Not found | ServiceAccount page lists its bindings (v0.44) | Not checked | Not found |
| **GitOps state** | **1–2**: synthetic Argo row, sync/health pills, sync action. No Flux | **1–2**, Argo in the navigator since 2026.8 — **Premium (paid)** | Not found | **1–2**, GitOps menu for Flux **and** Argo (1.19.7) | Plugins | Plugins | Not found |
| **Helm releases** | **1–2**, read-only by decision (install/upgrade/rollback stay Helm's) | Yes | Yes | Yes | Read + rollback/upgrade; failed-release 404 bugs open ([#7523](https://github.com/kubernetes-sigs/headlamp/issues/7523)) | `:helm`, incl. rollback | Not found |
| **Browse CRDs with their own columns** | **1–2**, discovery-driven, printer columns honoured | Yes | Yes | Yes; first-class views for ESO, Gateway API | Yes | Yes | Yes, typed via runtime Roslyn models |
| **Find anything fast** (palette, search, keys) | Ctrl/Cmd+K palette, Ctrl/Cmd+P cluster switcher, `/` row search, k9s-style row letters | Hotbar, search | Hotbar, search | Command palette (1.20.2) and keyboard navigation (1.20.0) — **new this month** | Search | Native | Not found |
| **Several clusters at once** | Tabs + aggregated fleet lists | One cluster per view | One cluster per view | "As if it was one big cluster" — headline | Multi-cluster in core | One context at a time | Multi-cluster, multi-window docking |
| **Install and update** | MSI/MSIX/pkg/.desktop builds, **unsigned**, no auto-update, 4 RIDs | In-app update (not re-checked) | Flatpak with bundled cloud auth plugins; auto-update is an open ask ([#552](https://github.com/freelensapp/freelens/issues/552)) | In-app update notifications, snooze added in 1.20.3 | Desktop builds (Electron); signing not checked | Package managers | **Signed + notarized, Velopack auto-update, winget/Homebrew/Store**, Windows MSI (v1.1.0 betas) |
| **Cost, licence, telemetry** | **Free, MIT, no telemetry** | Free for personal use; commercial use and Premium (Argo CD, Prism AI, multi-cloud discovery) are paid | Free, MIT | Paid, closed | Free, Apache-2.0 | Free, Apache-2.0 | Free, MIT, **telemetry on by default** |
| **Stack / startup** | NativeAOT, ~156 ms to first window (measured) | Electron; markets "up to 20× faster on large clusters" (2026.8) — interaction latency, not startup | Electron; v2.0.0 stays Electron | Tauri | Electron desktop; −60 MiB RSS at start (v0.45) | TUI | ReadyToRun, ~645 ms (measured); still no `PublishAot` on 2026-09-22 |

## Change log

- **2026-09-22 (v0.4.0)** — seeded from `docs/research/` (August 2026) and updated with
  the delta since 2026-08-15: Aptakube 1.19.4–1.20.5, Headlamp v0.45.0, Lens 2026.8/2026.9,
  KubeUI v1.1.0–v1.2.0; FreeLens and k9s have not released since July and June. Two rows
  are new with this survey's deep theme: *rollout history / rollback* and *what changed
  recently*. Rows other than triage, change history, logs, GitOps, palette and install
  carry August evidence that was not re-checked.
