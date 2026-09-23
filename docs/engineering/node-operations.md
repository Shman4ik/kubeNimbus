# Node operations (detail, cordon / uncordon, drain)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ClusterClient.Nodes.cs` + `NodeActions.cs` + `NodeResources.cs` (Core) and
`NodeDetailTabViewModel` + `NodeDetailView` (App) are the node surface: what a node says
about itself, how much of it is already promised away, which pods are on it, and the
three actions that take it out of service and put it back. The read-only half was
half-present before this — `ResourceStatusSummary.SummarizeNode` already rendered
`Ready,SchedulingDisabled` and `IsMeteredKind` already covered `Node` — with no pane
behind it and no way to act on what it said.

Double-clicking a node opens the detail pane rather than its manifest (UI rule 2), and
the three actions land on FEAT-1's shared confirm strip (UI rule 17) from the row context
menu and the command palette. Nothing new is always visible.

### The read-only half

- **Allocatable, not capacity, is the denominator.** Capacity includes what
  `--system-reserved` and `--kube-reserved` hold back; a headroom figure computed against
  it overstates the free room by exactly that much, and it is the figure someone decides
  to drain on. Capacity is carried alongside so the gap is visible rather than lost.
- **Requested is the scheduler's own formula**, not a sum of every container:
  `max(sum(regular containers), max(init containers)) + spec.overhead`, with native
  sidecars (init containers whose `restartPolicy` is `Always`) counted into the running
  sum because they never exit. Summing init containers alongside the regular ones
  overstates any node running Jobs; ignoring them understates a node mid-startup. Both
  wrong answers are *plausible*, which is why `NodeResourcesTests` pins the formula in
  both directions. Terminal pods are excluded, as `kubectl describe node` excludes them.
- **Requested and the sum of the declared limits share one track, and the card says which
  part is which.** The "allocatable vs requested" card draws one `Controls/ResourceMeter`
  per resource — a hand-rolled `DrawingContext` control, same argument as `Sparkline`: the
  requested figure as the filled portion, the limits total as a lighter extent on the same
  axis with a 2px marker where it lands, and the absolute plus the percentage printed at
  the right of the row (`NodeResourceLineViewModel.LimitSummaryText`). Limits were computed
  by `NodeResources.Summarize` from the day the node pane shipped and were rendered
  nowhere, which is a strange thing to withhold: how far a node's limits oversubscribe it
  is one of the two questions the card exists to answer. It is **one track rather than two
  stacked bars** because the dock is ~300px (UI rule 10) — a second bar per resource
  triples the card's height to plot a second series on an axis that already carries it, and
  two bars are harder to compare than one, not easier. Four consequences worth keeping:
  - **The requested fill clamps at the track and the limit marker does not.** Limits past
    allocatable are ordinary overcommit — it is how most clusters are run — so a limit over
    100% pins its marker inside the track's right edge and draws it in the *warning* colour
    rather than in the marker colour, and the printed percentage goes warn with it. Silently
    clamping it would render an oversubscribed node as exactly full, which is a wrong answer
    stated confidently. `LimitPercentValue` is therefore deliberately unclamped; the meter
    is what decides how to draw a value past its end.
  - **The pods row has no limit, and renders as though the concept does not exist for it.**
    `Limit: null` is that line's normal case, not missing data, so there is no marker, no
    extent, no caption and no dangling separator. A row that silently differs in *shape*
    from the two above it is a bug; `NodeResourceLineTests` pins both halves.
  - **Overcommitted limits do not make the row read as tight.** `IsTight` stays
    `RequestedPercent > 90` — "the scheduler is nearly out of room", which is what a drain
    of the neighbouring node depends on — and overcommit gets its own flag on the limits
    figure alone. Colouring the whole row on it would say a normally run cluster is in
    trouble, which it is not until the pods actually use what they are allowed to.
  - **The marker takes the theme's high-contrast foreground, not a second accent.** Limits
    below requested is ordinary (the limits total sums only the containers that declare
    one), so the marker is routinely drawn *on top of* the accent fill, and accent-on-accent
    is invisible on the dark theme. The footnote under the card states all of this in one
    sentence, because a reader must not have to guess which part of the track is which.
- **A `*` column beside an `Auto` column of variable-width text gives every row a
  different bar length**, and that is what this card shipped with: the row was
  `ColumnDefinitions="70,*,Auto"` with the numbers in the `Auto` column, so the star column
  ended wherever each row's own text happened to stop. CPU, Memory and Pods print
  different-width figures, so the three tracks came out three different lengths — aligned
  at the left and ragged at the right — and bars of different lengths cannot be compared row
  to row, which is the whole job of a small-multiples chart. Every column but the track is a
  fixed width now. The trap generalizes to any `ItemsControl` whose rows mix a proportional
  visual with per-row text, and it is invisible from the code: it only shows up in a
  rendered screenshot, which is where it was reported from.
