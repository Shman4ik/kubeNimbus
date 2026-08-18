# Research: the workload/pod *detail* surface — what people open a client to read

2026-08-18. Single-theme run, commissioned to cover ground the backlog has not yet
had researched: not logs, not node ops, not the cluster overview, not create/edit —
the pane people actually spend their time in once they've found the object. Four
sub-questions, all from the brief:

1. `kubectl describe`-equivalent detail (conditions, tolerations, affinity, QoS,
   probes, the events digest) — test `FEAT-11`'s hypothesis.
2. Pod troubleshooting flows (CrashLoopBackOff, ImagePullBackOff, unschedulable,
   failing readiness probe) — what a GUI is asked to shortcut.
3. Owner/relationship navigation beyond pod → ReplicaSet → Deployment.
4. Requests/limits vs. actual usage on the detail surface.

## What was searched, and a hard limit up front

`api.github.com` answers `200` on `/rate_limit`, but every repository- and
search-scoped endpoint this session tried (`/repos/{owner}/{repo}/issues`,
`/search/issues`) returns `GitHub access to this repository is not enabled for
this session` or `sessions are bound to their configured repositories` — this
proxy's GitHub App session is scoped to `Shman4ik/kubeNimbus` only, not the
public API generally, and there is no `gh` CLI in the container either. So, as a
prior report in this directory recorded under the same constraint: **no reaction
counts (👍) could be read for anything below.** Ranking is by shipped status
(a feature every competitor ships unprompted, confirmed by reading its own
source, is stronger evidence than a filed-but-unshipped request), issue age,
duplicate filings, and user- vs. maintainer-authorship, read off `WebFetch`
against the issue page and the competitor's own source tree. Every claim below
that names a specific field was checked against the competitor's actual
component source, not inferred from a screenshot or a blog post — where that
was possible; two file lookups 404'd (noted where it happened) and were not
force-fit into a claim.

Codebase check first (`grep -rn` over `src/`): kubeNimbus's `PodDetailTabViewModel`
today parses containers, statuses, environment (literal + Secret/ConfigMap refs),
owner references, and — since the mutating-actions and usage-graphs passes —
resource requests/limits into `ContainerViewModel` fields. It does **not** parse
or render Pod `status.conditions`, `spec.tolerations`, `spec.affinity`,
`spec.priorityClassName`, `spec.nodeSelector`, `spec.runtimeClassName`, or any
container's `livenessProbe`/`readinessProbe`/`startupProbe` — a grep for those
keywords across `src/` returns exactly one hit, and it is a demo fixture's log
line, not app code. That establishes the "before" state cleanly: this is a
real gap, not a wording difference.

## What each competitor leads with, on this surface

