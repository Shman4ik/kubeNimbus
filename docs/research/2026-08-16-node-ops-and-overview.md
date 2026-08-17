# Node operations, and the "what is wrong with this cluster right now" screen

*2026-08-16. Question asked: `docs/BACKLOG.md` section B is marked unvalidated. Test the
two hypotheses behind its largest P1/P2 pair — FEAT-4 (node detail + cordon/uncordon/drain)
and FEAT-9 (cluster overview) — and say whether their priorities are the right way round.*

The short version: **both items are real, and both are mis-shaped.** Each bundles a cheap,
well-evidenced half with an expensive half, and in each case the expensive half is the one
the item is named after. Split them and the order changes.

## What was searched, and what could not be reached

This session's egress policy blocks every vendor site in the field —
`k8slens.dev`, `lenshq.io`, `forums.k8slens.dev`, `aptakube.com`, `headlamp.dev`,
`k9scli.io`, `freelens.app` — and also `reddit.com`, `news.ycombinator.com` and
`web.archive.org`. `github.com`, `raw.githubusercontent.com` and web search are reachable.

Two consequences the reader should weigh:

- **No Reddit or Hacker News thread was read.** The brief asked for them. That is a gap in
  this report, not a finding about them.
- **Landing-page claims are cited indirectly** — through the vendors' own open-source code
  and issue trackers where they exist, through their GitHub READMEs, and through search
  snippets otherwise. Every marketing claim below says which. Where a claim could not be
  verified from the vendor's own page, it says that too.

What *was* read directly: the MIT-licensed Lens Desktop Core sources for the cluster
overview, k9s's node DAO, Headlamp's overview and node-detail components, the Aptakube /
FreeLens / Headlamp / Portainer issue trackers, and Aptakube's GitHub release notes.

## The field: who ships node operations, and how

