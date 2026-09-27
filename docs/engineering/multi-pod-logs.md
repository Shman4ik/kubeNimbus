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
   first line cannot prove emptiness. Core's `responseReady` callback fires only after
   successful HTTP headers, so a slow API response stays in the loading state. After
   headers and a short grace for the opening body burst, a quiet follow says "No lines
   received yet … following new output", which states what was observed without claiming
   kubelet history is empty. The aggregated pane waits for each active pod's response and
   names the count still pending; a failed source is a partial result. The first attempt
   used a 750 ms timer from request start and claimed "No lines in the last 5 minutes"
   before HTTP answered; removing the timer without adding a response signal then left
   a healthy quiet follow in "Waiting for log response or output" forever. Both were
   false state transitions, caught during independent verification. When Follow is off,
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
8. **One container per pod: the one `kubectl logs` picks with no `-c`** — the pod's
   `kubectl.kubernetes.io/default-container` when it names one it has, else its first
   container (`PodDetails.DefaultContainer`, FEAT-38; see
   [log-pane-reading](log-pane-reading.md)). The chip names it, so what is being tailed is
   stated rather than assumed. Tailing *every* container of a pod, colour-keyed by
   container (FEAT-35), was looked at in the logs bundle and **not built**: the
   `LogSourceViewModel` carries pod and container, but the pane's own state —
   `_sourcesByPod`, `_streamsByPod`, `_respondedPods`, `_latestPods` — is keyed by pod name
   alone, so "a change to which sources are created and nothing else" was not true of this
   code, and the demand behind the row is ambiguous (k9s#827 may mean pods). A sidecar is
   one click away on pod detail's container strip.

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
demo-unavailable. A demo pod whose canned stream is empty (the unschedulable
`fraud-detector`) ends through `LogStreamEnd.DescribePod` on its own dataset object, the
sentence a live cluster's pane reads from the pod — it used to end "the sample stream has
finished" over zero lines, a chip reading "ended" beside a body that disagreed (ENG-45).

The search box, Levels, Clear, the UTC chip and the remembered display toggles are the same
as pod detail's and are described once, in [log-pane-reading](log-pane-reading.md).

**Core gained `labelSelector` to make it possible.** `WatchResourceAsync` and
`ListResourceOnceAsync` take a `LabelSelector?`, and the watch engine gained an
`extraQuery` string appended to the watch request. The one trap: the selector must be
escaped identically on the list half and the watch half, or the watch reports additions
the list never seeded — `LabelSelectorQuery` is the single place that renders it.

**The pane is also the Applications page's log view**, through `WorkloadLogsOptions` rather
than a second implementation ([applications-mode](applications-mode.md)): several selectors in
one namespace (an Argo app of several workloads is one stream), a focus pod (the page's Pods
list chooses which pod is included; every pod keeps streaming into the buffer, so "All pods"
loses nothing), the run before (`previous=true`, never followed — the API server refuses
follow with previous), an **Errors only** toggle over the same severity classes the lines are
coloured with, and a `Footer` line the page ends a crashed run with. Embedded, the pod strip
is hidden (the page's own list is the selector) and Errors only is shown; in the inspector
dock the pane is exactly as before.


## When a follow ends (both log panes)

A followed stream that the API server closes without an error used to be reported as
"Stream ended — app exited." in the single-pod pane and "app exited." on a multi-pod
source, on faith. That is true when the container exits, and false when a load balancer,
a proxy or the API server itself drops the connection — AKS and EKS load balancers close
idle connections after a few minutes, which is exactly how long a quiet service goes
without logging. It was also said about a container that had never started (ENG-40).

`LogStreamEnd` reads the pod instead. The pane shows "checking whether app is still
running…", waits `SettleDelay` (two seconds — the kubelet closes the stream as the
container exits and reports the exit on a later status sync, so an immediate read finds
the ended run still `running` and blames the connection), then GETs the pod and compares
the container's run with the one the follow started on (`PodDetails.ContainerRunOf`, from
the watched pod at stream start):

- same container id, still running — the connection closed, not the container; said as a
  warning, with Follow as the way back;
- a different container id — a restart; Follow picks up the new run, Previous the old one;
- terminated — "exited" with the kubelet's reason and exit code;
- waiting with no container id — it has not started yet; waiting with one — not running,
  with the reason (`CrashLoopBackOff`);
- no status for the container and no node — the pod is not scheduled yet, with the
  PodScheduled reason (`Unschedulable`). An unscheduled pod's follow request is an
  immediate 204, so this is what such a stream's ending is; the multi-pod pane's chip then
  reads **not started** rather than "ended", the body repeats the chips' sentence, and the
  source is re-opened once the pod runs (ENG-45, [log-pane-reading](log-pane-reading.md));
- 404 — the pod is gone; any other read failure — said, with the server's first line.

It does **not** reconnect. The owner's usage is open, look, close, and a silent reconnect
would hide the one fact worth knowing — that lines between the drop and the resume may be
missing. Restarting the stream is one press of Follow, and the sentence says so.
`LogStreamEndTests` pins the verdicts; the real status shapes (running, waiting
CrashLoopBackOff, 404) were read from the sandbox. Not observed: a real idle-timeout drop.

**The one exception: a follow opened before its container started.** A real kubelet
answers a follow requested between a container being *created* and *started* with a 200
and an empty body that closes at once — it ends a follow at the end of the log of a
container that is not running, and this one has not run yet. The multi-pod pane opens a
stream the instant its watch reports a pod, so during a rollout a new replica lands in that
window (observed, with its empty 200, in one of about eight `Live/WorkloadLogsLiveTests` runs against k3s v1.33; the other
runs saw the server's `is waiting to start: ContainerCreating` and `is not available` 400s,
which the pane already retries on the pod's next Modified). Left alone, the new pod sat in
the strip with no lines, blamed on a dropped connection, and no later event restarted it.
So when the stream that ended was opened on a container that was not running
(`LogStreamEnd.StartedAfterRequest`: not running at the start, running now), the
multi-pod pane follows it again instead of printing a verdict. Nothing can have been lost —
the follow never had a run to lose lines from, and the new one backfills with the pod's
tail. It cannot loop: the restarted follow starts from a running container. The single-pod
pane is unchanged; it is opened on a pod someone chose, not the instant one appears.
`LogStreamStartRaceTests` (App) pins the predicate.
