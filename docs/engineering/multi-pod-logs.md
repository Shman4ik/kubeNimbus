# Multi-pod logs (one workload, one stream)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`WorkloadLogsTabViewModel` + `WorkloadLogsView` tail every pod a workload owns in a
single inspector pane, colour-keyed by pod. It is the job `stern` exists for, and the
thing that makes a *rolling deployment* readable: during a roll, the pod going away and
the pod coming up are one question, and reading them in two panes reads them in the
wrong order. Reached from the row context menu ("Logs (all pods)"), the list's L key (and
Shift+L, full-size), the logs icon on a hovered or selected workload row, and Ctrl/Cmd+K —
no always-visible control (UI rule 1); see [row-logs-and-maximized](row-logs-and-maximized.md). Since L1 the palette also offers a
`Logs: Deployment/<name>` row for every Deployment, StatefulSet and DaemonSet in the tab's
namespace whether or not the list is showing it, and Ctrl/Cmd+Shift+L opens the palette
already narrowed to those rows and the namespace's pods; see the command catalog section
of `CLAUDE.md`. Every route lands in `ClusterTabViewModel.OpenLogsForAsync`, which is what
keeps "which pane, and does it reuse the one already open" one decision. Only those three
controller kinds get palette rows — a ReplicaSet or Job qualifies on the same selector
evidence and the L key still reaches it, but as a palette row it would be a second row for
pods a Deployment already covers.

Eight things are load-bearing.

1. **Which pods comes from the object's own selector, resolved by the API server.**
   `LabelSelector.ForPodsOf` (Core) reads `spec.selector` in both shapes Kubernetes uses
   — the `LabelSelector` object (`matchLabels`/`matchExpressions`: Deployment,
   StatefulSet, DaemonSet, ReplicaSet, Job) and the plain string map (Service,
   ReplicationController) — and the pane runs it as a `labelSelector` list+watch. That
   is capability from the object, never from a list of kinds, exactly as
   `WorkloadActions.SupportsRestart` is: a CRD that declares a pod selector qualifies on
   the same evidence a Deployment does, and neither is named anywhere. It also settles
   the rollout case for free — a Deployment's selector names the *app*, never the
   pod-template hash, so both ReplicaSets are in scope. Resolving pods by walking the
   owner chain instead would have selected the current ReplicaSet's pods and quietly
   lost the half of the rollout the pane exists to show. `LabelSelectorTests` pins that.