- **A condition's polarity is read off `Ready`**, the one condition Kubernetes defines as
  positive; everything else is a pressure condition, healthy when False. Reading it off a
  list of known-bad condition types instead would classify a cloud provider's or
  node-problem-detector's own condition as fine by default, which is the wrong way to be
  wrong.
- **Taints are shown even though the cordon flag is too**, because the scheduler enforces
  `spec.unschedulable` by way of the `node.kubernetes.io/unschedulable` taint. A cordoned
  node has both, and a reader shown only one of them wonders which is real.
- **"Pods on this node" is one field-selected list with an explicit Refresh**, not a
  second watch. `spec.nodeName=<node>` is server-side (the API server indexes it), and the
  precedent for a one-shot inside an inspector pane is pod detail's Events tab. The node
  object itself stays live: the pane tracks the same `ResourceRowViewModel` the list holds
  and re-reads conditions, taints and the cordon flag on every watch tick, the same way
  pod detail tracks its row.
- **The pod rows open logs through the cluster tab's shared route.** L, the row's
  hover icon and the Logs context item resolve the pod again before opening its logs.
  This matters because the list is a one-shot snapshot: a pod may have disappeared
  before the click, and the pane must say so rather than opening a stale object.

### Cordon, and the one honest exception to "capability from discovery"

Cordon is a one-field merge patch of `spec.unschedulable`, structurally identical to
FEAT-1's `restartedAt` patch, and uncordon writes an explicit `false` rather than a JSON
`null` — a null would *remove* the field under RFC 7386, which means the same thing to the
scheduler and is not what `kubectl uncordon` leaves behind.

The capability check names the kind, and that is deliberate rather than a shortcut.
Scale has a discovery signal (a `scale` subresource) and restart has an object signal (a
pod template to stamp); cordon has **neither**. `spec.unschedulable` is a field of the core
`v1.Node` schema, discovery says nothing about it, and an uncordoned node omits the field
entirely — so "does the object have the field" answers false for exactly the nodes you
would want to cordon. `NodeActions.SupportsCordon` therefore tests the kind *and* asks
discovery the half it can answer: does this server say nodes are patchable. Drain adds the
signal there *is* one for — whether the server serves `pods/eviction` — so a cluster
without the Eviction API never sees the menu item at all.

Cordon and uncordon are two commands in **one menu slot**: the menu shows whichever the
node's current state makes meaningful. That is UI rule 11's "a control pair where one half
is always disabled is one control", settled by the port-forward pane's Start/Stop, and it
is why the menu still never shifts — exactly one of the pair is ever present.

### Drain: what it does, and what it refuses

