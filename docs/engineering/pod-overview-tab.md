# Pod detail's Overview tab (conditions, tolerations, QoS, priority, probes)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`PodDetails.cs` (Core) and pod detail's fifth tab render the structured half of
`kubectl describe pod`: the pod's `status.conditions`, its `spec.tolerations`, its
`spec.nodeSelector`, its QoS and priority class, and the selected container's
liveness/readiness/startup probes. All of it was previously reachable only by opening the
YAML editor and scrolling — a page of text to answer "is the readiness probe why this
never goes Ready?" — while three of the five comparable clients (the Lens/OpenLens/
FreeLens lineage and Headlamp, both confirmed against their own component source; k9s's
`d` in its own idiom) render exactly these fields unprompted. See
[`docs/research/2026-08-18-pod-workload-detail.md`](../../docs/research/2026-08-18-pod-workload-detail.md).

**It is structured fields, not a `describe` text clone, and that is a cost decision as
much as a design one.** kubectl's `describe` is a large Go `text/template`-shaped
formatter; none of the competitors reimplement that prose, and reproducing it here would
be a new formatter to maintain against a moving target. Reading the same `JsonElement`s
`ReadContainerSpecs` already reads costs no dependency, no reflection and nothing at AOT
time.

Eight things are load-bearing.

1. **Overview is tab index 4, appended after Usage.** `SelectedDetailTabIndex`'s existing
   values (Logs=0, Env=1, Events=2, Usage=3) are depended on by
   `ClusterTabViewModel.OpenLogs` and by the screenshot scenarios — which is exactly why
   the Usage tab was appended after Events rather than inserted where it reads best. A
   new tab goes on the end for the same reason, even though "overview" is the section
   someone would put first. Usage's `IsVisible` gate does not move it: a hidden `TabItem`
   keeps its index.
2. **No new chrome, and no picker of its own** (UI rules 1 and 10). The tab adds one
   entry to the strip that already exists and nothing to the row it shares; probes are
   container-scoped and the container strip two rows up is already their selector, which
   is the same relationship the Environment tab has with it. A section that kept showing
   the first container's probes under the second container's name would be the bug the
   log stream had before it followed the picker — `PodOverviewTests` pins that it follows.
3. **Not gated on the Advanced view.** The switch's job is "hide what you did not come
   here for"; this is what the field ships unprompted and what the item asked for as
   always-visible. Nothing here polls, fetches or watches, so it costs nothing to leave on.
4. **A pod's conditions are the opposite polarity from a node's, and unknown types get a
   third answer.** `NodeCondition.IsProblem` reads polarity off `Ready` because a node's
   other conditions are all pressure conditions; a pod's are mostly *positive* — the four
   the scheduler and kubelet set, plus `PodReadyToStartContainers` — and `DisruptionTarget`
   is the one Kubernetes defines the other way. A type on neither list comes back
   `PodConditionPolarity.Unclassified` and renders grey rather than green:
   a custom readiness gate is positive by construction but `PodResizePending` is not, and
   a false reassurance is the wrong way to be wrong for the one person reading this pane
   *because* something is wrong. `PodCondition.IsProblem` is therefore `bool?`, and an
   `Unknown` status is the same third answer.
5. **The QoS class is read, never derived.** It is a pure function of the containers'
   requests and limits and could be recomputed, but the API server has already computed it
   and the eviction path uses *its* value; a local one that disagreed would be worse than
   an empty cell. An object carrying none never went through a server, and the pane says
   so instead of inventing one.
6. **Every toleration is listed, admission's own included.** The DefaultTolerationSeconds
   plugin adds `node.kubernetes.io/not-ready` and `unreachable` (both `NoExecute`, 300s)
   to nearly every pod. They are noise right up until the moment someone is comparing one
   pod against another, at which point hiding them makes a pod that genuinely declares one
   indistinguishable from one that does not. `PodToleration.Display` is kubectl's own
   rendering, including the empty-key `op=Exists` form that tolerates everything and the
   no-effect form that must not print a dangling colon.
7. **A probe's timings are defaulted to the API server's own when the object omits them.**
   The server defaults all five on admission, so a probe missing them has never been
   through a server; printing `delay=0s timeout=1s period=10s #success=1 #failure=3` is
   what that object *would* be given and what `kubectl describe` ends up showing for it.
   Printing nothing would read as a probe with no configuration at all. The handler line
   is kubectl's shorthand too (`http-get`, `exec [...]`, `tcp-socket`, `grpc`), so a probe
   read here and one read in a terminal are visibly the same probe — and a named port is
   printed **as written**, never resolved against the container's port list, because a
   probe aimed at a port name that does not exist is precisely the failure being chased.
8. **The rebuild is signature-guarded, and the guard is on content, not on "have we
   rendered".** A watch tick on a healthy pod is almost always a status refresh that
   changes none of these fields, and rebuilding four `ItemsControl`s per tick discards
   scroll position and any half-made text selection — the same reason the Environment tab
   is guarded. Guarding on first-render instead would swallow a condition *change*, which
   is the whole reason the section exists; both mistakes were written and confirmed red
   before `PodOverviewTests` was called done.

**The demo dataset carries all of it** (demo rule 4): the report-generator pod the
pod-detail scenarios open has conditions, a node selector, a priority class, three
tolerations and three probe shapes across its two containers, and `fraud-detector` is the
opposite state — one genuinely bad `PodScheduled: False` with the scheduler's own message,
and every other section empty, which is the whole of UI rule 9 for this pane on one
object. `legacy-batch-runner` deliberately still carries none of it. The sandbox gained
the same states (`scripts/manifests/10-shop.yaml`: a `PriorityClass`, a node selector that
still schedules everywhere, a toleration, and httpGet/tcpSocket/exec probes across
shop-web's two containers) because nothing in it produced any of them before.

**Container requests and limits are the Usage tab's business, not this tab's** — see the
next section. They were already computed when this tab shipped and were reachable only
by hovering a chip, which is a different gap with its own (older, louder) evidence.