2. **An empty selector is refused, not read as "everything".** Kubernetes' own semantics
   for an empty `LabelSelector` are "select all", and honouring that here would open a
   log stream against every pod in the namespace because an object happened to declare
   `selector: {}`. `ForPodsOf` returns null instead, the capability check reads that as
   "not offered", and the menu item is simply disabled. Aptakube shipped the other
   behaviour and had to withdraw it (aptakube#227). An unknown `matchExpressions`
   operator is refused for the same reason in miniature: dropping a requirement *widens*
   a selector, so a selector whose only requirement is unreadable comes back null rather
   than matching everything.
3. **The per-pod line range shares the pane's buffer budget.** The default last-200
   range still asks for `clamp(bufferLines / podCount, 25, 200)` per pod. Last-1000
   uses the same per-pod share, up to 1000. This avoids an opening burst of N × 1000
   lines evicting whole replicas' histories before a reader can see them. The 5-minute,
   1-hour, 24-hour and Everything ranges send `sinceSeconds` or no range parameter;
   combining a tail limit with them would silently cut off the interval the user chose.
   Those wider requests can fill the pane's `LogBufferLines` cap, so the pane states
   when it trims older lines. `LogBufferLines` remains a **per-pane** cap, not a per-pod
   one. The request is cancelled and reopened when the range changes, while Follow's
   state stays as it was. The demo control is disabled because its fixed July 2026
   timestamps cannot answer a relative-time query honestly. A finite snapshot that
   completes with no lines can state that the range is empty. An open follow with no
   first line cannot prove emptiness: a slow API response and a connected but quiet
   container look identical to the line iterator, so the pane says it is waiting for
   a response or output until a line arrives or the stream ends. A 750 ms timeout
   once claimed "No lines in the last 5 minutes" before the HTTP request answered;
   that was a false empty state, not a loading optimization. When Follow is off,
   the finite fetch ends in the chip state **loaded**, rather than **ended** with an
   "exited" message that would claim a healthy container stopped.
4. **Concurrency is capped at 50 streams, and the cap is stated.** N pods is N long-lived
   HTTP connections against one API server; a Deployment scaled to 400 would otherwise
   open 400 of them because someone clicked a menu item. 50 is `stern`'s own
   `--max-log-requests` default. Pods past the cap are not streamed and `CapNotice` says
   so in an `infoBar` — an aggregated pane showing 50 of 120 replicas without saying
   which number it is would be a lie by omission.
5. **The merge is two-stage, and the reason for the split is the whole design.** Every
   stream is already requested with `timestamps=true`, so each line carries the server's
   RFC3339 instant. The *opening burst* — N pods each answering with their tail at once —
   is held for a 900 ms prime window and then sorted as one block; without that, a
   three-replica pane opens with pod A's hour, then pod B's hour, then pod C's, which is
   three streams shown consecutively and fails the item's acceptance criterion outright.
   After the burst, each 100 ms flush tick sorts only what arrived within it. A **true
   k-way merge was considered and rejected**: holding a line back until every other
   stream has produced something at least as new is what a finished log file allows and a
   live tail does not — one quiet replica would stall the pane for everybody, which is
   the opposite of what a tail is for. So out-of-order arrival past a tick is possible,
   and the timestamps toggle is what settles an argument about it. `AsyncMerge` was
   considered too and is *not* used: it interleaves in arrival order, which is the one
   thing this pane must not do, and the per-source failure isolation it provides is
   already had here from one task per stream.
6. **The sort is stable and carries timestamps forward.** Two pods that logged in the
   same millisecond keep their arrival order (LINQ's `OrderBy` is a stable sort;
   `Array.Sort` is not — that is why `OrderBatch` sorts an index array through `OrderBy`),
   and a line whose leading token is not a timestamp inherits the instant of the line
   before it, so a stack trace stays attached to the line it belongs to instead of being
   flung to the top of the batch. Both are pinned by `WorkloadLogsTests`, and both were
   confirmed to turn the suite red before the tests were called done.
7. **The buffer is the streams' complete record; `LogLines` is the projection.** This is
   UI rule 13's invariant in the log pane, and it fails the same way: a line that arrives
   *while* its pod chip is toggled off must still be buffered, or re-including that pod
   shows only what it says from then on and the minutes it was hidden are gone with
   nothing to indicate anything is missing. Filtering on the way in rather than on the
   way out is the mistake; it was written into the code and confirmed to turn the suite
   red. A pod that is **deleted** likewise keeps its lines — what a terminating replica
   said last is usually why the pane was opened — and a `Reset` from the informer (a
   410-Gone relist) deliberately does **not** clear the sources, because every pod still
   there arrives again as `Added` a moment later and clearing would cancel healthy
   streams and discard a buffer no reconnect can refetch.
8. **One container per pod: the one `kubectl logs` picks with no `-c`.** The chip names
   it, so what is being tailed is stated rather than assumed. Tailing *every* container of
   a pod, colour-keyed by container, is a separate and strictly smaller feature; sources
   here are keyed by pod **and** container from the start, so that becomes a change to
   which sources are created and to nothing else — not a change to the merge, the buffer,
   the legend or the view.

**The colour palette is one set for both themes**, eight mid-tone hues in
`LogSourcePalette`, cycling past eight. Same argument as the exec terminal's palette: a
colour resolved once and held (here, a brush per line) does not follow a live theme swap,
and a half-swapped palette is worse than a single one that works in both. The colour is a
hint beside a name that is always printed, not an identifier, so an honest repeat past
eight beats inventing hues nobody can tell apart. Both themes are rendered by the
screenshot harness, which is where that claim is checked rather than asserted.
The pod chip labels explicitly use Fluent's theme foreground: inherited foreground
was nearly white on the light theme's pale blue checked chip, leaving the pod names
barely readable in the 1280 px screenshot despite their essential legend role.

**The demo cluster runs this for real** (demo rule 4): its three
`payment-service-report-generator` replicas exist precisely for this — two on the old
ReplicaSet and one on the new — and their canned streams interleave by timestamp so that
what the pane renders offline is a rolling deployment read as one stream. The pods are
found through the same `LabelSelector.Matches` a live cluster's query is rendered from,
and every line goes through the same merge, buffer and filter. Nothing about this pane is
demo-unavailable.

**Core gained `labelSelector` to make it possible.** `WatchResourceAsync` and
`ListResourceOnceAsync` take a `LabelSelector?`, and the watch engine gained an
`extraQuery` string appended to the watch request. The one trap: the selector must be
escaped identically on the list half and the watch half, or the watch reports additions
the list never seeded — `LabelSelectorQuery` is the single place that renders it.