**Lens / OpenLens / FreeLens (they share the component).** Confirmed against
`packages/core/src/renderer/components/workloads-pods/pod-details.tsx`: the pod
detail drawer renders, as fixed sections with no user action required, **Status,
Node, Pod IP(s), Service Account, Priority Class, QoS Class, Runtime Class,
Conditions, Node Selector, Tolerations, Affinities, Secrets**, then Init
Containers, Containers, Volumes. This is table stakes in the strictest sense —
it has shipped, unprompted, since Lens's earliest versions, and FreeLens's own
release notes (v1.6.0/v1.6.1) show it is still live and actively tuned in the
fork ("Columns with node and QoS are now hidden by default" — i.e. QoS Class is
common enough to need hiding from the *list*, on top of already being in every
pod's detail).
[Lens source](https://raw.githubusercontent.com/lensapp/lens/master/packages/core/src/renderer/components/workloads-pods/pod-details.tsx) ·
[FreeLens v1.6.0 notes](https://github.com/freelensapp/freelens/releases/tag/v1.6.0)

**Headlamp (CNCF).** Confirmed against `frontend/src/components/pod/Details.tsx`:
**State, Node, Service Account, Host/Pod IP(s), QoS Class, Priority, Priority
Class, Runtime Class, Nominated Node, Start Time, Termination Grace Period,
Reason, Message, Node Selectors**, then a dedicated **Tolerations** table (Key,
Value, Operator, Effect, Seconds) and a **Conditions** section. It does not
appear to render Affinity as its own section — narrower than Lens on that one
field. [Headlamp source](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/pod/Details.tsx)

**k9s.** No drawer at all — instead, `d` ("Describe resource") is a **global**
key binding, documented mid-table in the README's own key-binding reference,
working across every resource kind the same way `y` (YAML) and `l` (logs) do.
It is the terminal-native answer to the same question a GUI drawer answers, and
it is core, not a plugin. [k9s README](https://raw.githubusercontent.com/derailed/k9s/master/README.md)

**Aptakube.** Its own README leads with connect/aggregated-logs/diff/multi-
namespace/"human-friendly resource view"/edit — no bullet names conditions,
tolerations, QoS or probes specifically, so this is not something Aptakube
*markets*; its `aptakube.com/resource-view` page (the more likely place to
find out) was unreachable — blocked by this session's egress proxy — so
Aptakube is a genuine "not found" here, not a negative claim.
[Aptakube README](https://github.com/aptakube/aptakube)

**KubeUI** (the one direct open-source native peer). Its README's own feature
bullets stop at "resource visualization features for understanding
relationships between objects" and name nothing from this list specifically —
no mention of conditions, tolerations, QoS, or probes.
[KubeUI README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)

**Reading across five of six:** three of five GUI competitors (Lens/OpenLens/
FreeLens as one lineage, Headlamp, and k9s in its own idiom) ship a
conditions+tolerations+QoS digest as a **default, unprompted, no-click**
surface — not something users had to ask for, which is itself the strongest
form of "the market has been taught to expect this." This is squarely
**marketing/table-stakes evidence**, not upvoted-issue demand — there is very
little to find asking for it precisely because it predates every issue
tracker's existence in these projects. `FEAT-11`'s hypothesis is **supported**,
more strongly than its current P3/M framing implies, but it should be narrowed
(see Proposed backlog items) — kubeNimbus already ships a dedicated Events tab
(per `CLAUDE.md`), so the "events digest" third of `FEAT-11`'s scope is stale;
what's actually missing is the conditions/tolerations/QoS/priority/probe half.

**A cost note the market doesn't have to pay and kubeNimbus does.** kubectl's
own `describe` is a large Go `text/template`-shaped formatter; none of the
competitors above reimplement that prose — they render **structured fields**
from the object's own JSON, which is what `pod-details.tsx` and Headlamp's
`Details.tsx` both are. That is the cheap, AOT-safe version: the same
`JsonElement` reads `ReadContainerSpecs` already does, no new dependency, no
reflection. A byte-for-byte `kubectl describe` text clone is not what the field
actually ships, and would be a worse bet here.

## Pod troubleshooting flows: a clean negative

No dedicated "explain why this pod is broken" feature was found filed as a
request against any of Lens, FreeLens, Aptakube, Headlamp or k9s. What *was*
found (k9s#174 — pods that died from resource pressure showing as "Pending"
rather than their real terminal state; two `lens`/`freelens` issues about
readiness-probe warnings misfiring on completed Job pods) are display-accuracy
bugs in status classification, not requests for a bespoke troubleshooting
assistant. Read together with the "conditions + events" finding above, the
pattern is consistent: **the ask resolves to "show me `describe`", not to
anything smarter.** kubeNimbus already has the on-call basics this surface
would sit beside — logs starting on open, Previous-container logs, a Restart
action, an Events tab with Reason/Message and Type colour, and status pills
that already distinguish CrashLoopBackOff/ImagePullBackOff from a merely-Pending
pod. Nothing here argues for a new item beyond `FEAT-11`/`FEAT-43` below; it is
reported because the brief asked, and the honest answer is "the market doesn't
ask for more than describe + events, and kubeNimbus already has the events
half."
[k9s#174](https://github.com/derailed/k9s/issues/174) ·
[Lens#2676](https://github.com/lensapp/lens/issues/2676)

## Owner and relationship navigation

kubeNimbus today navigates one edge (`ownerReferences`, pod → ReplicaSet →
Deployment) plus, since the store-readiness pass, `envFrom` sources on the
Environment tab ("each `envFrom` line opens the object it names" —
`CLAUDE.md`). Checked against the code: **individual `configMapKeyRef`/
`secretKeyRef` rows do not** — `EnvVarViewModel` resolves/reveals the *value*
but carries no command to open the *object*, unlike the whole-source
`envFrom` rows two lines above it in the same tab. That is a real, narrow,
cheap gap in a feature that is 90% already built.

**The general want is real and old.** [k9s#745](https://github.com/derailed/k9s/issues/745)
(May 2020, user-filed, closed): *"The kubernetes objects are connected …
similarly, pod's configmap, volume, secrets are related"* — asking for
click-through from a pod's describe view to its ConfigMap/Secret/Volume. It was
closed without shipping, which is unsurprising for a TUI (there is no cheap
analogue of a hyperlink in a terminal grid) but says nothing about GUI demand —
and a GUI is exactly the shape that answers it cheaply, which is the point:
kubeNimbus already built the mechanism (`_openOwner`) for one of the two
directions this issue asks for.

**Service → its pods, confirmed shipped and marketed as a default section.**
Headlamp's own `frontend/src/components/service/Details.tsx` renders a
**"Targeted Pods"** section resolved from the Service's label selector,
alongside **Endpoints** and **Endpoint Slices** tables — no click required, no
extension needed.
[Headlamp source](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/service/Details.tsx)
kubeNimbus already has the *engine* for this and doesn't know it: `FEAT-3`'s
`LabelSelector.ForPodsOf` explicitly lists Service among the selector-capable
kinds (it reads the plain-string-map shape Service/ReplicationController use,
not just the `LabelSelector` object shape) — this is a UI-only gap, not an
engine one.

**PV ↔ PVC cross-linking: real but modest demand.**
[Lens#6425](https://github.com/lensapp/lens/issues/6425) — open since October
2022, user-filed, labelled `good first issue` by Lens's own maintainers (i.e.
acknowledged-easy, still unshipped four years on) — asks for a PV's bound
claim (namespace/name) to be visible rather than requiring
`kubectl get pv -o=custom-columns=...`. `spec.claimRef` on a PV and
`spec.volumeName` on a PVC are both plain scalar fields already present in the
object the app already holds; this is the same "open the object it names"
affordance as the ConfigMap/Secret gap above, applied to a second pair of kinds.

**Two clean negatives, stated plainly rather than smuggled into a row.**
*Ingress → Service was checked directly and found weak even in the strongest
competitor on this list.* Headlamp's own Ingress `Details.tsx` shows backend
`serviceName:port` as **plain, non-clickable text** — the one competitor that
invested in Service→pods, Endpoints and EndpointSlices did *not* bother making
the Ingress→Service edge clickable. That is evidence the edge is cheap to
*show* and not worth *linking*, and no separate row is proposed for it.
*ConfigMap/Secret → who mounts it, the **reverse** direction* (open a ConfigMap
and see which pods reference it, rather than open a pod and see its
ConfigMap) — no filed request was found for this shape in any tracker
searched. It is also the expensive direction: answering it needs a scan of
every pod in the namespace (or cluster) against every env/volume reference,
which is a different cost class from the "the object I already opened names
another object" pattern every finding above uses. Not proposed.

**And one deliberately-not-proposed, larger finding, stated for the record.**
[The Lens Resource Map extension](https://github.com/nevalla/lens-resource-map-extension)
(406 stars, 31 forks) and its FreeLens continuation
([freelensapp/freelens-resource-map-extension](https://github.com/freelensapp/freelens-resource-map-extension))
are real, well-used evidence that *some* users want a force-directed graph of
everything a namespace's objects connect to — pods, their Secrets/ConfigMaps/
PVCs, the Services that route to them, Ingresses and their TLS Secrets, all at
once. That is popularity for a genuinely different feature shape than anything
above: a whole new view, not a click on an existing detail pane, and it sits
uneasily against this app's own rule 1 ("every always-visible control must be
justified") and rule 10's dock-chrome budget. It is reported because the
brief asked for relationship-navigation demand and this is the single
strongest data point found for it — but it is not one of the proposed rows
below; a graph view is a design decision for a human, not a research
recommendation.

## Requests/limits vs. actual usage: the strongest single finding in this run

kubeNimbus already reads `spec.containers[].resources.{requests,limits}` into
`ContainerViewModel` (`CpuRequestNanocores`/`MemoryRequestBytes`/
`CpuLimitNanocores`/`MemoryLimitBytes`) — this shipped quietly as part of the
usage-graphs work, "to give the usage numbers a scale to be read against." But
checked against the actual view: those four numbers are rendered in exactly
**one** place, a hover tooltip (`ResourcesTooltip`) on the container chip and
the container-row header. The Usage tab itself shows live and peak CPU/Memory
as visible text and charts a request/limit is nowhere printed without a mouse
sitting still over the chip.

That is precisely the gap [Lens#4154](https://github.com/lensapp/lens/issues/4154)
describes — open since **October 2021** (four-plus years), user-filed, and
explicit about wanting exactly what kubeNimbus already computes and merely
hides: *"I am not looking for graphs, just the most recent number (in the case
of usage) and what is configured for requests/limits."* It is not an isolated
complaint: [Lens#8106](https://github.com/lensapp/lens/issues/8106) (open,
2024) is a *regression* report — a user who remembers requests/limits used to
show in the pod view and no longer do — and
[FreeLens PR #971](https://github.com/freelensapp/freelens) ("Show resource
requests and limits in Pod details", merged into FreeLens v1.5.0) is the fork
independently fixing the same gap Lens's own issue tracker had left open. A
community fork shipping a fix for something the upstream project's own users
had been asking for since 2021 is close to the strongest shape of evidence
this brief asks to weigh — demand *and* a shipped correction, both pointing
the same direction.

**The cost here is unusually low, and worth stating precisely.** No new Core
work, no new dependency, no new watch or poll: `ContainerViewModel` already
holds all four numbers and `Quantity.FormatCpu`/`FormatMemory`/`Percent` — used
today only inside `ResourcesTooltip`'s string — are exactly the formatters a
visible line would reuse. This is a rendering-only change to the Usage tab.

## Proposed backlog items

Ranked by evidence strength, per the brief's instruction (not by how good the
idea sounds). `FEAT-44` is the strongest single item in this report — cheapest,
oldest demand, and the only one with a shipped-and-corrected signal behind it.

| — | Item | Demand or marketing, with the link | S/M/L | Rec | | Notes |
|---|---|---|---|---|---|---|
| — | Pod detail: render Conditions, Tolerations, QoS Class, Priority Class, Node Selector and each container's Liveness/Readiness/Startup probe config as structured, always-visible sections (not a raw `describe` text clone) | Marketing/table-stakes: shipped and unprompted in [Lens/OpenLens/FreeLens](https://raw.githubusercontent.com/lensapp/lens/master/packages/core/src/renderer/components/workloads-pods/pod-details.tsx) and [Headlamp](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/pod/Details.tsx); `d` (Describe) is a documented [global k9s shortcut](https://raw.githubusercontent.com/derailed/k9s/master/README.md) across every kind | M | P1 | | **Amends/narrows `FEAT-11`, not a duplicate — recommend retiring or rewriting that row rather than running both.** `FEAT-11`'s "events digest" third is already shipped (the dedicated Events tab); this item is the conditions/tolerations/QoS/priority/probe half that genuinely doesn't exist. Structured fields over JSON already held, same pattern as `ReadContainerSpecs` — no new dependency, no reflection, deliberately *not* a kubectl-`describe`-prose clone. Must fit UI rule 10's two-chrome-row budget, likely as a section within or beside the existing container strip rather than a new tab |
| — | Surface container CPU/Memory **requests and limits as visible text** on pod detail's Usage tab (beside current/peak, with %-of-limit), not only in the container-chip hover tooltip | Demand: [lensapp/lens#4154](https://github.com/lensapp/lens/issues/4154), open since **Oct 2021**, explicit — *"not looking for graphs, just the number… what is configured for requests/limits"*; [lensapp/lens#8106](https://github.com/lensapp/lens/issues/8106), open, a **regression** report. Shipped-and-corrected: [FreeLens PR #971](https://github.com/freelensapp/freelens) added exactly this to the fork after years of it being missing upstream | S | P1 | | The strongest single finding here — old, explicit, unusually cheap. `ContainerViewModel.CpuRequestNanocores/MemoryRequestBytes/CpuLimitNanocores/MemoryLimitBytes` and the `Quantity.*` formatters already exist and are used today only inside `ResourcesTooltip`'s hover string; this is a rendering change to the Usage tab reusing both, no new Core work |
| — | Let an individual `configMapKeyRef`/`secretKeyRef` env row **open its source object**, the same "open the object it names" affordance the `envFrom` rows already have | Demand: [derailed/k9s#745](https://github.com/derailed/k9s/issues/745) (2020, user-filed, closed unshipped in the TUI — the want is real, the shape a terminal can't cheaply offer) | S | P2 | | kubeNimbus already ships this for whole `envFrom` sources (`PodDetailTabViewModel`'s `OpenReference`-style handler, referenced in `CLAUDE.md`'s ConfigMaps/Secrets section) — this closes the one remaining gap on individual key refs, reusing the same `_openOwner` plumbing already wired for owner chips and event navigation |
| — | Service detail: a **"Targeted Pods"** section resolved from the Service's own selector | Marketing/table-stakes, confirmed via source: [Headlamp's Service `Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/service/Details.tsx) ships Targeted Pods + Endpoints + Endpoint Slices with no click required | M | P2 | | No new Core engine work — `FEAT-3`'s `LabelSelector.ForPodsOf` already lists Service among the selector-capable kinds (the plain-string-map shape, not just `LabelSelector`). The real cost: kubeNimbus has no kind-specific detail view for anything but Pod today, so this would be the *first* non-Pod object detail pane — worth a short design note on where that pattern lives before building, since it sets precedent for any future Ingress/PVC detail too |
| — | PV ↔ PVC: show a PV's bound claim (namespace/name) and a PVC's bound volume, each opening the other object | Demand: [lensapp/lens#6425](https://github.com/lensapp/lens/issues/6425), open since Oct 2022, labelled `good first issue` by Lens's own maintainers — acknowledged-easy, still unshipped four years on | S | P3 | | `spec.claimRef` (PV) and `spec.volumeName` (PVC) are plain scalar fields already present in the object JSON the app already reads for its YAML view; same open-object affordance as the row above, applied to a second pair of kinds. Modest demand — one open issue, no duplicate filings found — weighted accordingly against the two P1/P2 items above |

## What was explicitly considered and not proposed

- **A full relationship graph** (Lens Resource Map extension, 406★/31 forks,
  and its FreeLens continuation) — real, popular, but a different feature
  shape (a whole new view) than anything else in this report, and in tension
  with UI rules 1 and 10. Left for a human design decision, not proposed as a
  row.
- **Ingress → Service made clickable** — even Headlamp, the competitor that
  clearly invests in this class of feature, renders it as plain text. Clean
  negative; not proposed.
- **ConfigMap/Secret → its consumers (reverse direction)** — no filed demand
  found, and it is the one item in this report that would need a
  namespace/cluster-wide scan rather than reading a field already on the
  object in hand. Clean negative; not proposed.
- **A bespoke "explain why this pod is broken" troubleshooting assistant** —
  no demand found beyond what conditions + events already answer. Clean
  negative; not proposed, and is itself supporting evidence for the first row
  above rather than a separate item.

## Sources

- [Lens `pod-details.tsx`](https://raw.githubusercontent.com/lensapp/lens/master/packages/core/src/renderer/components/workloads-pods/pod-details.tsx)
- [Lens `pod-details-container.tsx`](https://raw.githubusercontent.com/lensapp/lens/master/packages/core/src/renderer/components/workloads-pods/pod-details-container.tsx)
- [FreeLens v1.6.0 release notes](https://github.com/freelensapp/freelens/releases/tag/v1.6.0)
- [Headlamp `pod/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/pod/Details.tsx)
- [Headlamp `service/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/service/Details.tsx)
- [Headlamp `ingress/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/ingress/Details.tsx)
- [k9s README (key bindings)](https://raw.githubusercontent.com/derailed/k9s/master/README.md)
- [k9s#174](https://github.com/derailed/k9s/issues/174) — Pending status misclassification
- [k9s#745](https://github.com/derailed/k9s/issues/745) — related-resource navigation request
- [Lens#2676](https://github.com/lensapp/lens/issues/2676) — readiness-probe warning on completed Job pods
- [Lens#4154](https://github.com/lensapp/lens/issues/4154) — requests/limits/usage in one view
- [Lens#8106](https://github.com/lensapp/lens/issues/8106) — requests/limits regression
- [FreeLens PR #971 / v1.5.0 discussion](https://github.com/freelensapp/freelens/discussions/935) — requests/limits shipped in the fork
- [Lens#6425](https://github.com/lensapp/lens/issues/6425) — PV → PVC claimRef visibility
- [Lens Resource Map extension](https://github.com/nevalla/lens-resource-map-extension)
- [FreeLens resource-map-extension](https://github.com/freelensapp/freelens-resource-map-extension)
- [Aptakube README](https://github.com/aptakube/aptakube)
- [KubeUI README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)

**Not reachable this session:** `aptakube.com/resource-view` (blocked by the
egress proxy); Headlamp's PersistentVolumeClaim `Details.tsx` (404 at the
guessed path — not force-fit into a claim); `api.github.com`'s search and
per-repo endpoints (session-scoped to this repo only, so no reaction counts
anywhere in this report).