| Product | Node actions shipped | How drain is implemented | Overview screen |
|---|---|---|---|
| **Lens** | Shell, Cordon, Drain, Edit, Remove on the node context menu ([walkthrough](https://mohammaddarab.com/a-look-inside-big-data-cluster-infrastructure-using-a-kubernetes-ide/)) | **Shells out to the user's `kubectl`** — see FreeLens #31 below, and the Lens forum report that [2024.11's Drain action still passed `--delete-local-data`](https://forums.k8slens.dev/t/lens-2024-11-drain-action-still-using-kubectl-delete-local-data-instead-of-delete-emptydir-data/3903) (page itself blocked here; title and snippet from search) | Yes — the default cluster route. Metrics half is **Prometheus-gated** |
| **FreeLens** | Node/pod menu was removed from OpenLens in 6.3.0 and restored by a community extension ([`openlens-node-pod-menu`](https://github.com/alebcay/openlens-node-pod-menu), forked as [`freelens-node-pod-menu`](https://github.com/freelensapp/freelens-node-pod-menu), now deprecated/empty) | `kubectl drain <node> --delete-local-data --ignore-daemonsets --force`, verbatim, in [freelensapp/freelens#31](https://github.com/freelensapp/freelens/issues/31) | Inherited from Lens, same Prometheus gate |
| **Aptakube** | Cordon + Drain, single and multi-select | **Shells out to `kubectl`** — [#412](https://github.com/aptakube/aptakube/issues/412) reports `kubectl error: Os { code: 13, kind: PermissionDenied }`, a host filesystem error on the binary, not an API error | Yes — "Workloads Overview", plus a "Node overview" with CPU/Memory tiles |
| **Headlamp** | Cordon/uncordon and Drain, as a **backend** job with a status-polling endpoint ([`node/Details.tsx`](https://github.com/kubernetes-sigs/headlamp/blob/main/frontend/src/components/node/Details.tsx) imports `drainNode`, `drainNodeStatus`) | Hand-rolled in Go (see the bug list below) | Yes — [`cluster/Overview.tsx`](https://github.com/kubernetes-sigs/headlamp/blob/main/frontend/src/components/cluster/Overview.tsx) |
| **k9s** | `u` cordon/uncordon, `r` drain in the node view; node shell behind a per-cluster **feature gate, default off** ([README](https://github.com/derailed/k9s/blob/master/README.md)) | Imports [`k8s.io/kubectl/pkg/drain`](https://github.com/derailed/k9s/blob/master/internal/dao/node.go) — kubectl's own library | `:pulses`, plus [Popeye](https://popeyecli.io/) integration as a cluster sanitizer |
| **Portainer** | Requested in [#4006](https://github.com/portainer/portainer/issues/4006) (2020) with an explicit safety design | — | — |
| **kubeNimbus** | Node status incl. `Ready,SchedulingDisabled`, node CPU/Mem from `metrics.k8s.io`, roles + kubelet version in Details | Nothing | Nothing |

**The single most useful finding in this table is the third column.** Nobody in this field
implements drain from scratch except the tools written in Go, where `k8s.io/kubectl/pkg/drain`
is a free import. The two non-Go GUIs — Lens/FreeLens (Electron) and Aptakube (Tauri/Rust) —
both **spawn the user's `kubectl` binary**, and both have public bug reports that only make
sense if that is what they do. Headlamp is the one project that hand-rolled the algorithm in
its own backend, and it is paying for it:

- [#7268](https://github.com/kubernetes-sigs/headlamp/issues/7268) (open, 15 Aug 2026) — the
  drain deletes **mirror pods** (which the kubelet immediately recreates) and **`emptyDir`
  pods without warning**, i.e. silent permanent data loss. `kubectl drain` skips mirror pods
  entirely and refuses `emptyDir` pods unless `--delete-emptydir-data` is passed.
- [#5736](https://github.com/kubernetes-sigs/headlamp/issues/5736) (closed 8 Aug 2026,
  blocker) — deleted **DaemonSet** pods on Kubernetes 1.16+ because a label check had been
  removed upstream.
- [#5734](https://github.com/kubernetes-sigs/headlamp/issues/5734) (closed 21 May 2026) — the
  drain goroutine ignored the server's shutdown context, so pods kept being deleted after a
  restart.
- [#6750](https://github.com/kubernetes-sigs/headlamp/issues/6750) (open) — the drain **status**
  handler asserts a cached value to `string` without checking.

That is four correctness bugs in one year, in a CNCF project, in Go, with the reference
implementation available to read. It is the strongest available estimate of what "drain"
costs a from-scratch implementation.

kubeNimbus is in the non-Go camp *and* has deliberately made `kubectl` optional — FEAT-16
ships a launcher whose whole design note says a missing `kubectl` warns and never blocks. So
the two cheap paths other GUIs took are both unavailable as written. `KubernetesClient.Aot`
19.0.2 offers `CreateNamespacedPodEvictionAsync` (the eviction primitive) and **no drain
helper** — checked in the package's own XML docs. Everything above that primitive would be
ours: pod filtering (mirror / DaemonSet / standalone / `emptyDir`), the eviction loop, PDB
429 back-off, `--force`, `--grace-period`, `--timeout`, and progress/partial-failure
reporting.

## FEAT-4, tested

### The half that is well-evidenced and cheap

FEAT-4's stated signal — "node operations are the classic reason to open a GUI at 3 a.m." —
is a slogan, and the evidence for it is thin *as stated*. What the evidence does support,
precisely, is that people want to **see** node state and to **stop scheduling** on a node:

- [freelensapp/freelens#1154](https://github.com/freelensapp/freelens/issues/1154) — "Add
  Status column to Nodes tab" (Sep 2025). The reporter's rationale is exact: nodes are
  cordoned during maintenance, `kubectl get nodes` prints `Ready,SchedulingDisabled`, and
  FreeLens made you click into a node or read its taints to find out. **kubeNimbus already
  renders exactly that string** (`ResourceStatusSummary.SummarizeNode`), and colours a
  cordoned node warn rather than ok. Headlamp had the same bug —
  [#5742](https://github.com/kubernetes-sigs/headlamp/issues/5742), "cordoned nodes still
  appear as Ready in node status label", closed May 2026.
- [aptakube#474](https://github.com/aptakube/aptakube/issues/474) — "Add sum of
  limits/allocatable at resource overview page": *"Sometimes we also need to see all
  limits/allocatable to check cluster's water level."* This is FEAT-4's "allocatable vs
  requested", requested against a paid competitor.
- [aptakube#438](https://github.com/aptakube/aptakube/issues/438) — an ephemeral-storage tile
  "next to CPU and Memory utilization" in the **Node overview**, i.e. a per-node capacity
  page that exists and that users want more of.
- [aptakube#567](https://github.com/aptakube/aptakube/issues/567) — "Karpenter incorrect nodes
  count" (Aug 2026), and [freelens#2409](https://github.com/freelensapp/freelens/issues/2409)
  — "Cluster metrics show almost no history on autoscaled clusters". Node churn under
  Karpenter/CAS is a real reporting surface, and both incumbents get it wrong.

None of this needs new engine work in kubeNimbus. "Pods on this node" is
`ListResourceOnceAsync(..., fieldSelector: "spec.nodeName=<node>")` and that parameter
already exists in `ClusterClient.Dynamic.cs`. Requested-vs-allocatable is arithmetic over
pod specs with `Quantity.cs`, which already parses `100m`/`128Mi`/`12345n`. Node CPU/Mem is
already polled — `IsMeteredKind` covers `Pod` **and** `Node`, so the list columns work today.
**Cordon/uncordon is a one-field merge patch of `spec.unschedulable`**, structurally
identical to FEAT-1's rollout-restart annotation patch, and it lands on FEAT-1's already
shipped confirm strip (UI rule 17). Cordon is an `S`, not part of an `L`.

### The half that is table stakes and expensive

Drain. The brief asks whether operators deliberately keep it in kubectl. **No evidence was
found that they refuse it on principle** — every product in the field ships it, and users
file bugs *against* it, which is evidence of use rather than avoidance. Nor was any
upvoted "please add drain to my GUI" thread found for a desktop client of this class; the
requests that exist are ordinary feature requests, satisfied years ago:
[aptakube#195](https://github.com/aptakube/aptakube/issues/195) (Dec 2023, completed),
[aptakube#134](https://github.com/aptakube/aptakube/issues/134) (Jan 2024, completed),
[aptakube#362](https://github.com/aptakube/aptakube/issues/362) (multi-select, Dec 2024,
completed), [portainer#4006](https://github.com/portainer/portainer/issues/4006) (2020).

So the honest reading is: **drain is table stakes, not a differentiator, and the risk is in
the implementation, not in the desire.** Portainer's own design for it is instructive and is
the closest thing to a stated safety consensus found anywhere: a confirmation modal reading
*"Draining this node will cause all workloads to be evicted from that node. This might lead
to some service interruption. Are you sure?"*, and the drain action **disabled while another
drain is in flight**, because *"the kubectl drain command should only be issued to a single
node at a time"*.

Three further constraints specific to this app:

1. **A desktop client has no backend to own a long-running drain.** Headlamp needed a
   goroutine plus a pollable status endpoint, and its bug #5734 is precisely the lifetime
   question ("what happens when the thing running the drain goes away"). In kubeNimbus the
   drain would run in the app process: closing the tab, or quitting, stops evicting
   mid-way. The node stays cordoned, some pods are gone and some are not, and nothing tells
   you. That state needs designing before any of it is written.
2. **A drain can block indefinitely** on a PDB (the eviction API returns 429), which is
   *correct* behaviour and looks identical to a hung UI. Every flag kubectl exposes
   (`--timeout`, `--force`, `--delete-emptydir-data`, `--disable-eviction`,
   `--grace-period`) exists because someone hit that; k9s's `DrainOptions` carries all five.
   A GUI that hides them is wrong, and a GUI that shows all five is a form, not a menu item.
3. **FEAT-16 gives kubeNimbus a third option nobody else has.** The terminal launcher already
   opens the user's own shell with an overlay kubeconfig pinning this cluster's context. A
   "Drain this node…" action could hand `kubectl drain <node> --ignore-daemonsets …` to that
   terminal, pre-filled — which is *what Lens and Aptakube do anyway*, except visibly, in the
   user's own shell, with the user's own kubectl and their own flags, and with the output and
   the Ctrl-C where they expect them. It costs an `S`, it cannot silently delete an
   `emptyDir` pod, and it degrades honestly when `kubectl` is absent (that state already
   exists). It is not as slick as an in-app progress bar. It is defensible on day one.

### Proposal for FEAT-4's text

Split it. Node detail + cordon/uncordon is a well-evidenced `M` that closes most of the
value. Drain is its own item, `L`, and its first deliverable is a decision between
{in-app eviction loop, hand to the terminal, don't ship}, not code.

## FEAT-9, tested

### The marketing claim, checked

FEAT-9's signal reads "the screen Lens leads its marketing with". **I could not verify that
from Lens's own site** — `k8slens.dev` and `lenshq.io` are both blocked here. What can be
verified is stronger than a landing page anyway, because it is Lens's own MIT-licensed code:

[`packages/core/src/renderer/components/cluster/`](https://github.com/lensapp/lens/tree/master/packages/core/src/renderer/components/cluster)
contains `cluster-overview.tsx`, `cluster-pie-charts.tsx`, `cluster-metrics.tsx`,
`cluster-no-metrics.tsx` and — the interesting one —
[`cluster-issues.tsx`](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx).
Reading them:

- The overview composes **pie charts** (pod/node status counts, computed from the API — no
  Prometheus), **metrics charts** (Prometheus), and **Cluster Issues**.
- Cluster Issues is literally the FEAT-9 screen: it merges each node's
  `getWarningConditions()` with `eventStore.getWarnings()` into one sortable Message / Object
  / Type / Age table, with the empty state *"No issues found — Everything is fine in the
  Cluster"*.
- When metrics are disabled the overview renders `<ClusterIssues className="OnlyClusterIssues"/>`
  — the issues table **is** the fallback overview.
- `cluster-no-metrics.tsx` contains the string this field is famous for: *"Metrics are not
  available due to missing or invalid Prometheus configuration."*

Third-party descriptions agree that Lens opens on this screen ([HowToGeek](https://www.howtogeek.com/devops/how-to-visualize-your-kubernetes-cluster-with-the-lens-dashboard/):
"Lens defaults to showing a cluster overview screen"; [Loft](https://www.vcluster.com/blog/kubernetes-dashboards-lens)).
Treat "leads its marketing with it" as **plausible and unverified**; treat "it is the default
route and it is where Lens puts cluster health" as **verified from source**.

Now the counter-evidence, which is the more interesting half:

**Aptakube — the closest competitor in positioning — does not market an overview at all.**
Its [README feature list](https://github.com/aptakube/aptakube) is: multi-cluster, aggregated
log viewer, resource diff, multi-namespace selector, human-friendly resource view, view &
modify, zero-config, *"**NOT** another Electron app"*. Search-engine summaries of the blocked
landing page match (multi-cluster, log aggregation, metrics with **metrics-server and
Prometheus**, port-forwarding, resource comparison). No overview, no dashboard.

**And yet Aptakube shipped one, as a headline release.** [Release 1.9.0](https://github.com/aptakube/aptakube/releases/tag/1.9.0)
(Nov 2024) is titled **"Workloads Overview"** and adds *"Recent Warnings/Restarts/High
Resource Usage to Workloads Overview"* — which is FEAT-9's second and third bullets almost
word for word. It has been extended steadily since: HPA in 1.17.2, PVC status in 1.18.4.

That is the demand/marketing split this repo's research is supposed to surface, and here it
runs the *opposite* way to the FEAT-9 hypothesis: the overview is **built and used but not
marketed**; what gets marketed is aggregated logs (FEAT-3), diff (FEAT-5) and startup speed.

### Do people actually use it, or bounce to a resource list?

The best available evidence is refinement traffic — nobody files a bug about a chart they
never look at:

- Aptakube: [#462](https://github.com/aptakube/aptakube/issues/462) make the *Abnormal
  Resource Usage* table sortable; [#461](https://github.com/aptakube/aptakube/issues/461)
  show the context name in it; [#500](https://github.com/aptakube/aptakube/issues/500) total
  CPU/memory in the workloads overview; [#480](https://github.com/aptakube/aptakube/issues/480)
  cluster auth status on the home screen.
- Headlamp: [#6897](https://github.com/kubernetes-sigs/headlamp/issues/6897) overview charts
  show 0 for namespace-limited users; [#6816](https://github.com/kubernetes-sigs/headlamp/issues/6816)
  usage percentages ~1000× wrong when the metrics API emits milli-units;
  [#6716](https://github.com/kubernetes-sigs/headlamp/issues/6716) tiles briefly show 0.0 %
  while loading; [#6090](https://github.com/kubernetes-sigs/headlamp/issues/6090) the Pods
  chart always shows 100 %; [#5863](https://github.com/kubernetes-sigs/headlamp/issues/5863)
  Chrome renderer OOM on the clusters overview.
- FreeLens: [#2020](https://github.com/freelensapp/freelens/issues/2020) "Add a refresh button
  on the cluster overview".
- And the clearest statement of the underlying need, filed against Headlamp in Aug 2026 —
  [#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974), *"Add a cluster-wide
  diagnostics overview page"*: **"There is no way to see what is unhealthy across a cluster
  without opening objects one at a time."** That sentence is FEAT-9's justification, written
  by someone else, about a competitor.

The honest counterweight is also on record, and it is Aptakube's: 1.9.0 made the **initial
screen configurable — Workloads Overview, Pods, or Deployments**. A paid product with real
telemetry concluded that where you land is a preference, not a default worth forcing. Any
kubeNimbus overview should follow that: reachable, not imposed (and UI rule 1 says the same
thing for its own reasons).

### Where the incumbents are broken, which is where a new entrant wins

This is the strongest finding in the report, and it is not in either backlog item:

**In the Lens lineage, the cluster overview and the node metrics are blank unless you run
Prometheus, and people have been complaining about it for six years.**

- Sorting FreeLens's issues by reactions puts two metrics issues at the top of the list:
  [#466](https://github.com/freelensapp/freelens/issues/466) "Metrics Server and External
  Prometheus" (Mar 2025, open) and [#627](https://github.com/freelensapp/freelens/issues/627)
  "Metrics are not displayed using metrics-server" (Apr 2025, open). #627 names the affected
  screens exactly: **cluster overview, nodes list, pod details**, all showing *"Metrics are
  not available due to missing or invalid Prometheus configuration"* on a cluster that runs
  metrics-server. #466 additionally wants an external Prometheus-compatible backend
  (VictoriaMetrics, Mimir) rather than one installed in-cluster.
- The same complaint against Lens itself spans releases and years:
  [#957](https://github.com/lensapp/lens/issues/957), [#1189](https://github.com/lensapp/lens/issues/1189),
  [#2437](https://github.com/lensapp/lens/issues/2437), [#8044](https://github.com/lensapp/lens/issues/8044),
  and it leaks into other projects' trackers —
  [prometheus-community/helm-charts#4647](https://github.com/prometheus-community/helm-charts/issues/4647)
  is a chart issue filed because Lens could not read the labels.
- Node metrics specifically are still wrong in the fork:
  [freelens#1883](https://github.com/freelensapp/freelens/issues/1883), "Nodes list view shows
  CPU as `0.0Ki`".

kubeNimbus already reads `metrics.k8s.io`, with the version taken from **discovery**, and
already degrades to no columns rather than an error when the group is absent. An overview
built on `metrics.k8s.io` + Warning events + node conditions is *precisely the screen these
users are asking two other projects for*, and kubeNimbus can build it from parts it shipped
months ago. Aptakube supports metrics-server too — so this is a wedge against the Lens
lineage, not against the whole field, and the landing page (DIST-4) should say it in those
terms.

**Non-goal check.** This does not challenge "long-range metrics history is Prometheus's job".
Everything above is point-in-time plus the existing bounded `UsageHistory` ring. The line to
hold is: an overview may show *now* and the session's own window; it may not grow a store.
Note that FreeLens users are asking for the other thing too ([#2409](https://github.com/freelensapp/freelens/issues/2409),
metric history on autoscaled clusters) — that request is correctly refused here.

**One engineering warning, taken from Headlamp's source.** Its overview polls on a 60 s
interval with this comment in place: *"The overview only needs periodic snapshots for
aggregate charts. Avoid long-lived watches here because large clusters can stream enough
events to exhaust the tab."* kubeNimbus's hard rule 2 is list+watch everywhere; an overview
that opens watches on pods, nodes and events across a cluster (× every cluster in a fleet) is
exactly the case that rule was not written for. Headlamp's OOM report (#5863) is what it looks
like when you get it wrong. This deserves a deliberate decision in the item, not a default.

## Are FEAT-4 (P1) and FEAT-9 (P2) the right way round?

**As written, no — but the fix is to split them, not to swap them.** Both are `L` because each
contains a hard part; separated, three of the four halves are `M` or smaller and the ordering
falls out of the evidence:

| Work | Evidence | Cost | Where it should sit |
|---|---|---|---|
| Node detail + cordon/uncordon | Direct requests against two competitors (freelens#1154, aptakube#474/#438); every competitor ships it; **no new engine work** | M | Highest of the four — this is the FEAT-4 that deserves P1 |
| Cluster issues (Warning events + node warning conditions + unhealthy workloads) | Lens's own fallback overview; aptakube 1.9.0; headlamp#6974 states the need in one sentence | M | Second. Cheaper and better evidenced than drain |
| Capacity overview (node capacity, requests vs allocatable, counts) | aptakube#474/#438/#500; the Prometheus-gap issues above | M | Third. This is where the wedge against Lens/FreeLens lives |
| Drain | Table stakes, universally shipped, no unmet demand found; four Headlamp bugs; no C# library; no backend; blocks on PDBs | L | Last, and gated on a design decision |

So: **FEAT-4 keeps its P1 for the half that is not drain, and drain drops below FEAT-9's
issues panel.** That is a real inversion of the current table, and it is driven by two things
the hypotheses did not know — that drain is a from-scratch implementation in this stack while
every rival either imports it or shells out, and that the overview screen is being actively
refined by users of three separate competitors while the drain requests were all closed years
ago.

## The demo cluster interacts with this, and today it would embarrass us

`DemoData` declares a `Node` descriptor in its catalog but ships **no node objects** — the
dataset loads `pods`, `deployments`, `events`, `pod-metrics`, `secret`, `configmaps` and
`crd-catalog`, and nothing else. So a Microsoft Store reviewer — the audience the demo cluster
was built for — clicking **Nodes** today lands on the "No Nodes found" empty state, which is
correct behaviour and a poor advertisement. Build FEAT-4 or FEAT-9 on top of that and the
reviewer's node detail is empty and their overview reads zero nodes, zero capacity.

Adding `nodes.json` (three nodes: two Ready, one `Ready,SchedulingDisabled` with a taint, so
the cordon/uncordon and conditions surfaces both have something to show) plus node entries in
the metrics fixture is an `S`, is useful on its own, and is a prerequisite for demoing either
item. Note the demo cluster cannot honestly *perform* a cordon or a drain — like exec and
port-forward, those need an API server, so they take the `Border.demoUnavailable` treatment
(demo rule 5), and `RowActionViewModel.IsDemo` is the existing precedent.

## Proposed backlog items

| — | Node detail inspector tab: conditions, taints, labels, allocatable vs **requested**, and the pods scheduled on the node | Demand: [freelens#1154](https://github.com/freelensapp/freelens/issues/1154), [aptakube#474](https://github.com/aptakube/aptakube/issues/474) ("check cluster's water level"), [aptakube#438](https://github.com/aptakube/aptakube/issues/438); every competitor ships a node detail page | M | P1 | | Narrows FEAT-4 to its evidenced half. No new engine work: `ListResourceOnceAsync` already takes `fieldSelector` (`spec.nodeName=`), `Quantity.cs` already parses requests, node CPU/Mem is already polled (`IsMeteredKind` covers `Node`). Fits the ~300 px dock only under UI rule 10 — two chrome rows, so conditions/taints/pods want the `ListBox.segmented` + `TabControl.headerless` pattern, not a fourth stacked row |
| — | Cordon / uncordon from the row context menu and the palette, on FEAT-1's confirm strip | Demand: [freelens#1154](https://github.com/freelensapp/freelens/issues/1154) (people cordon for maintenance and need to see it), [aptakube#195](https://github.com/aptakube/aptakube/issues/195), [aptakube#134](https://github.com/aptakube/aptakube/issues/134), k9s `u` | S | P1 | | A one-field merge patch of `spec.unschedulable` — structurally the same as FEAT-1's `restartedAt` patch, same strip (UI rule 17), same `WorkloadActionsTests` pattern of pinning the patch bytes. `SummarizeNode` already renders `Ready,SchedulingDisabled`, so the list reflects it with no extra work. Demo cluster must refuse in place |
| — | **Decide** the drain strategy before writing any of it: in-app eviction loop vs. handing `kubectl drain` to FEAT-16's terminal vs. not shipping — *done when the decision and its reasoning are in `CLAUDE.md`* | Marketing/table stakes: shipped by [Lens](https://mohammaddarab.com/a-look-inside-big-data-cluster-infrastructure-using-a-kubernetes-ide/), Aptakube, Headlamp, k9s. Demand for it in a GUI is ordinary, not upvoted — all requests found were closed years ago | S | P1 | | Splits the expensive half out of FEAT-4. Inputs to the decision: `KubernetesClient.Aot` ships `CreateNamespacedPodEvictionAsync` and **no drain helper**; Lens and Aptakube both shell out to `kubectl` ([freelens#31](https://github.com/freelensapp/freelens/issues/31), [aptakube#412](https://github.com/aptakube/aptakube/issues/412)); Headlamp hand-rolled it in Go and shipped four correctness bugs in a year ([#7268](https://github.com/kubernetes-sigs/headlamp/issues/7268), [#5736](https://github.com/kubernetes-sigs/headlamp/issues/5736), [#5734](https://github.com/kubernetes-sigs/headlamp/issues/5734), [#6750](https://github.com/kubernetes-sigs/headlamp/issues/6750)) |
| — | If the decision is to implement it: node drain with eviction-API semantics — skip mirror pods, skip DaemonSet-managed pods, refuse `emptyDir` pods unless explicitly allowed, honour PDB 429 with back-off, expose grace-period/timeout/force, one drain at a time, and a cancel that leaves a stated partial state | Marketing/table stakes; the correctness bar comes from [kubectl drain](https://kubernetes.io/docs/tasks/administer-cluster/safely-drain-node/) and from Headlamp's four bugs | L | P2 | | Blocked on the decision row above. A desktop app has **no backend**: closing the tab or quitting stops the eviction loop mid-drain — that state needs a design, and it is Headlamp [#5734](https://github.com/kubernetes-sigs/headlamp/issues/5734) in a different shape. [portainer#4006](https://github.com/portainer/portainer/issues/4006) is the best available safety precedent (confirm sentence naming service interruption; drain disabled while another is in flight). Every failure here is destructive and silent, so `WorkloadActionsTests`-style byte-level pinning plus sandbox-gated integration tests are not optional |
| — | A "Cluster issues" panel: Warning events + node warning conditions + workloads not in a healthy state, one ranked list, each row opening the object | Demand: [headlamp#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974) — *"There is no way to see what is unhealthy across a cluster without opening objects one at a time"*; Lens ships exactly this as [`cluster-issues.tsx`](https://github.com/lensapp/lens/blob/master/packages/core/src/renderer/components/cluster/cluster-issues.tsx) and falls back to it when metrics are off; [aptakube 1.9.0](https://github.com/aptakube/aptakube/releases/tag/1.9.0) "Recent Warnings/Restarts/High Resource Usage" | M | P1 | | Narrows FEAT-9 to its cheapest and best-evidenced half, and it is nearly all parts we own: events are already a kind with Warning/Normal colouring, `ResourceStatusSummary` already classifies health per kind, `SummarizeNode` already reads conditions. Needs an "everything is fine" empty state (UI rule 9) — Lens's is *"No issues found — Everything is fine in the Cluster"*. Decide the refresh model deliberately: Headlamp polls its overview every 60 s with the comment *"avoid long-lived watches here because large clusters can stream enough events to exhaust the tab"*, which is in tension with hard rule 2 |
| — | A capacity overview: node count/status, cluster CPU+memory usage from `metrics.k8s.io`, and **requests vs allocatable** | Demand: [freelens#466](https://github.com/freelensapp/freelens/issues/466) and [freelens#627](https://github.com/freelensapp/freelens/issues/627) are the **top-reacted issues in that repo** and both are "the overview and node metrics are blank because I run metrics-server, not Prometheus"; same complaint against Lens from 2020–2023 ([#957](https://github.com/lensapp/lens/issues/957), [#1189](https://github.com/lensapp/lens/issues/1189), [#2437](https://github.com/lensapp/lens/issues/2437), [#8044](https://github.com/lensapp/lens/issues/8044)); [aptakube#474](https://github.com/aptakube/aptakube/issues/474), [aptakube#500](https://github.com/aptakube/aptakube/issues/500) | M | P2 | | The other half of FEAT-9, and the one competitive wedge found in this whole report: the Lens lineage's overview *requires Prometheus*, kubeNimbus's would not. Stays inside the non-goal — point-in-time plus the existing bounded `UsageHistory`; **do not** add stored history ([freelens#2409](https://github.com/freelensapp/freelens/issues/2409) asks for exactly the thing this repo has permanently refused). Must degrade to counts-only when `metrics.k8s.io` is absent, which `MetricsUnavailableException` already makes easy |
| — | If an overview ships, make the landing surface a preference (overview / last kind / a chosen kind) rather than a forced default | Demand: [aptakube 1.9.0](https://github.com/aptakube/aptakube/releases/tag/1.9.0) shipped the overview **and** made the initial screen configurable between Workloads Overview, Pods and Deployments in the same release | S | P2 | | Direct evidence that "do people use an overview or bounce to a list" is a preference, not a default. Belongs in `settings.json` (a preference, not session state) and must respect UI rule 1 — the overview earns a sidebar entry, not always-visible chrome |
| — | Demo dataset: add nodes and node metrics — three nodes, one of them `Ready,SchedulingDisabled` with a taint | Internal, but it gates the two items above: `DemoData`'s catalog declares `Node` and the dataset has **no node objects**, so the demo's Nodes view is empty today and a demo overview would read zero nodes | S | P2 | | The Store-certification audience is the one that sees this first (demo-cluster rules 4 and 6). Cordon/drain themselves must refuse in place on the demo tab, like exec and port-forward — `RowActionViewModel.IsDemo` is the precedent. Pairs with ENG-14 (nothing tests the demo dataset) |
| — | Node shell — a debug pod on a node with host namespaces, from the node row | Demand: krew's [`node-shell`](https://github.com/kvaps/kubectl-node-shell); k9s ships it behind a per-cluster **feature gate, default off** ([README](https://github.com/derailed/k9s/blob/master/README.md)); Aptakube ships it ([#479](https://github.com/aptakube/aptakube/issues/479), "Tolerations of node shell pod is too weak"); Lens's node menu has "Shell"; it is one of the two things [`openlens-node-pod-menu`](https://github.com/alebcay/openlens-node-pod-menu) existed to restore after 6.3.0 | M | P3 | | **Non-goal tension, stated rather than smuggled:** this creates a privileged, host-namespace pod on the user's cluster. That is not an "in-cluster agent" (nothing persists, nothing phones home), but it is the closest this app would come to one, and the decision belongs to a human. k9s's default-off feature gate is the precedent worth copying if it is taken at all. Depends on FEAT-10's real terminal to be usable |
| — | DIST-4's comparison page should claim "metrics with no Prometheus" explicitly | Marketing: the Lens lineage's overview and node metrics are Prometheus-gated and its users have said so for six years ([freelens#627](https://github.com/freelensapp/freelens/issues/627), [freelens#466](https://github.com/freelensapp/freelens/issues/466), [lens#2437](https://github.com/lensapp/lens/issues/2437)); Aptakube markets metrics-server support, so this differentiates against Lens/FreeLens, not the whole field | S | P2 | | Only honest once there is a screen where it is visible — pairs with the capacity-overview row. Note the framing Aptakube leads with instead, since it is the closest positioning: multi-cluster, **aggregated logs**, **diff**, "NOT another Electron app" ([README](https://github.com/aptakube/aptakube)) — no overview anywhere in its marketing |

## What could not be established

- **No Reddit or Hacker News evidence at all.** Both are egress-blocked here, and search
  snippets did not surface usable quotes. The brief specifically wanted the "Lens alternative"
  and licence-change threads; a session with those hosts reachable should redo that half.
- **No reaction counts.** GitHub renders them client-side and the fetched HTML did not carry
  them, so "top-reacted" for FreeLens #466/#627 means "first under
  `sort:reactions-+1-desc`", not a number. Ranking of issues in this report is by that
  ordering and by the existence of independent duplicate reports, not by counts.
- **Whether FreeLens currently ships cordon/drain in core** — the extension that provided it
  is deprecated and now empty, and `packages/core/.../nodes/` contains no action files;
  GitHub code search requires authentication. The 2024 evidence that it shelled out to
  `kubectl` stands; where the menu lives today does not.
- **Lens's current marketing page in its own words** — blocked. The claim that it "leads with"
  the overview is unverified; that the overview is its default cluster route and its health
  screen is verified from its own source.
- **No usage telemetry from anyone**, which is the only thing that would settle "do people
  look at the overview or bounce". Refinement traffic and Aptakube's configurable landing
  screen are the closest available proxies, and they point in opposite directions by design:
  people use it, and not everyone wants to start there.
