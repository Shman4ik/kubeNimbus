# Train v0.4.0 — competitor delta and the "what is broken → why → what changed" survey

*2026-09-22. Track A of the v0.4.0 SURVEY phase. Two questions: what changed in the
Kubernetes desktop-client field since 2026-08-15 (the date of the oldest report in
`docs/research/`), and — the deep theme — how each client answers **"what is broken
across this cluster, why, and what changed"**: cluster-wide triage of unhealthy
workloads, and change history (rollout revisions, revision diffs, rollback, event
timelines).*

The short version:

- **The field moved fast in the last five weeks, and most of the movement is Aptakube.**
  Ten releases (1.19.4 → 1.20.5) including a command palette, keyboard navigation,
  multi-select scale, a Flux + Argo GitOps menu and continuous refinement of its
  *Workloads Overview*. Headlamp shipped v0.45; Lens shipped 2026.8 (Argo CD, "up to
  20× faster") and a 2026.9 build; KubeUI went 1.1.0 stable and then 1.2.0. **FreeLens
  and k9s have not released since July and June.**
- **Nothing found invalidates the Mission paragraph.** KubeUI still has no `PublishAot`
  (checked in its csproj today), FreeLens v2.0.0 stays Electron, Lens is still Electron.
  Two things sharpen it instead — see "Positioning" below: **Lens put Argo CD behind
  its paywall**, which kubeNimbus ships free, and **speed is now a marketed claim at
  three competitors**, none of whom publishes a startup number.
- **Deep theme, triage:** every competitor except KubeUI gets from "cluster open" to "a
  list of what is broken" in one to three interactions; kubeNimbus takes about four
  *per kind* and has no single place that answers it. `FEAT-24` (the cluster issues
  panel) is already P1 in the Inbox and its evidence got stronger this month. The
  cheapest step toward it is k9s's `Ctrl-z` — an unhealthy-only toggle on any list —
  which kubeNimbus does not have.
- **Deep theme, change history:** Headlamp (revision picker + rollback, v0.41) and k9s
  (`Ctrl-l` on ReplicaSets) ship rollback; Aptakube ships a two-object compare; kubeNimbus
  has none of the three. Demand is moderate and specific — the best statement of it is
  a Headlamp user: *"Rolling back to a previous release is a fairly important operation
  as it allows to remedy critical situations quickly."*

## What was searched, and what could not be reached

**Budget: 30 fetches (WebSearch + WebFetch), all 30 used.** Reachable: `github.com`
HTML, `raw.githubusercontent.com`, web search. **Egress-blocked**, as in every August
report: `lenshq.io` and `docs.k8slens.dev` (both tried; Lens claims below come from
search snippets of those pages and are labelled so), and by the same policy
`aptakube.com`, Reddit and Hacker News were not attempted.

Consequences for the reader:

- **No Reddit or Hacker News thread was read.** One search for recent r/kubernetes
  comparison threads returned only listicles and alternativeto.net pages; nothing
  usable was quoted from it.
- **Reaction counts were not visible** in the GitHub HTML WebFetch returns for
  individual issues. "Sorted by reactions" below means GitHub's own sort order, which
  is reliable for *ranking* but gives no absolute numbers.
- **Seabird was not re-verified.** The repository page returned no dates; the August
  finding (no commit since 2025-08-13) stands unrechecked.
- The re-survey of August material was deliberately avoided: rows of the matrix other
  than triage, change history, logs, GitOps, the palette and install carry August
  evidence.

## The delta since 2026-08-15

### Aptakube — ten releases, and it is closing the gaps kubeNimbus used to lead on

From [its releases page](https://github.com/aptakube/aptakube/releases):

| Release | Date | What matters for kubeNimbus |
|---|---|---|
| 1.19.4 | 2026-08-27 | Resource tabs (Overview/YAML/Events) are part of back/forward history |
| 1.19.5 | 2026-09-01 | Pod ↔ Service ↔ HTTPRoute links; **warning-count badge on the Events tab**; Workloads Overview UX |
| 1.19.6 | 2026-09-02 | Redirect to the list after delete; **Workload Overview layout for Recent Restarts and warnings** |
| 1.19.7 | 2026-09-07 | **GitOps menu for Flux *or* Argo CD**; "significantly reduced CPU and Network usage when the app is idle"; log viewer waits for container readiness |
| 1.20.0 | 2026-09-10 | **"Significantly improved support for Keyboard Navigation"**; multi-row selection; CSV export; Compare view can ignore metadata/status; Recent Restarts greyed for `Completed` |
| 1.20.1 | 2026-09-11 | Gateway API: GRPC/TLS/TCP routes |
| 1.20.2 | 2026-09-14 | **Command palette** for resource jumping; YAML editor warns when the object changes under it; "Review exactly what you're sending before applying" as a JSON Patch; **Clear Logs** |
| 1.20.3 | 2026-09-15 | First-class External Secrets Operator; update-notification snooze |
| 1.20.4 | 2026-09-16 | **Multi-select scale** for Deployments/StatefulSets; inline TLS certificate data on Secrets |
| 1.20.5 | 2026-09-18 | ESO generators/templates; Argo Application auto-sync/prune/self-heal fields |

**Read against kubeNimbus:** the command palette and keyboard-first navigation were
kubeNimbus's distinguishing interaction model (Ctrl/Cmd+K, k9s row letters); the
closest positioning competitor now markets both. The YAML "object changed under you"
warning kubeNimbus already has (`YamlEditorTabViewModel.StaleNotice`). Clear logs is
`FEAT-40` in the Inbox; multi-select is `FEAT-6`; TLS decode is `FEAT-30`.

**New issues filed since 2026-08-01** ([search](https://github.com/aptakube/aptakube/issues?q=is%3Aissue%20created%3A%3E2026-08-01)):
twelve, all small — keyboard shortcut for scaling (#579, shipped), Clear logs (#578,
shipped), command-palette improvements (#577, open), a setting to *not* auto-reconnect
to the last cluster on launch (#573, shipped), terminal font (#570),
PodCertificateRequest support (#568). Aptakube's tracker is a request-to-release
pipeline measured in days, which is itself the competitive fact.

### Headlamp — v0.45.0 (2026-08-20)

From [its releases page](https://github.com/kubernetes-sigs/headlamp/releases): desktop
startup RSS reduced by 60 MiB; **target-container selection for ephemeral debug
containers**; cordoned/drained node status indicators; **pod eviction alongside delete**;
Service details list matched pods; PVC and StatefulSet creation forms. v0.44.0
(2026-07-29, just before the window but not in any August report) is the more relevant
one for the deep theme: **"Cluster overview polling reduces browser OOMs on large
clusters"** and pod-list pagination with a 1,000-item budget.

Open and new: Helm operations 404 on failed releases
([#7523](https://github.com/kubernetes-sigs/headlamp/issues/7523),
[#7660](https://github.com/kubernetes-sigs/headlamp/issues/7660)), and a proposal to
check authorization before Helm rollback/uninstall
([#7608](https://github.com/kubernetes-sigs/headlamp/issues/7608)).

### Lens — 2026.8 (2026-08-19) and 2026.9.20601 (2026-09-02)

The release post itself is egress-blocked; from search snippets of
[the 2026.8 post](https://lenshq.io/blog/lens-release-august26/) and the
[forum release thread](https://forums.k8slens.dev/t/lens-2026-8-190756-latest-release/7171):
"up to **20× faster on large clusters** … switching tabs, even on a cluster with
thousands of pods, is now instant … cut background processing time from over 7 seconds
to about a quarter of a second", and **Argo CD in the cluster navigator** — dashboards,
list and detail views, one-click Sync and Refresh, and "Ask AI" summaries of Argo
objects. Per search snippets of Lens's
[premium-features page](https://docs.lenshq.io/k8slens/premium-features/) and
[blog](https://lenshq.io/blog/lens-premium-features), **"Argo CD support is a Premium
Feature, included with Plus, Pro, and Enterprise plans"**, as are Prism AI
troubleshooting and multi-cloud discovery. The 2026.9 build is known only from
[Chocolatey](https://community.chocolatey.org/packages/lens); its contents were not
found. The [k8slens.dev](https://k8slens.dev/) title now reads *"From Kubernetes
control to governed AI agents"* — the landing page leads with AI, not with the IDE.

### KubeUI — v1.1.0 (2026-09-12), v1.1.1, v1.2.0 (2026-09-17)

From [its releases page](https://github.com/IvanJosipovic/KubeUI/releases): 1.1.0 is the
stable cut of the August beta series — "Pod log enhancements and AI features"; the
betas added a **Windows MSI installer** and moved to the .NET 11 SDK. 1.2.0 adds a
Secret/ConfigMap editor. **Still no `PublishAot`, `PublishTrimmed` or `TrimMode`** in
[`KubeUI.Desktop.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj)
on 2026-09-22 — `PublishSingleFile` + `PublishSelfContained` + `PublishReadyToRun`,
unchanged. Nothing in the window touches the triage or change-history themes.

### FreeLens — no release since v1.10.3 (2026-07-07)

[Releases](https://github.com/freelensapp/freelens/releases). The top of the tracker
by reactions ([sorted](https://github.com/freelensapp/freelens/issues?q=is%3Aissue%20is%3Aopen%20sort%3Areactions-%2B1-desc))
is: the v2.0.0 tracking issue, the dependency dashboard, newer-API fields, then **three
metrics issues** — [#466](https://github.com/freelensapp/freelens/issues/466),
[#627](https://github.com/freelensapp/freelens/issues/627), and a newer duplicate
[#1670](https://github.com/freelensapp/freelens/issues/1670) (Feb 2026, "Implement
Kubernetes Metrics Server in addition to Prometheus") — and then workload logs
([#687](https://github.com/freelensapp/freelens/issues/687)). The metrics-server wedge
the August report found is intact and has a third issue behind it.

- [**v2.0.0**](https://github.com/freelensapp/freelens/issues/2279) is an ESM-first
  Electron rebuild on React 19 with Extensions API v2 — **still Electron**; a "explore
  Tauri" issue ([#2056](https://github.com/freelensapp/freelens/issues/2056)) is closed.
- [**#2376**](https://github.com/freelensapp/freelens/issues/2376) (2026-07-29, open, no
  maintainer reply visible): *"After upgrading freelens this morning to 1.10.3, my mac
  was classified as high risk because of a possible reverse shell incident."* It is
  **unresolved and may be a false positive**; it is recorded because an unanswered
  security report on the most-starred open-source client is part of the market's
  state, not as a claim that anything happened.

### k9s — no release since v0.51.0 (2026-06-06)

[Releases](https://github.com/derailed/k9s/releases). Nothing new in the window.

### Newly relevant: kdashboard

[folio-pro/kdashboard](https://github.com/folio-pro/kdashboard) — Electron + Svelte,
**Functional Source License** (converts to Apache-2.0 after two years), **11 stars**.
Negligible as a competitor. Worth one paragraph only because its README is the whole
deep theme written as marketing: a *Problems* view that "correlates workload status,
events, and pod conditions into diagnoses with suggested next steps", a cluster
overview of "nodes, pods, problems, and warnings", **"Deployment revision history with
side-by-side pod template diffs and rollback"**, and an issue for per-resource
"who changed what, when" from `managedFields`
([#116](https://github.com/folio-pro/kdashboard/issues/116)). Marketing emphasis from a
new entrant, not demand.

### Context, secondary source only

A [2026 comparison post](https://srexpert.cloud/blog/best-kubernetes-gui-tools-compared-2026)
states that the upstream Kubernetes Dashboard was archived in January 2026 and that
SIG UI recommends Headlamp as its successor. Not verified at the source; it does not
touch the Mission paragraph (which does not mention the Dashboard).

## Positioning — does anything change the Mission paragraph?

Checked clause by clause against today's evidence. **No clause is falsified.**

- *"Lens … a heavy Electron app"* — still Electron. But Lens now **markets speed**
  ("20× faster"), as do Aptakube (idle CPU/network, 1.19.7) and Headlamp (−60 MiB RSS,
  v0.45). All three are about runtime cost on big clusters, not startup; **none
  publishes a startup number**, so kubeNimbus's measured ~156 ms claim is still
  uncontested, but "fast" alone is no longer a distinguishing word in a headline.
- *"FreeLens (the surviving fork) is still Electron"* — true, and v2.0.0 keeps it so.
- *"KubeUI is not NativeAOT and cannot cheaply become so"* — still true on 2026-09-22.
- **New, and worth a line in `DIST-4`/`DIST-7`:** Lens's Argo CD navigator is
  **Premium**; kubeNimbus's Argo CD surface is free and MIT. Aptakube has Argo *and*
  Flux (paid). That makes GitOps visibility a clean "free vs paid" line against Lens.
- **Marketing pressure, reported and not proposed:** AI is now what three products
  lead with — Lens (Prism, MCP server, "governed AI agents"), KubeUI (1.1.0 AI
  features, MCP/ACP), kdashboard (Claude Code / Codex MCP endpoint). Headlamp went the
  other way and describes its diagnostics as **not** needing AI. kubeNimbus has no AI
  surface; any would sit against the no-telemetry, no-network-beyond-your-clusters
  stance in `SECURITY.md`. That is a human decision and no row is proposed.

## Deep theme: what is broken → why → what changed

### 1. "What is broken across this cluster" — triage

| Product | Path to a list of what is broken | Interactions | Notes |
|---|---|---|---|
| **Lens** | Cluster overview is the default route; *Cluster Issues* merges node warning conditions + Warning events ([source](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx)) | 1 | Charts Prometheus-gated; issues table is the fallback |
| **FreeLens** | Inherited | 1 | Same Prometheus gate; three top-reacted issues about it |
| **Aptakube** | *Workloads Overview*: "which pods are failing, which deployments are not running … undersized containers, warning events, recent restarts" plus PVC/Argo/Rollout/Certificate status ([search summary of aptakube.com](https://aptakube.com/), [1.19.6 notes](https://newreleases.io/project/github/aptakube/aptakube/release/1.19.6)) | 1 (configurable landing) | Being refined every few weeks: 1.19.5, 1.19.6, 1.20.0 — evidence of use |
| **Headlamp** | Cluster overview (now polled, v0.44). A cluster-wide *diagnostics* page is **open and assigned**: [#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974), v1 scoped to pods + workloads in the selected namespaces, reusing `getPodDiagnostics`/`getWorkloadDiagnostics` | 1 overview; diagnostics page not yet shipped | #6974's framing: *"There is no way to see what is unhealthy across a cluster without opening objects one at a time."* It also says the page "needs several lists watched at once, which is the expensive part" |
| **k9s** | `:pods⏎`, `0`, **`Ctrl-z` "Toggle faults/error display"** ([README](https://github.com/derailed/k9s/blob/master/README.md)); `:pulses`; `:popeye` | 3 per kind | The faults toggle works on any list, so it is a predicate, not a page |
| **KubeUI** | Not found | — | |
| **kdashboard** | Problems view with diagnoses and suggested next steps (README) | 1 | Marketing only; 11 stars |
| **kubeNimbus** | Kind → All namespaces → sort Status, per kind; the row search deliberately does not match status (UI rule 13) | ~4 per kind, 12+ for pods/workloads/events/nodes | `FEAT-24` (issues panel, P1), `FEAT-25` (capacity), `FEAT-26` (landing preference) all in the Inbox, unbuilt |

**Two engineering signals from the field, both about cost:** Headlamp moved its overview
from watches to **polling** in v0.44 to stop browser OOMs, and its #6974 proposal names
the multi-list watch as "the expensive part". kubeNimbus's hard rule 2 (list+watch
everywhere) and the August report's warning about a cluster-wide watch × every fleet
member both point the same way: `FEAT-24`'s refresh model must be decided in its spec,
not defaulted.

**Demand strength:** strong and convergent — every GUI incumbent ships a triage surface,
Aptakube's is under active refinement, Headlamp's is being built from a user-shaped
issue. This is not a new finding (it is `FEAT-24`'s August evidence), but the gap
between kubeNimbus and the field widened this month rather than narrowed.

### 2. "Why is this one broken" — per-object diagnosis

Headlamp merged a **deterministic, rule-based Diagnostics section** on pod and workload
pages in v0.43 ([PR #5487](https://github.com/kubernetes-sigs/headlamp/issues/5487),
merged 2026-05-14): *"phase/status reason, failed conditions, waiting or terminated
containers, restart counts, exit codes, previous-log availability, warning events, and
Pending scheduling hints"*, and for workloads *"aggregating unhealthy owned pods and
dominant failure reasons"* — explicitly *"does not require HolmesGPT, AI plugins,
providers, or in-cluster agents."* Aptakube added a warning-count badge to the Events
tab (1.19.5) so the reason is visible before the tab is opened.

**Demand strength: weak.** The August
[pod-workload-detail report](../../../research/2026-08-18-pod-workload-detail.md) found
"a clean negative" for a bespoke troubleshooting feature — users ask for `describe` +
events, which kubeNimbus has (Overview tab, Events tab, status pills naming the reason,
`P` for previous logs). Headlamp's diagnostics is marketing emphasis from a CNCF
project, not an answer to an upvoted request. It is the cheap, AI-free counter to the
AI pitch above, which is its one strategic interest.

### 3. "What changed" — rollout history, revision diffs, rollback

| Product | Revision list | Diff between revisions | Rollback |
|---|---|---|---|
| **Headlamp** | **Yes** — "the rollback button opens the revision list" for Deployment/DaemonSet/StatefulSet (v0.41, from [#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222); release detail via [search summary](https://computingforgeeks.com/headlamp-kubernetes-dashboard/)) | Not found | **Yes, to any revision.** Still being hardened: null-reference crashes in rollback ([#6337](https://github.com/kubernetes-sigs/headlamp/issues/6337), open), missing DS/STS rollback tests ([#6590](https://github.com/kubernetes-sigs/headlamp/issues/6590)). A community plugin ports it to Argo Rollouts ([portone-io/headlamp-argo-rollouts](https://github.com/portone-io/headlamp-argo-rollouts)) |
| **k9s** | ReplicaSets view | No | **`Ctrl-l` on a ReplicaSet** ([README](https://github.com/derailed/k9s/blob/master/README.md)); a request for richer rollback was closed not planned ([#1077](https://github.com/derailed/k9s/issues/1077)) |
| **Aptakube** | ControllerRevision is a viewable kind ([#285](https://github.com/aptakube/aptakube/issues/285), closed) | **Compare** any two objects; since 1.20.0 can ignore metadata/status | Not found |
| **Lens / FreeLens** | Not found (Lens docs blocked) | Not found | Not found; FreeLens [#418](https://github.com/freelensapp/freelens/issues/418) "Rollout support" is open and too vague to count |
| **KubeUI** | Not found | Not found | Not found |
| **kdashboard** | Yes (README) | **"side-by-side pod template diffs"** | Yes (README) |
| **kubeNimbus** | No — ReplicaSets/ControllerRevisions are browsable kinds, reachable by owner navigation | No — but `TextDiff` (Core) already renders a line diff for the apply preview | No — only by hand-editing the Deployment's YAML |

**Demand strength: moderate.** The clearest user statement is #3222's *"Rolling back to a
previous release is a fairly important operation as it allows to remedy critical
situations quickly … Maybe also see the history of rollouts to select"*, which a CNCF
project then shipped; a third party wrote a plugin to get the same for Argo Rollouts;
k9s ships a key for it. No upvoted request was found for *diffing* revisions — that is
marketing (kdashboard) plus the adjacent Aptakube Compare view.

**Field-level "who changed this":** [kubectl-blame](https://github.com/knight42/kubectl-blame)
— *"Annotate each line in the given resource's YAML with information from the
managedFields to show who last modified the field"* — has 155 stars and a krew entry.
That is the only demand signal found, and it is weak. kubeNimbus's YAML editor strips
`managedFields` today (`YamlEditorTabViewModel`), so the data is read and discarded.

**Event timelines ("what changed in the last hour"):** every product has an events
list; none was found with a time-windowed cross-kind timeline beyond sorting by age.
No evidence of demand for one was found.

## Evidence refreshes for existing backlog rows

Not new candidates — the rows exist; the evidence behind them moved.

- **`FEAT-24` (cluster issues, P1)** — Aptakube's overview refinements (1.19.5, 1.19.6,
  1.20.0), Headlamp #6974 now assigned, Headlamp v0.44's switch to polling.
- **`FEAT-25` (capacity overview)** — FreeLens [#1670](https://github.com/freelensapp/freelens/issues/1670)
  is a third metrics-server issue near the top of that tracker.
- **`FEAT-40` (clear the log pane)** — Aptakube shipped it from
  [#578](https://github.com/aptakube/aptakube/issues/578) in 1.20.2.
- **`FEAT-6` (multi-select)** — Aptakube shipped multi-select scale (1.20.4) and
  multi-row selection (1.20.0).
- **`FEAT-30` (TLS Secret decode)** — Aptakube now shows TLS/certificate data inline
  (1.20.4), joining KubeUI.
- **`DIST-4` / `DIST-7` (comparison page)** — add "Argo CD free; Premium in Lens".

## Candidates

Ranked by strength of evidence, cheapest-first within a tier. "Job" is the matrix row the
item shortens. None of these is built; partial overlaps are named.

| User outcome | Evidence (demand or marketing) | Size | Does kubeNimbus have it? | Notes |
|---|---|---|---|---|
| **1. Show only what is unhealthy, on any list** — one toggle (k9s's `Ctrl-z`) that narrows the current list to rows whose health is not ok, across All namespaces and in fleet mode; Warning events are included because they already classify as `warn`. *Job: find what is broken — from ~4 interactions and a scan per kind to 3 and a list that is only problems* | **Marketing/table stakes** in the tool people fall back to: k9s `Ctrl-z` "Toggle faults/error display" ([README](https://github.com/derailed/k9s/blob/master/README.md)). **Demand** for the outcome: [headlamp#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974) — *"no way to see what is unhealthy across a cluster without opening objects one at a time"* | S | **No.** `ResourceRowViewModel.StatusHealth` already carries the verdict; `RowFilter` deliberately does not match status text | Does **not** conflict with UI rule 13 — the rule forbids matching the *text* "Running", this is a predicate on the computed health. Must apply to `VisibleRows` only, never `Rows` (rule 13's pinned invariant, `ClusterTabRowFilterTests`). Needs its own empty state ("Nothing unhealthy among 214 pods"), distinct from `IsListEmpty` and `IsFilterEmpty`. Pairs with, and is a cheap first slice of, `FEAT-24` |
| **2. A cluster issues panel** — Warning events + node warning conditions + workloads not healthy, one ranked list, each row opening the object. *Job: find what is broken — to 1 interaction* | **Demand:** [headlamp#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974). **Marketing/table stakes:** Lens [`cluster-issues.tsx`](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx); Aptakube Workloads Overview, refined in [1.19.5/1.19.6/1.20.0](https://github.com/aptakube/aptakube/releases) | M | **No — `FEAT-24` in the Inbox (P1)**, unbuilt | Existing row, evidence refreshed. Decide the refresh model in the spec: Headlamp moved its overview to **polling** in v0.44 to stop OOMs, and hard rule 2 says list+watch — a cluster-wide watch × every fleet member is the case that rule was not written for. Needs Lens's "everything is fine" empty state (UI rule 9). Should include Aptakube's *recent restarts* (restart count > 0 with a recent `lastState.terminated.finishedAt`, which `ResourceStatusSummary.LastRestartAt` already computes), greyed for `Completed` as Aptakube 1.20.0 learned |
| **3. See a workload's revision history** — a *Revisions* tab on the workload detail pane: revision number, age, image(s), change-cause, and which is current, read from owned ReplicaSets (`deployment.kubernetes.io/revision`) or ControllerRevisions (StatefulSet/DaemonSet). *Job: what changed* | **Demand:** [headlamp#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222) *"Maybe also see the history of rollouts to select"* → shipped in Headlamp v0.41. **Marketing:** kdashboard README; Aptakube [#285](https://github.com/aptakube/aptakube/issues/285) (ControllerRevision view) | M | **No.** Workload detail has Pods/Conditions/Events tabs (`WorkloadDetailView.axaml`); ReplicaSets are browsable only as a raw kind | UI rule 10: a fourth segment on the existing strip, not a new row of chrome. One `ListResourceOnceAsync` of ReplicaSets filtered by ownerReference UID — no new watch. The demo dataset has no ReplicaSet history; a demo tab needs one (demo rule 4) or an honest empty state |
| **4. Diff two revisions of a workload** — select a revision, see its pod template against the current one in the apply preview's line diff. *Job: what changed — "which change broke it" without leaving the app* | **Marketing:** kdashboard "side-by-side pod template diffs"; Aptakube *Compare* view, spec-only since [1.20.0](https://github.com/aptakube/aptakube/releases). No upvoted demand found | S (on top of 3) | **Partial.** `TextDiff`/`TextDiffPair` (Core) and the apply preview's rendering exist; nothing feeds them two revisions | Strip `pod-template-hash` and metadata before diffing or every pair reads as changed. Cheap only after item 3 lands; score them together |
| **5. Roll a workload back to a chosen revision** — from the Revisions tab, on the confirm strip. *Job: act on a resource — the "remedy critical situations quickly" gesture* | **Demand:** [headlamp#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222) (*"a fairly important operation"*), shipped v0.41; a third-party [Argo Rollouts plugin](https://github.com/portone-io/headlamp-argo-rollouts) ported it. **Table stakes:** k9s `Ctrl-l`. FreeLens [#418](https://github.com/freelensapp/freelens/issues/418) open | M | **No** | Mutating, so UI rule 17's strip. Deployment rollback is client-side in `apps/v1` (kubectl copies the ReplicaSet's template, minus `pod-template-hash`, into the Deployment) — Headlamp's open null-reference bug [#6337](https://github.com/kubernetes-sigs/headlamp/issues/6337) is the cost of getting that wrong. **Two tensions for the human:** (a) Helm rollback is deliberately out of scope ("Helm write operations are deliberate") — workload rollback is a different object, but the owner may want the two decisions to read consistently; (b) rolling back a Deployment that Argo CD manages with self-heal is undone within seconds — the strip should say so when the object carries Argo's tracking label or annotation. kubeNimbus does **not** read those today (`ArgoCd.cs` knows only the refresh annotation), so that check is new work |
| **6. See how many Warning events an object has before opening its Events tab** — a count on the Events segment of pod and workload detail. *Job: why is this one broken* | **Marketing:** Aptakube 1.19.5 "Warning Count badge to Events tab" ([releases](https://github.com/aptakube/aptakube/releases)). No demand issue found | S | **No.** Pod and workload Events are plain `ListBoxItem` segments | Events are already fetched on open (`WorkloadDetailTabViewModel.Events`, pod detail's Events tab), so the count is free. Text, not a coloured dot (UI rule 11's reasoning; `FEAT-73`'s status-dot finding) |
| **7. A one-line "why" for an unhealthy pod or workload** — deterministic: the dominant failure reason, exit code, whether previous logs exist, the scheduling message for Pending; for a workload, "3 of 5 pods CrashLoopBackOff: exit 137 (OOMKilled)". *Job: why is this one broken* | **Marketing:** Headlamp v0.43 Diagnostics, [PR #5487](https://github.com/kubernetes-sigs/headlamp/issues/5487), positioned as needing no AI. **Demand: weak** — the August [pod-workload-detail report](../../../research/2026-08-18-pod-workload-detail.md) found "a clean negative" for a troubleshooting assistant | M | **Partial.** Status pills name the reason; Overview tab shows conditions and probes; `P` loads previous logs. Nothing aggregates owned pods' reasons for a workload | Worth taking only as the workload half (aggregating owned pods), which nothing in kubeNimbus does today. The pod half largely restates the status pill. Must fit UI rule 10's two-row budget — a line inside the Overview tab, not a new row above it |
| **8. Clear the log pane without restarting the stream** | **Demand:** [lens#5315](https://github.com/lensapp/lens/issues/5315) (open); Aptakube [#578](https://github.com/aptakube/aptakube/issues/578) → shipped 1.20.2 | S | **No — `FEAT-40` in the Inbox** | Off-theme; listed because a competitor shipped it inside the survey window. Applies to both the single-pod and multi-pod panes |
| **9. See who last changed a field** — a blame gutter in the YAML view from `managedFields` (manager name + time per field). *Job: what changed* | **Demand, weak:** [kubectl-blame](https://github.com/knight42/kubectl-blame), 155 stars, on krew. **Marketing:** kdashboard [#116](https://github.com/folio-pro/kdashboard/issues/116) | M | **No.** The YAML editor strips `managedFields` (`YamlEditorTabViewModel`) | `managedFields` uses the `FieldsV1` trie format — parseable with `JsonDocument`, AOT-safe, no new dependency. Lowest evidence on this list; include only if the change-history slice (3–5) is taken and this rounds it out |

**Not proposed, with the reason:**

- **A cluster-wide event timeline windowed by time** — every client stops at a sortable
  events list, and no request for more was found.
- **An AI assistant / MCP endpoint** — marketing pressure at Lens, KubeUI and kdashboard;
  it would sit against the no-network-beyond-your-clusters stance. A human decision.
- **Flux in the GitOps section** — Aptakube added it (1.19.7), and kubeNimbus has Argo
  only. No demand evidence was gathered for Flux in this survey; worth its own survey
  before it is scored.
- **Ephemeral debug containers** (Headlamp v0.45) — not in this survey's theme and no
  demand evidence was gathered; noted for a later survey.

## Sources

- **Aptakube:** [releases](https://github.com/aptakube/aptakube/releases) ·
  [issues since 2026-08-01](https://github.com/aptakube/aptakube/issues?q=is%3Aissue%20created%3A%3E2026-08-01) ·
  [rollback/revision issue search](https://github.com/aptakube/aptakube/issues?q=is%3Aissue%20rollback%20OR%20revision%20OR%20history%20OR%20rollout) ·
  [#285](https://github.com/aptakube/aptakube/issues/285) · [#578](https://github.com/aptakube/aptakube/issues/578) ·
  [1.19.6 on newreleases.io](https://newreleases.io/project/github/aptakube/aptakube/release/1.19.6) ·
  [aptakube.com (search summary only)](https://aptakube.com/)
- **Headlamp:** [releases](https://github.com/kubernetes-sigs/headlamp/releases) ·
  [#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974) · [#5487](https://github.com/kubernetes-sigs/headlamp/issues/5487) ·
  [#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222) · [#6337](https://github.com/kubernetes-sigs/headlamp/issues/6337) ·
  [#6590](https://github.com/kubernetes-sigs/headlamp/issues/6590) · [#7523](https://github.com/kubernetes-sigs/headlamp/issues/7523) ·
  [#7608](https://github.com/kubernetes-sigs/headlamp/issues/7608) · [#7660](https://github.com/kubernetes-sigs/headlamp/issues/7660) ·
  [rollback/revision issue search](https://github.com/kubernetes-sigs/headlamp/issues?q=is%3Aissue%20rollback%20OR%20%22rollout%20history%22%20OR%20revision%20OR%20%22revision%20history%22) ·
  [Argo Rollouts plugin](https://github.com/portone-io/headlamp-argo-rollouts) ·
  [computingforgeeks guide (v0.41 rollback detail)](https://computingforgeeks.com/headlamp-kubernetes-dashboard/)
- **Lens** (snippets only; vendor sites blocked): [2026.8 post](https://lenshq.io/blog/lens-release-august26/) ·
  [2026.8 forum thread](https://forums.k8slens.dev/t/lens-2026-8-190756-latest-release/7171) ·
  [premium features](https://docs.lenshq.io/k8slens/premium-features/) ·
  [premium features blog](https://lenshq.io/blog/lens-premium-features) ·
  [2026.3 post](https://lenshq.io/blog/lens-release-march26) ·
  [Chocolatey 2026.9.20601](https://community.chocolatey.org/packages/lens) ·
  [k8slens.dev](https://k8slens.dev/) ·
  [cluster-issues.tsx](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx) ·
  [lens#5315](https://github.com/lensapp/lens/issues/5315)
- **FreeLens:** [releases](https://github.com/freelensapp/freelens/releases) ·
  [open issues by reactions](https://github.com/freelensapp/freelens/issues?q=is%3Aissue%20is%3Aopen%20sort%3Areactions-%2B1-desc) ·
  [rollback/revision issue search](https://github.com/freelensapp/freelens/issues?q=is%3Aissue%20rollback%20OR%20revision%20OR%20%22rollout%20history%22%20sort%3Areactions-%2B1-desc) ·
  [#2279 v2.0.0](https://github.com/freelensapp/freelens/issues/2279) · [#2376](https://github.com/freelensapp/freelens/issues/2376) ·
  [#418](https://github.com/freelensapp/freelens/issues/418) · [#466](https://github.com/freelensapp/freelens/issues/466) ·
  [#627](https://github.com/freelensapp/freelens/issues/627) · [#1670](https://github.com/freelensapp/freelens/issues/1670) ·
  [#687](https://github.com/freelensapp/freelens/issues/687) · [#2056](https://github.com/freelensapp/freelens/issues/2056)
- **KubeUI:** [releases](https://github.com/IvanJosipovic/KubeUI/releases) ·
  [KubeUI.Desktop.csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj)
- **k9s:** [releases](https://github.com/derailed/k9s/releases) · [README](https://github.com/derailed/k9s/blob/master/README.md) ·
  [rollback issue search](https://github.com/derailed/k9s/issues?q=is%3Aissue%20rollback%20OR%20%22rollout%20history%22%20OR%20%22rollout%20undo%22%20sort%3Areactions-%2B1-desc) ·
  [#1077](https://github.com/derailed/k9s/issues/1077)
- **Others:** [kdashboard](https://github.com/folio-pro/kdashboard) · [kdashboard#116](https://github.com/folio-pro/kdashboard/issues/116) ·
  [kubectl-blame](https://github.com/knight42/kubectl-blame) · [Seabird](https://github.com/getseabird/seabird) (not re-verified) ·
  [srexpert 2026 comparison (secondary)](https://srexpert.cloud/blog/best-kubernetes-gui-tools-compared-2026)
- **Earlier reports this builds on:** [`docs/research/`](../../../research/), especially
  [node-ops-and-overview](../../../research/2026-08-16-node-ops-and-overview.md) and
  [kubeui-positioning](../../../research/2026-08-17-kubeui-positioning.md)