There is no `k8s.io/kubectl/pkg/drain` to import here and `KubernetesClient.Aot` ships the
eviction primitive and no drain helper, so the loop is ours. Every *decision* it makes is
therefore pure and tested (`NodeActions.Plan`), and only the HTTP is in `ClusterClient`.
The classification is kubectl's own filter order, and each entry is here because skipping
it is a known way to break a cluster — the two marked below are the open silent-data-loss
bug in a comparable CNCF client ([headlamp#7268](https://github.com/kubernetes-sigs/headlamp/issues/7268)):

| Pod | What the drain does | Why |
|---|---|---|
| Already terminating | waited for, not evicted again | its 404 would read as a failure, and the node is not drained until it is gone |
| **Mirror (static) pod** | skipped, always | the kubelet owns it from a file on disk and recreates it seconds later |
| Succeeded / Failed | skipped | nothing is running; only a record is left |
| **DaemonSet-owned** | skipped, and named in the plan | its controller ignores cordon; `kubectl` requires `--ignore-daemonsets`, and a gate whose only possible answer is yes is worse than a sentence saying what was left behind |
| **No controller** | **refused** unless "Evict unmanaged pods" | nothing recreates it: draining destroys the workload (`kubectl --force`) |
| **`emptyDir` volume** | **refused** unless "Delete emptyDir data" | node-local storage with no copy anywhere, deleted with the pod (`kubectl --delete-emptydir-data`) |

Seven things are load-bearing:

1. **The plan is computed and shown before anything is evicted, and a plan with refusals
   does not run.** kubectl refuses the same way — it names every problem pod before it
   touches one. Half a drain that then stops on a question is worse than the question
   asked first. Ticking either option re-plans from the pod list already read, so the
   refusal it clears disappears in front of you rather than on confirm.
2. **Two options, not five.** `--ignore-daemonsets` has one possible answer and the plan
   states what it left behind instead; `--disable-eviction` bypasses PodDisruptionBudgets
   and this app will not offer that as a checkbox; `--timeout` is replaced by a drain you
   can watch and stop. What is left are the two that authorize destroying something which
   does not come back, and both are off by default.
3. **The drain streams.** `DrainNodeAsync` is an `IAsyncEnumerable<DrainProgress>` — one
   event per thing that happens — because its duration is not bounded by anything this app
   controls. A **429 from a PodDisruptionBudget is correct behaviour**, can last minutes or
   forever, and is indistinguishable from a hung window unless the pane says "blocked by a
   PodDisruptionBudget, still retrying". It gets its own per-pod row and its own colour
   (warn, not error). A 403 is separated from it deliberately: retrying will not fix that
   one, so it is recorded as failed and not asked again.
4. **The eviction loop polls, and that is a stated exception to hard rule 2.** It re-lists
   the node's pods every 2s between passes. The loop's question is "is this specific set of
   pods gone yet", which has a natural end (the set empties), it is scoped to the drain's
   own `CancellationToken`, and re-listing is also how it notices a pod that appeared
   *after* it started — a watch seeded once would not. It is what `kubectl drain`'s own
   `waitForDelete` does. This is the second documented poll in the app, after the metrics
   API.
5. **Cordon happens first, always.** Evicting from a node that still accepts work is a way
   to have the scheduler put the pod back on the same node.
6. **A partial drain is a designed state, not an accident — this is the constraint that
   cannot be engineered away.** The loop runs in the desktop app's own process: closing the
   tab or quitting stops it, leaving the node cordoned with some pods moved and some not.
   So (a) the confirm sentence says exactly that *before* anything starts, which is the one
   thing someone must know; (b) the strip cannot be dismissed while a drain runs — Cancel
   is replaced by **Stop draining**, because "Cancel" over a loop that is already evicting
   reads as undo and there is no undo; (c) stopping reports how many pods moved, that the
   node is still cordoned, and the two ways out (run it again, or uncordon and leave it as
   it is); and (d) `ClusterTabViewModel.DisposeAsync` cancels the loop explicitly rather
   than leaving a task running against a disposed client. `cluster-tab-node-drain-stopped`
   is that state rendered.
7. **One drain at a time, enforced by the single-slot strip.** `ArmRowAction` refuses to
   replace an action that is busy or draining: re-arming over a running loop would leave it
   evicting with nothing on screen reporting it. Portainer reached the same rule from the
   other direction in [portainer#4006](https://github.com/portainer/portainer/issues/4006)
   — a drain should be issued to one node at a time.

**Eviction is posted as `policy/v1`**, which the API server has served since 1.22 and which
`kubectl` itself sends; `policy/v1beta1` was removed in 1.25. A server too old for it
answers with its own message, which the strip prints verbatim rather than this app guessing
a second version to retry with. And because discovery gates the whole feature on
`pods/eviction` existing, a 404 from that endpoint can only be about the pod — which is why
it is read as "already gone", the outcome the caller wanted.

**The demo cluster plans for real and refuses to evict** (demo rules 4 and 5). The
classification is pure and the shipped dataset has pods on nodes, so the plan, the two
refusals and the whole strip render offline exactly as they would against a cluster; only
the eviction has no honest stand-in, and `RowActionViewModel.IsDemo` says so in place with
the confirm disabled. The demo catalog's Pod descriptor therefore declares an `eviction`
subresource for the same reason its Deployment declares `scale`: without it the demo would
teach that kubeNimbus cannot drain a node, rather than that *this* cluster cannot.

**Both states of the resource card are in the shipped data** (demo rule 4). `demo-worker-1`
is the ordinary shape — some pods declare limits, most do not, so the limits total lands
*below* requested and the marker sits inside the fill — and `demo-worker-2` is
overcommitted in both CPU and memory (109% of allocatable each), which is what renders the
warn marker pinned at the track's end. `redis-cache-0` and `notification-dispatcher` carry
the generous limits that produce it. The sandbox had nothing that oversubscribed a node
either, so `shop-api` in `scripts/manifests/10-shop.yaml` now declares a limit far above
its request; the scheduler never reads limits, so it schedules exactly as it did before.

**Two defects were found by looking at the rendered strip and are worth remembering.** A
compiled binding to a **method group** (`{Binding DrainPlan.Summary}` against
`string Summary()`) renders the delegate's type name — ``System.Func`1[System.String]`` — with
no error anywhere; `DrainPlan.Summary` is a property now. And `RowActionViewModel`'s target
sentence tested its namespace for `null` where `ResourceRowViewModel.Namespace` is a
non-nullable string, so every cluster-scoped object read "`Node/demo-worker-1 in `". That
second one is pre-existing and applied to deleting a PersistentVolume or a Namespace too.

**Not shipped, deliberately:** node shell (that is `kubectl debug node/`, a different
feature), `--disable-eviction`, a `--grace-period` control (the option exists in
`DrainOptions` and nothing sets it — a pod's shutdown window is a property of the app, not
of whoever is draining), multi-node drain, and node labels/taints editing. The YAML editor
already reaches all of the last one.
